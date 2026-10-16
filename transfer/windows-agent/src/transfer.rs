use std::{
    path::{Path, PathBuf},
    sync::Arc,
    time::Duration,
};

use anyhow::{Context, Result, anyhow};
use reqwest::{
    StatusCode,
    multipart::{Form, Part},
};
use serde::Deserialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tokio::{
    fs::{self, File, OpenOptions},
    io::{AsyncReadExt, AsyncSeekExt, AsyncWriteExt, SeekFrom},
    time::sleep,
};
use tokio_util::io::ReaderStream;
use tracing::{debug, warn};

use muying_transfer_common::protocol::{
    BatchUploadItem, BatchUploadManifest, BatchUploadResponse, ByteRange, StagedObjectReceipt,
    UploadCommitResponse, UploadInitRequest, UploadInitResponse, UploadManifest,
};

use crate::{
    AgentEvent, AgentState, api,
    queue::{SyncBaseline, TaskRecord, TaskState},
};

const MAX_BATCH_ITEM_BYTES: u64 = 16 * 1024 * 1024;
const MAX_BATCH_TOTAL_BYTES: u64 = 256 * 1024 * 1024;
const MAX_CLAIMED_BATCH_ITEMS: usize = 64;
const MAX_AUTO_RETRIES: u32 = 5;
const FILE_STABLE_CHECK_MS: u64 = 400;

pub async fn run_workers(state: Arc<AgentState>) {
    let _ = state.queue.recover_interrupted();
    loop {
        if state
            .shutting_down
            .load(std::sync::atomic::Ordering::Acquire)
        {
            break;
        }
        let worker_gate = state.worker_gate.lock().await;
        let config_guard = state.config.read().await;
        if config_guard.is_none() {
            drop(config_guard);
            drop(worker_gate);
            tokio::select! {
                _ = sleep(Duration::from_millis(500)) => {},
                _ = state.shutdown_notify.notified() => {},
            }
            continue;
        }
        let available_sync_roots: Vec<String> = config_guard
            .as_ref()
            .expect("agent configuration checked above")
            .sync_roots
            .iter()
            .map(|root| root.root_id.clone())
            .collect();
        let next = match state.queue.claim_next(&available_sync_roots) {
            Ok(next) => next,
            Err(error) => {
                drop(config_guard);
                drop(worker_gate);
                warn!(%error,"could not claim a transfer task");
                tokio::select! {
                    _ = sleep(Duration::from_secs(1)) => {},
                    _ = state.shutdown_notify.notified() => {},
                }
                continue;
            }
        };
        let Some(first) = next else {
            drop(config_guard);
            drop(worker_gate);
            tokio::select! {
                _ = sleep(Duration::from_millis(350)) => {},
                _ = state.shutdown_notify.notified() => {},
            }
            continue;
        };
        state.worker_status.send_replace(true);
        drop(config_guard);
        drop(worker_gate);
        if let Some(batch_id) = first.batch_parent_id.clone() {
            let mut tasks = vec![first];
            while tasks.len() < MAX_CLAIMED_BATCH_ITEMS {
                if state
                    .shutting_down
                    .load(std::sync::atomic::Ordering::Acquire)
                    || state.config.read().await.is_none()
                {
                    break;
                }
                match state.queue.claim_batch_sibling(&batch_id) {
                    Ok(Some(task)) => tasks.push(task),
                    Ok(None) => break,
                    Err(error) => {
                        warn!(%error,"could not claim a batch sibling");
                        break;
                    }
                }
            }
            process_batch(&state, batch_id, tasks).await;
        } else {
            process_task(&state, first).await;
        }
        state.worker_status.send_replace(false);
    }
    state.worker_status.send_replace(false);
}

async fn process_task(state: &Arc<AgentState>, mut task: TaskRecord) {
    if let Err(error) = ensure_task_active(state, &task.snapshot.operation_id).await {
        record_failure(state, &task, &error).await;
        return;
    }
    let kind = task.snapshot.kind.as_str();
    let result = match kind {
        "upload" => upload_one(state, &mut task).await.map(|_| ()),
        "download" => download_one(state, &mut task).await.map(|_| ()),
        "delete" => delete_one(state, &task).await,
        other => Err(anyhow!("unsupported task kind: {other}")),
    };
    if let Err(error) = result {
        record_failure(state, &task, &error).await;
    }
}

async fn delete_one(state: &Arc<AgentState>, task: &TaskRecord) -> Result<()> {
    ensure_task_active(state, &task.snapshot.operation_id).await?;
    let config = state
        .config
        .read()
        .await
        .clone()
        .ok_or_else(|| anyhow!("agent is logged out"))?;
    let result = api::delete_file(&state.api, &config, task).await?;
    state.queue.update_worker_state(
        &task.snapshot.operation_id,
        TaskState::Completed,
        None,
        None,
        None,
        false,
        0,
    )?;
    if let (Some(root_id), Some(logical_path)) = (&task.root_id, &task.snapshot.logical_path) {
        if let Some(mut baseline) = state.queue.baseline(root_id, logical_path)? {
            baseline.deleted = true;
            baseline.version = result.version.unwrap_or(baseline.version + 1);
            state.queue.upsert_baseline(&baseline)?;
        }
        state
            .queue
            .remove_local_fingerprint(root_id, logical_path)?;
    }
    state.emit(AgentEvent {
        event: "task_state".into(),
        seq: 0,
        task_id: Some(task.snapshot.task_id.clone()),
        operation_id: Some(task.snapshot.operation_id.clone()),
        payload: json!({"state":"completed","task":task.snapshot}),
    });
    Ok(())
}

async fn process_batch(state: &Arc<AgentState>, batch_id: String, mut tasks: Vec<TaskRecord>) {
    if tasks.is_empty() {
        return;
    }
    tasks.sort_by_key(|task| task.snapshot.created_at);
    let mut small = Vec::new();
    let mut total = 0_u64;
    for task in tasks {
        if let Err(error) = ensure_task_active(state, &task.snapshot.operation_id).await {
            record_failure(state, &task, &error).await;
            continue;
        }
        let path = match task.source_path.as_deref() {
            Some(path) => path,
            None => {
                record_failure(state, &task, &anyhow!("batch upload has no source path")).await;
                continue;
            }
        };
        if task.source == "sync" {
            if let Err(error) = reject_reparse_source_ancestry(Path::new(path)) {
                record_failure(state, &task, &error).await;
                continue;
            }
        }
        match hash_stable_file(Path::new(path)).await {
            Ok((size, hash, _))
                if size <= MAX_BATCH_ITEM_BYTES
                    && total.saturating_add(size) <= MAX_BATCH_TOTAL_BYTES =>
            {
                let _ = state
                    .queue
                    .set_bytes_total(&task.snapshot.operation_id, size);
                total = total.saturating_add(size);
                small.push((task, size, hash));
            }
            Ok(_) => {
                if let Err(error) = upload_one(state, &mut task.clone()).await {
                    record_failure(state, &task, &error).await;
                }
            }
            Err(error) => record_failure(state, &task, &error).await,
        }
    }
    if small.is_empty() {
        return;
    }
    if small.len() == 1 {
        let (task, _, _) = small.remove(0);
        if let Err(error) = upload_one(state, &mut task.clone()).await {
            record_failure(state, &task, &error).await;
        }
        return;
    }
    if let Err(error) = upload_small_batch(state, &batch_id, &mut small).await {
        // The batch route is per-item and idempotent. A transport-level failure leaves each
        // operation retryable; the daemon/client can be queried again with the same IDs.
        for (task, _, _) in &small {
            record_failure(state, task, &error).await;
        }
    }
    emit_batch_progress(state, &batch_id).await;
}

async fn upload_one(
    state: &Arc<AgentState>,
    task: &mut TaskRecord,
) -> Result<UploadCommitResponse> {
    ensure_task_active(state, &task.snapshot.operation_id).await?;
    let config = state
        .config
        .read()
        .await
        .clone()
        .ok_or_else(|| anyhow!("agent is logged out"))?;
    let source = PathBuf::from(
        task.source_path
            .as_deref()
            .ok_or_else(|| anyhow!("upload task has no source path"))?,
    );
    if task.source == "sync" {
        reject_reparse_source_ancestry(&source)?;
    }
    state_event(state, task, TaskState::WaitingStable, None, None);
    let (size, sha256, source_modified_ns) = hash_stable_file(&source).await?;
    ensure_task_active(state, &task.snapshot.operation_id).await?;
    state
        .queue
        .set_bytes_total(&task.snapshot.operation_id, size)?;
    state_event(state, task, TaskState::Hashing, Some(0), None);
    let mut current = task.clone();
    current.snapshot.bytes_total = Some(size);

    if current.ticket.is_empty() || current.object_id.is_none() || current.object_kind.is_none() {
        let intent = api::begin_upload(&state.api, &config, &current, size, &sha256).await?;
        if let Err(error) = ensure_task_active(state, &current.snapshot.operation_id).await {
            let _ = api::abort_upload(
                &state.api,
                &config,
                &current.snapshot.operation_id,
                &intent.state,
            )
            .await;
            return Err(error);
        }
        state
            .queue
            .set_control_state(&current.snapshot.operation_id, &intent.state)?;
        current.object_id = Some(intent.object_id.clone());
        current.object_kind = Some(intent.object_kind.clone());
        current.revision_id = Some(intent.revision_id.clone());
        current.file_id = intent.file_id.clone();
        current.expected_version = Some(intent.expected_version);
        current.transfer_url = intent.transfer_url.clone();
        current.ticket = intent.ticket.clone();
        state.queue.update_authorization(
            &current.snapshot.operation_id,
            current.file_id.as_deref(),
            current.revision_id.as_deref().unwrap(),
            current.object_id.as_deref().unwrap(),
            current.object_kind.as_deref().unwrap(),
            intent.expected_version,
            &current.transfer_url,
            &current.ticket,
        )?;
        state_event(state, &current, TaskState::Transferring, Some(0), None);
    }
    let object_id = current
        .object_id
        .clone()
        .ok_or_else(|| anyhow!("upload intent did not provide object_id"))?;
    let mut transfer_url = current.transfer_url.trim_end_matches('/').to_owned();
    if transfer_url.is_empty() {
        anyhow::bail!("upload intent did not provide transfer_url");
    }
    let manifest = UploadManifest {
        op_id: current.snapshot.operation_id.clone(),
        object_id: object_id.clone(),
        object_kind: current
            .object_kind
            .clone()
            .ok_or_else(|| anyhow!("upload intent did not provide object_kind"))?,
        expected_version: current.expected_version.unwrap_or(0),
        size,
        sha256: sha256.clone(),
        media_type: current.media_type.clone(),
        logical_path: current.snapshot.logical_path.clone(),
    };
    let mut init_url = if transfer_url.ends_with("/uploads") {
        transfer_url.clone()
    } else {
        format!("{transfer_url}/uploads")
    };
    ensure_task_active(state, &current.snapshot.operation_id).await?;
    let response = state
        .api
        .post(&init_url)
        .json(&UploadInitRequest {
            ticket: current.ticket.clone(),
            manifest: manifest.clone(),
        })
        .send()
        .await
        .context("initialize upload on transfer service")?;
    let response = if response.status() == StatusCode::UNAUTHORIZED {
        let intent = api::begin_upload(&state.api, &config, &current, size, &sha256).await?;
        if current
            .object_id
            .as_deref()
            .is_some_and(|object_id| object_id != intent.object_id)
            || current
                .object_kind
                .as_deref()
                .is_some_and(|kind| kind != intent.object_kind)
        {
            anyhow::bail!("reissued upload ticket changed the immutable object identity");
        }
        current.object_id = Some(intent.object_id.clone());
        current.object_kind = Some(intent.object_kind.clone());
        current.revision_id = Some(intent.revision_id.clone());
        current.file_id = intent.file_id.clone();
        current.expected_version = Some(intent.expected_version);
        current.transfer_url = intent.transfer_url.clone();
        current.ticket = intent.ticket.clone();
        transfer_url = current.transfer_url.trim_end_matches('/').to_owned();
        init_url = if transfer_url.ends_with("/uploads") {
            transfer_url.clone()
        } else {
            format!("{transfer_url}/uploads")
        };
        state
            .queue
            .set_control_state(&current.snapshot.operation_id, &intent.state)?;
        state.queue.update_authorization(
            &current.snapshot.operation_id,
            current.file_id.as_deref(),
            &intent.revision_id,
            &intent.object_id,
            &intent.object_kind,
            intent.expected_version,
            &intent.transfer_url,
            &intent.ticket,
        )?;
        state
            .api
            .post(&init_url)
            .json(&UploadInitRequest {
                ticket: current.ticket.clone(),
                manifest: manifest.clone(),
            })
            .send()
            .await
            .context("retry upload initialization with a renewed ticket")?
    } else {
        response
    };
    let init: UploadInitResponse = decode_response(response).await?;
    if init.chunk_size == 0 || init.chunk_size > 64 * 1024 * 1024 {
        anyhow::bail!("transfer service returned an invalid chunk size");
    }
    state.queue.save_checkpoint(
        &current.snapshot.operation_id,
        &init.upload_id,
        init.chunk_size,
        init.received_bytes,
        &serde_json::to_string(&init.missing_ranges)?,
    )?;
    state.queue.update_worker_state(
        &current.snapshot.operation_id,
        TaskState::Transferring,
        Some(init.received_bytes),
        None,
        None,
        false,
        0,
    )?;
    state_event(
        state,
        &current,
        TaskState::Transferring,
        Some(init.received_bytes),
        None,
    );

    let mut missing = init.missing_ranges;
    let mut sent = init.received_bytes;
    let mut file = File::open(&source)
        .await
        .with_context(|| format!("open upload source {}", source.display()))?;
    for range in missing.clone() {
        let mut start = range.start;
        while start <= range.end {
            ensure_task_active(state, &current.snapshot.operation_id).await?;
            let remaining = (range.end - start + 1).min(init.chunk_size);
            file.seek(SeekFrom::Start(start)).await?;
            let mut chunk = vec![0_u8; remaining as usize];
            file.read_exact(&mut chunk).await?;
            let chunk_sha = hex::encode(Sha256::digest(&chunk));
            let end = start + remaining - 1;
            let chunk_url = format!(
                "{}/{}/chunks",
                transfer_url.trim_end_matches("/uploads"),
                init.upload_id
            );
            let response = state
                .api
                .put(&chunk_url)
                .header("X-Transfer-Ticket", &current.ticket)
                .header("Content-Range", format!("bytes {start}-{end}/{size}"))
                .header("X-Chunk-SHA256", chunk_sha)
                .header(reqwest::header::CONTENT_TYPE, "application/octet-stream")
                .body(chunk)
                .send()
                .await
                .context("upload a file chunk")?;
            ensure_success(response).await?;
            sent = sent.saturating_add(remaining).min(size);
            subtract_range(&mut missing, start, end);
            let ranges_json = serde_json::to_string(&missing)?;
            state.queue.save_checkpoint(
                &current.snapshot.operation_id,
                &init.upload_id,
                init.chunk_size,
                sent,
                &ranges_json,
            )?;
            state.queue.update_worker_state(
                &current.snapshot.operation_id,
                TaskState::Transferring,
                Some(sent),
                None,
                None,
                false,
                0,
            )?;
            state_event(state, &current, TaskState::Transferring, Some(sent), None);
            start = end + 1;
        }
    }
    state.queue.update_worker_state(
        &current.snapshot.operation_id,
        TaskState::Verifying,
        Some(size),
        None,
        None,
        false,
        0,
    )?;
    state_event(state, &current, TaskState::Verifying, Some(size), None);
    ensure_task_active(state, &current.snapshot.operation_id).await?;
    if !state
        .queue
        .mark_commit_started(&current.snapshot.operation_id)?
    {
        anyhow::bail!("task_cancelled");
    }
    state_event(state, &current, TaskState::Committing, Some(size), None);
    if let Err(error) = ensure_task_active(state, &current.snapshot.operation_id).await {
        let _ = state
            .queue
            .clear_commit_started_before_send(&current.snapshot.operation_id);
        return Err(error);
    }
    let commit_url = format!(
        "{}/{}/commit",
        transfer_url.trim_end_matches("/uploads"),
        init.upload_id
    );
    let response = state
        .api
        .post(&commit_url)
        .header("X-Transfer-Ticket", &current.ticket)
        .send()
        .await
        .context("commit transfer upload")?;
    let staged: UploadCommitResponse = decode_response(response).await?;
    if staged.op_id != current.snapshot.operation_id
        || staged.object_id != object_id
        || staged.size != size
        || !staged.sha256.eq_ignore_ascii_case(&sha256)
    {
        state.queue.clear_commit_started_after_rejection(&current.snapshot.operation_id)?;
        anyhow::bail!("transfer service commit receipt did not match the queued upload");
    }
    ensure_task_active(state, &current.snapshot.operation_id).await?;
    let committed = api::commit_upload(
        &state.api,
        &config,
        &current.snapshot.operation_id,
        &staged.receipt,
    )
    .await?;
    state
        .queue
        .delete_checkpoint(&current.snapshot.operation_id)?;
    state.queue.update_worker_state(
        &current.snapshot.operation_id,
        TaskState::Completed,
        Some(size),
        None,
        None,
        false,
        0,
    )?;
    current.file_id = Some(committed.file_id.clone());
    current.revision_id = Some(committed.revision_id.clone());
    current.expected_version = Some(committed.version);
    current.snapshot.file_id = current.file_id.clone();
    current.snapshot.revision_id = current.revision_id.clone();
    current.snapshot.expected_version = Some(committed.version);
    current.snapshot.version = Some(committed.version);
    state
        .queue
        .set_version(&current.snapshot.operation_id, committed.version)?;
    if let (Some(root_id), Some(scope_id), Some(path)) = (
        &current.root_id,
        &current.scope_id,
        &current.snapshot.logical_path,
    ) {
        let baseline = SyncBaseline {
            root_id: root_id.clone(),
            logical_path: path.clone(),
            scope_id: scope_id.clone(),
            project_id: current.project_id.clone(),
            file_id: committed.file_id.clone(),
            revision_id: committed.revision_id.clone(),
            version: committed.version,
            size,
            sha256: sha256.clone(),
            deleted: false,
            conflicted: false,
        };
        state.queue.upsert_baseline(&baseline)?;
        state
            .queue
            .set_local_fingerprint(root_id, path, size, source_modified_ns)?;
    }
    state_event(state, &current, TaskState::Completed, Some(size), None);
    Ok(staged)
}

async fn upload_small_batch(
    state: &Arc<AgentState>,
    batch_id: &str,
    tasks: &mut Vec<(TaskRecord, u64, String)>,
) -> Result<()> {
    let claimed = std::mem::take(tasks);
    for item in claimed {
        match ensure_task_active(state, &item.0.snapshot.operation_id).await {
            Ok(()) => tasks.push(item),
            Err(error) => record_failure(state, &item.0, &error).await,
        }
    }
    if tasks.is_empty() {
        return Ok(());
    }
    let config = state
        .config
        .read()
        .await
        .clone()
        .ok_or_else(|| anyhow!("agent is logged out"))?;
    let tasks_for_api: Vec<_> = tasks.iter().map(|(task, _, _)| task.clone()).collect();
    let sizes_hashes: Vec<_> = tasks
        .iter()
        .map(|(_, size, hash)| (*size, hash.clone()))
        .collect();
    for (task, _, _) in tasks.iter() {
        state.queue.update_worker_state(
            &task.snapshot.operation_id,
            TaskState::Hashing,
            None,
            None,
            None,
            false,
            0,
        )?;
    }
    let directories: Value = state
        .queue
        .batch_directories_json(batch_id)
        .ok()
        .flatten()
        .and_then(|json| serde_json::from_str(&json).ok())
        .unwrap_or_else(|| json!([]));
    let intent = api::begin_batch_uploads(
        &state.api,
        &config,
        &tasks_for_api,
        &sizes_hashes,
        directories.clone(),
    )
    .await?;
    let intent_map: std::collections::HashMap<_, _> = intent
        .items
        .into_iter()
        .map(|item| (item.operation_id.clone(), item))
        .collect();
    let errors: std::collections::HashMap<_, _> = intent
        .errors
        .into_iter()
        .map(|error| (error.operation_id.clone(), error))
        .collect();
    let mut batch_items = Vec::new();
    for (task, size, sha256) in tasks.iter_mut() {
        let op = &task.snapshot.operation_id;
        let Some(intent) = intent_map.get(op) else {
            if let Some(error) = errors.get(op) {
                state.queue.update_worker_state(
                    op,
                    TaskState::Failed,
                    None,
                    Some(&error.code),
                    Some(&error.message),
                    false,
                    0,
                )?;
                continue;
            }
            anyhow::bail!("batch control API response omitted operation {op}");
        };
        if let Err(error) = ensure_task_active(state, op).await {
            let _ = api::abort_upload(&state.api, &config, op, &intent.state).await;
            record_failure(state, task, &error).await;
            continue;
        }
        task.object_id = Some(intent.object_id.clone());
        task.object_kind = Some(intent.object_kind.clone());
        state.queue.set_control_state(op, &intent.state)?;
        task.file_id = intent.file_id.clone();
        task.revision_id = Some(intent.revision_id.clone());
        task.expected_version = Some(intent.expected_version);
        task.ticket = intent.ticket.clone();
        task.transfer_url = intent.transfer_url.clone();
        state.queue.update_authorization(
            op,
            task.file_id.as_deref(),
            &intent.revision_id,
            &intent.object_id,
            &intent.object_kind,
            intent.expected_version,
            &intent.transfer_url,
            &intent.ticket,
        )?;
        let manifest = UploadManifest {
            op_id: op.clone(),
            object_id: intent.object_id.clone(),
            object_kind: intent.object_kind.clone(),
            expected_version: intent.expected_version,
            size: *size,
            sha256: sha256.clone(),
            media_type: task.media_type.clone(),
            logical_path: task.snapshot.logical_path.clone(),
        };
        batch_items.push(BatchUploadItem {
            ticket: intent.ticket.clone(),
            manifest,
        });
    }
    if batch_items.is_empty() {
        return Ok(());
    }
    let transfer_url = tasks
        .iter()
        .find(|(task, _, _)| {
            batch_items
                .iter()
                .any(|item| item.manifest.op_id == task.snapshot.operation_id)
        })
        .map(|(task, _, _)| task.transfer_url.clone())
        .ok_or_else(|| anyhow!("batch has no authorized transfer URL"))?;
    let upload_url = if transfer_url.ends_with("/uploads/batch") {
        transfer_url.clone()
    } else if transfer_url.ends_with("/uploads") {
        format!("{transfer_url}/batch")
    } else {
        format!("{}/uploads/batch", transfer_url.trim_end_matches('/'))
    };
    let batch_manifest = BatchUploadManifest {
        items: batch_items,
        directories: serde_json::from_value(directories).unwrap_or_default(),
    };
    let manifest_json = serde_json::to_string(&batch_manifest)?;
    let mut form = Form::new().text("manifest", manifest_json);
    for (task, size, _) in tasks.iter() {
        if !batch_manifest_has(task, &batch_manifest) {
            continue;
        }
        let path = PathBuf::from(
            task.source_path
                .as_deref()
                .ok_or_else(|| anyhow!("batch item has no source path"))?,
        );
        let file = File::open(&path)
            .await
            .with_context(|| format!("open batch source {}", path.display()))?;
        let stream = ReaderStream::with_capacity(file, 64 * 1024);
        let body = reqwest::Body::wrap_stream(stream);
        let part = Part::stream_with_length(body, *size).file_name("content");
        let part = if let Some(media_type) = task.media_type.as_deref() {
            part.mime_str(media_type)?
        } else {
            part
        };
        form = form.part(format!("file.{}", task.snapshot.operation_id), part);
    }
    let mut commit_started = Vec::new();
    let mut pre_send_error = None;
    for (task, size, _) in tasks.iter_mut() {
        if !batch_manifest_has(task, &batch_manifest) {
            continue;
        }
        if let Err(error) = ensure_task_active(state, &task.snapshot.operation_id).await {
            pre_send_error = Some(error);
            break;
        }
        match state
            .queue
            .mark_commit_started(&task.snapshot.operation_id)
        {
            Ok(true) => {
                task.commit_started = true;
                commit_started.push(task.snapshot.operation_id.clone());
                state_event(state, task, TaskState::Committing, Some(*size), None);
            }
            Ok(false) => {
                pre_send_error = Some(anyhow!("batch_cancelled_before_send"));
                break;
            }
            Err(error) => {
                pre_send_error = Some(error.into());
                break;
            }
        }
    }
    if let Some(error) = pre_send_error {
        for operation_id in &commit_started {
            let _ = state.queue.clear_commit_started_before_send(operation_id);
        }
        if matches!(error.to_string().as_str(), "task_cancelled" | "task_paused") {
            anyhow::bail!("batch_cancelled_before_send");
        }
        return Err(error);
    }
    let response = state
        .api
        .post(upload_url)
        .multipart(form)
        .send()
        .await
        .context("send small-file batch")?;
    let batch_response: BatchUploadResponse = decode_response(response).await?;
    let results: std::collections::HashMap<_, _> = batch_response
        .items
        .into_iter()
        .map(|result| (result.op_id.clone(), result))
        .collect();
    for (task, size, hash) in tasks.iter() {
        let op = &task.snapshot.operation_id;
        if task.ticket.is_empty() {
            continue;
        }
        let Some(result) = results.get(op) else {
            state.queue.update_worker_state(
                op,
                TaskState::Failed,
                None,
                Some("batch_result_missing"),
                Some("transfer service returned no result for this file"),
                true,
                0,
            )?;
            state.queue.clear_commit_started_after_rejection(op)?;
            if task.snapshot.attempt < MAX_AUTO_RETRIES {
                state
                    .queue
                    .schedule_retry(op, retry_delay_seconds(task.snapshot.attempt))?;
            }
            continue;
        };
        if let Some(error) = &result.error {
            if let Err(state_error) = ensure_task_active(state, op).await {
                record_failure(state, task, &state_error).await;
                continue;
            }
            let conflict = error.code.contains("conflict");
            let retryable = is_transient_code(&error.code);
            let updated = state.queue.update_worker_state(
                op,
                if conflict {
                    TaskState::Conflict
                } else {
                    TaskState::Failed
                },
                None,
                Some(&error.code),
                Some(&error.message),
                retryable,
                0,
            )?;
            if !updated {
                record_failure(state, task, &anyhow!("task_cancelled")).await;
                continue;
            }
            // A per-file batch error means this operation never reached the control-plane
            // metadata commit, so cancellation is safe after this explicit rejection.
            if !conflict {
                state.queue.clear_commit_started_after_rejection(op)?;
            }
            if retryable && task.snapshot.attempt < MAX_AUTO_RETRIES {
                state
                    .queue
                    .schedule_retry(op, retry_delay_seconds(task.snapshot.attempt))?;
            }
            emit_task(
                state,
                task,
                if conflict {
                    TaskState::Conflict
                } else {
                    TaskState::Failed
                },
                None,
                Some(&error.message),
            );
            continue;
        }
        let receipt = result
            .receipt
            .as_ref()
            .ok_or_else(|| anyhow!("staged batch result omitted receipt"))?;
        let current = tasks
            .iter()
            .find(|(item, _, _)| item.snapshot.operation_id == *op)
            .map(|(item, _, _)| item)
            .unwrap();
        state
            .queue
            .update_worker_state(op, TaskState::Committing, Some(*size), None, None, false, 0)?;
        state_event(state, current, TaskState::Committing, Some(*size), None);
        if let Err(error) = ensure_task_active(state, op).await {
            record_failure(state, task, &error).await;
            continue;
        }
        let committed = api::commit_upload(&state.api, &config, op, receipt).await?;
        state
            .queue
            .update_worker_state(op, TaskState::Completed, Some(*size), None, None, false, 0)?;
        state.queue.set_version(op, committed.version)?;
        if let (Some(root_id), Some(scope_id), Some(logical_path)) =
            (&task.root_id, &task.scope_id, &task.snapshot.logical_path)
        {
            state.queue.upsert_baseline(&SyncBaseline {
                root_id: root_id.clone(),
                logical_path: logical_path.clone(),
                scope_id: scope_id.clone(),
                project_id: task.project_id.clone(),
                file_id: committed.file_id.clone(),
                revision_id: committed.revision_id.clone(),
                version: committed.version,
                size: *size,
                sha256: hash.clone(),
                deleted: false,
                conflicted: false,
            })?;
        }
        emit_task(state, task, TaskState::Completed, Some(*size), None);
    }
    emit_batch_progress(state, batch_id).await;
    Ok(())
}

fn batch_manifest_has(task: &TaskRecord, manifest: &BatchUploadManifest) -> bool {
    manifest
        .items
        .iter()
        .any(|item| item.manifest.op_id == task.snapshot.operation_id)
}

async fn download_one(state: &Arc<AgentState>, task: &mut TaskRecord) -> Result<()> {
    ensure_task_active(state, &task.snapshot.operation_id).await?;
    let config = state
        .config
        .read()
        .await
        .clone()
        .ok_or_else(|| anyhow!("agent is logged out"))?;
    let file_id = task
        .file_id
        .as_deref()
        .ok_or_else(|| anyhow!("download task is missing file_id"))?
        .to_owned();
    if task.ticket.is_empty()
        || task.object_id.is_none()
        || task.object_kind.is_none()
        || task.transfer_url.is_empty()
    {
        let intent =
            api::begin_download(&state.api, &config, &file_id, task.revision_id.as_deref()).await?;
        task.ticket = intent.ticket.clone();
        task.object_id = Some(intent.object_id.clone());
        task.object_kind = Some(intent.object_kind.clone());
        task.transfer_url = intent.transfer_url.clone();
        task.snapshot.bytes_total = Some(intent.size);
        task.revision_id = Some(intent.revision_id.clone());
        state.queue.update_authorization(
            &task.snapshot.operation_id,
            Some(&file_id),
            &intent.revision_id,
            &intent.object_id,
            &intent.object_kind,
            task.expected_version.unwrap_or(0),
            &intent.transfer_url,
            &intent.ticket,
        )?;
        state
            .queue
            .set_bytes_total(&task.snapshot.operation_id, intent.size)?;
        task.snapshot.expected_version = Some(task.expected_version.unwrap_or(0));
    }
    ensure_task_active(state, &task.snapshot.operation_id).await?;
    let mut init_url = task.transfer_url.trim_end_matches('/').to_owned();
    let response = state
        .api
        .post(&init_url)
        .json(&json!({"ticket":task.ticket}))
        .send()
        .await
        .context("initialize download")?;
    let response = if response.status() == StatusCode::UNAUTHORIZED {
        let intent = api::begin_download(
            &state.api,
            &config,
            &file_id,
            task.revision_id.as_deref(),
        )
        .await?;
        if task
            .object_id
            .as_deref()
            .is_some_and(|object_id| object_id != intent.object_id)
            || task
                .object_kind
                .as_deref()
                .is_some_and(|kind| kind != intent.object_kind)
        {
            anyhow::bail!("reissued download ticket changed the immutable object identity");
        }
        task.ticket = intent.ticket.clone();
        task.object_id = Some(intent.object_id.clone());
        task.object_kind = Some(intent.object_kind.clone());
        task.transfer_url = intent.transfer_url.clone();
        task.revision_id = Some(intent.revision_id.clone());
        task.snapshot.bytes_total = Some(intent.size);
        init_url = task.transfer_url.trim_end_matches('/').to_owned();
        state.queue.update_authorization(
            &task.snapshot.operation_id,
            Some(&file_id),
            &intent.revision_id,
            &intent.object_id,
            &intent.object_kind,
            task.expected_version.unwrap_or(0),
            &intent.transfer_url,
            &intent.ticket,
        )?;
        state
            .queue
            .set_bytes_total(&task.snapshot.operation_id, intent.size)?;
        state
            .api
            .post(&init_url)
            .json(&json!({"ticket":task.ticket}))
            .send()
            .await
            .context("retry download initialization with a renewed ticket")?
    } else {
        response
    };
    let download: DownloadInitResponse = decode_response(response).await?;
    if task
        .snapshot
        .bytes_total
        .is_some_and(|size| size != download.size)
    {
        anyhow::bail!("download size differs from the authorized file metadata");
    }
    let destination = PathBuf::from(
        task.destination_path
            .as_deref()
            .ok_or_else(|| anyhow!("download task has no destination path"))?,
    );
    ensure_download_path_safe(&config, task, &destination)?;
    if let Some(parent) = destination.parent() {
        fs::create_dir_all(parent).await?;
    }
    let partial = partial_path(&destination, &task.snapshot.operation_id);
    if download.size == 0 {
        ensure_download_path_safe(&config, task, &partial)?;
        let empty = OpenOptions::new()
            .create(true)
            .truncate(true)
            .write(true)
            .open(&partial)
            .await?;
        empty.sync_all().await?;
    }
    ensure_download_path_safe(&config, task, &partial)?;
    let mut current = match fs::metadata(&partial).await {
        Ok(meta) => meta.len(),
        Err(_) => 0,
    };
    if current > download.size {
        ensure_download_path_safe(&config, task, &partial)?;
        let _ = fs::remove_file(&partial).await;
        current = 0;
    }
    state.queue.update_worker_state(
        &task.snapshot.operation_id,
        TaskState::Transferring,
        Some(current),
        None,
        None,
        false,
        0,
    )?;
    state_event(state, task, TaskState::Transferring, Some(current), None);
    while current < download.size {
        ensure_task_active(state, &task.snapshot.operation_id).await?;
        let end = (current + download.chunk_size - 1).min(download.size - 1);
        let url = format!(
            "{}/{}",
            init_url.trim_end_matches("/downloads"),
            download.download_id
        );
        let response = state
            .api
            .get(url)
            .header("X-Transfer-Ticket", &task.ticket)
            .header("Range", format!("bytes={current}-{end}"))
            .send()
            .await
            .context("download object range")?;
        if response.status() != StatusCode::PARTIAL_CONTENT {
            ensure_success(response).await?;
            anyhow::bail!("transfer service did not honor the requested byte range");
        }
        let chunk = response.bytes().await?;
        let expected = (end - current + 1) as usize;
        if chunk.len() != expected {
            anyhow::bail!("download range has an unexpected byte count");
        }
        ensure_download_path_safe(&config, task, &partial)?;
        let mut output = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&partial)
            .await?;
        output.write_all(&chunk).await?;
        output.sync_data().await?;
        drop(output);
        current += chunk.len() as u64;
        state.queue.update_worker_state(
            &task.snapshot.operation_id,
            TaskState::Transferring,
            Some(current),
            None,
            None,
            false,
            0,
        )?;
        state_event(state, task, TaskState::Transferring, Some(current), None);
    }
    ensure_task_active(state, &task.snapshot.operation_id).await?;
    state.queue.update_worker_state(
        &task.snapshot.operation_id,
        TaskState::Verifying,
        Some(download.size),
        None,
        None,
        false,
        0,
    )?;
    state_event(state, task, TaskState::Verifying, Some(download.size), None);
    ensure_download_path_safe(&config, task, &partial)?;
    let actual_hash = hash_file(&partial).await?;
    if !actual_hash.eq_ignore_ascii_case(&download.sha256) {
        anyhow::bail!("downloaded file SHA-256 does not match the ticket");
    }
    ensure_download_path_safe(&config, task, &destination)?;
    if fs::metadata(&destination).await.is_ok() {
        let Some(expected_hash) = task.replace_if_hash.as_deref() else {
            anyhow::bail!(
                "destination already exists; it cannot be replaced without a baseline hash"
            );
        };
        ensure_download_path_safe(&config, task, &destination)?;
        let destination_hash = hash_file(&destination).await?;
        if !destination_hash.eq_ignore_ascii_case(expected_hash) {
            anyhow::bail!(
                "destination changed since the sync baseline; it was kept and not overwritten"
            );
        }
        let backup = destination.with_file_name(format!(
            ".muying-replace-{}.bak",
            task.snapshot.operation_id
        ));
        ensure_download_path_safe(&config, task, &destination)?;
        ensure_download_path_safe(&config, task, &backup)?;
        fs::rename(&destination, &backup).await?;
        ensure_download_path_safe(&config, task, &partial)?;
        ensure_download_path_safe(&config, task, &destination)?;
        if let Err(error) = fs::rename(&partial, &destination).await {
            ensure_download_path_safe(&config, task, &backup)?;
            ensure_download_path_safe(&config, task, &destination)?;
            let _ = fs::rename(&backup, &destination).await;
            return Err(error.into());
        }
        ensure_download_path_safe(&config, task, &backup)?;
        fs::remove_file(backup).await?;
    } else {
        ensure_download_path_safe(&config, task, &partial)?;
        ensure_download_path_safe(&config, task, &destination)?;
        fs::rename(&partial, &destination).await?;
    }
    state.queue.update_worker_state(
        &task.snapshot.operation_id,
        TaskState::Completed,
        Some(download.size),
        None,
        None,
        false,
        0,
    )?;
    if let (Some(root_id), Some(scope_id), Some(logical_path), Some(version)) = (
        &task.root_id,
        &task.scope_id,
        &task.snapshot.logical_path,
        task.expected_version,
    ) {
        ensure_download_path_safe(&config, task, &destination)?;
        let metadata = fs::metadata(&destination).await?;
        let modified_ns = metadata
            .modified()
            .ok()
            .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
            .map(|duration| duration.as_nanos().min(i64::MAX as u128) as i64)
            .unwrap_or(0);
        state.queue.upsert_baseline(&SyncBaseline {
            root_id: root_id.clone(),
            logical_path: logical_path.clone(),
            scope_id: scope_id.clone(),
            project_id: task.project_id.clone(),
            file_id,
            revision_id: task.snapshot.revision_id.clone()
                .ok_or_else(|| anyhow!("download task is missing its authorized revision ID"))?,
            version,
            size: download.size,
            sha256: download.sha256,
            deleted: false,
            conflicted: false,
        })?;
        state
            .queue
            .set_local_fingerprint(root_id, logical_path, metadata.len(), modified_ns)?;
    }
    state_event(state, task, TaskState::Completed, Some(download.size), None);
    Ok(())
}

#[derive(Deserialize)]
struct DownloadInitResponse {
    download_id: String,
    size: u64,
    sha256: String,
    chunk_size: u64,
}

async fn hash_stable_file(path: &Path) -> Result<(u64, String, i64)> {
    for attempt in 0..8 {
        let before = fs::metadata(path)
            .await
            .with_context(|| format!("stat source file {}", path.display()))?;
        if !before.is_file() {
            anyhow::bail!("source path is not a regular file");
        }
        sleep(Duration::from_millis(FILE_STABLE_CHECK_MS)).await;
        let after = fs::metadata(path).await?;
        if before.len() != after.len() || before.modified().ok() != after.modified().ok() {
            continue;
        }
        let hash = hash_file(path).await?;
        let final_meta = fs::metadata(path).await?;
        if final_meta.len() == after.len() && final_meta.modified().ok() == after.modified().ok() {
            let modified_ns = final_meta
                .modified()
                .ok()
                .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
                .map(|duration| duration.as_nanos().min(i64::MAX as u128) as i64)
                .unwrap_or(0);
            return Ok((final_meta.len(), hash, modified_ns));
        }
        if attempt == 7 {
            anyhow::bail!("source file kept changing while it was hashed");
        }
    }
    anyhow::bail!("source file did not become stable")
}

async fn hash_file(path: &Path) -> Result<String> {
    let mut input = File::open(path)
        .await
        .with_context(|| format!("open {} for hashing", path.display()))?;
    let mut hasher = Sha256::new();
    let mut buffer = vec![0_u8; 1024 * 1024];
    loop {
        let count = input.read(&mut buffer).await?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
    }
    Ok(hex::encode(hasher.finalize()))
}

fn partial_path(destination: &Path, operation_id: &str) -> PathBuf {
    let name = destination
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or("download");
    destination.with_file_name(format!(".{name}.{operation_id}.partial"))
}

fn reject_reparse_source_ancestry(path: &Path) -> Result<()> {
    let mut ancestors: Vec<_> = path.ancestors().collect();
    ancestors.reverse();
    for ancestor in ancestors {
        let metadata = std::fs::symlink_metadata(ancestor)?;
        let mut reparse = metadata.file_type().is_symlink();
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            reparse |= metadata.file_attributes() & 0x400 != 0;
        }
        if reparse {
            anyhow::bail!("sync source ancestry contains a reparse point");
        }
    }
    Ok(())
}

fn ensure_download_path_safe(
    config: &crate::AgentConfig,
    task: &TaskRecord,
    path: &Path,
) -> Result<()> {
    if task.source == "sync" {
        let root_id = task
            .root_id
            .as_deref()
            .ok_or_else(|| anyhow!("sync download is missing its root identity"))?;
        crate::watcher::ensure_safe_sync_path(config, root_id, path)?;
    }
    Ok(())
}

async fn ensure_task_active(state: &AgentState, operation_id: &str) -> Result<()> {
    if state
        .shutting_down
        .load(std::sync::atomic::Ordering::Acquire)
    {
        anyhow::bail!("agent_shutdown");
    }
    if state.config.read().await.is_none() {
        anyhow::bail!("agent_logged_out");
    }
    let status = state
        .queue
        .task_state(operation_id)?
        .unwrap_or_else(|| "cancelled".into());
    match status.as_str() {
        "paused" => anyhow::bail!("task_paused"),
        "cancelled" => anyhow::bail!("task_cancelled"),
        _ => Ok(()),
    }
}

fn subtract_range(ranges: &mut Vec<ByteRange>, start: u64, end: u64) {
    let mut next = Vec::new();
    for range in ranges.drain(..) {
        if end < range.start || start > range.end {
            next.push(range);
            continue;
        }
        if start > range.start {
            next.push(ByteRange {
                start: range.start,
                end: start - 1,
            });
        }
        if end < range.end {
            next.push(ByteRange {
                start: end + 1,
                end: range.end,
            });
        }
    }
    *ranges = next;
}

async fn decode_response<T: for<'de> serde::Deserialize<'de>>(
    response: reqwest::Response,
) -> Result<T> {
    let status = response.status();
    let bytes = response.bytes().await?;
    if !status.is_success() {
        let message = serde_json::from_slice::<Value>(&bytes)
            .ok()
            .and_then(|value| {
                value
                    .get("error")
                    .and_then(|e| e.get("message"))
                    .and_then(Value::as_str)
                    .map(str::to_owned)
            })
            .unwrap_or_else(|| format!("transfer service returned HTTP {}", status.as_u16()));
        if status == StatusCode::CONFLICT {
            anyhow::bail!("conflict: {message}");
        }
        anyhow::bail!("transfer_http_{}: {message}", status.as_u16());
    }
    serde_json::from_slice(&bytes).context("decode transfer service response")
}

async fn ensure_success(response: reqwest::Response) -> Result<()> {
    let status = response.status();
    if status.is_success() {
        return Ok(());
    }
    let body = response.text().await.unwrap_or_default();
    if status == StatusCode::CONFLICT {
        anyhow::bail!("conflict: HTTP {} {body}", status.as_u16());
    }
    anyhow::bail!("transfer_http_{}: {body}", status.as_u16())
}

fn state_event(
    state: &AgentState,
    task: &TaskRecord,
    new_state: TaskState,
    bytes: Option<u64>,
    message: Option<&str>,
) {
    let state_name = serde_json::to_value(new_state).unwrap_or(json!("unknown"));
    let snapshot = json!({"task_id":task.snapshot.task_id,"operation_id":task.snapshot.operation_id,"kind":task.snapshot.kind,
        "source":task.snapshot.source,"state":state_name,"display_name":task.snapshot.display_name,"folder_group":task.snapshot.folder_group,
        "logical_path":task.snapshot.logical_path,"file_id":task.file_id,"revision_id":task.revision_id,"object_id":task.object_id,
        "expected_version":task.expected_version,"batch_id":task.batch_parent_id,"bytes_total":task.snapshot.bytes_total,"bytes_done":bytes,
        "version":task.snapshot.version,
        "message":message});
    state.emit(AgentEvent {
        event: "task_state".into(),
        seq: 0,
        task_id: Some(task.snapshot.task_id.clone()),
        operation_id: Some(task.snapshot.operation_id.clone()),
        payload: snapshot.clone(),
    });
    state.emit(AgentEvent{event:"progress".into(),seq:0,task_id:Some(task.snapshot.task_id.clone()),operation_id:Some(task.snapshot.operation_id.clone()),
        payload:json!({"phase":state_name,"bytes_done":bytes,"bytes_total":task.snapshot.bytes_total})});
}

fn emit_task(
    state: &AgentState,
    task: &TaskRecord,
    new_state: TaskState,
    bytes: Option<u64>,
    message: Option<&str>,
) {
    state_event(state, task, new_state, bytes, message);
}

async fn emit_batch_progress(state: &AgentState, batch_id: &str) {
    if let Ok(tasks) = state.queue.list_tasks() {
        let children: Vec<_> = tasks
            .iter()
            .filter(|task| task.batch_id.as_deref() == Some(batch_id))
            .collect();
        if children.is_empty() {
            return;
        }
        let done = children
            .iter()
            .filter(|task| {
                matches!(
                    task.state.as_str(),
                    "completed" | "failed" | "conflict" | "cancelled"
                )
            })
            .count();
        let bytes_done = children.iter().map(|task| task.bytes_done).sum::<u64>();
        let bytes_total = children
            .iter()
            .filter_map(|task| task.bytes_total)
            .sum::<u64>();
        state.emit(AgentEvent{event:"progress".into(),seq:0,task_id:Some(batch_id.into()),operation_id:None,
            payload:json!({"phase":"batch","state":if done==children.len(){"completed"}else{"transferring"},"items_total":children.len(),"items_done":done,"bytes_done":bytes_done,"bytes_total":bytes_total})});
    }
}

async fn record_failure(state: &AgentState, task: &TaskRecord, error: &anyhow::Error) {
    let text = error.to_string();
    let conflict = text.starts_with("conflict:");
    let paused = text == "task_paused";
    let cancelled = text == "task_cancelled";
    let inactive = text == "agent_logged_out" || text == "agent_shutdown";
    let transient = is_transient_error(error);
    let (new_state, code, retryable) = if paused {
        (TaskState::Paused, "paused", false)
    } else if inactive {
        (TaskState::Queued, "worker_stopped", false)
    } else if cancelled {
        (TaskState::Cancelled, "cancelled", false)
    } else if conflict {
        (TaskState::Conflict, "version_conflict", false)
    } else {
        (TaskState::Failed, "transfer_failed", transient)
    };
    let current = state
        .queue
        .task_state(&task.snapshot.operation_id)
        .ok()
        .flatten();
    match current.as_deref() {
        Some("paused") => {
            state_event(state, task, TaskState::Paused, None, None);
            return;
        }
        Some("cancelled") => {
            state_event(state, task, TaskState::Cancelled, None, None);
            return;
        }
        Some("completed") => {
            state_event(state, task, TaskState::Completed, None, None);
            return;
        }
        Some("conflict") => {
            state_event(state, task, TaskState::Conflict, None, None);
            return;
        }
        Some("queued") if paused || cancelled => return,
        _ => {}
    }
    let _ = state.queue.update_worker_state(
        &task.snapshot.operation_id,
        new_state.clone(),
        None,
        Some(code),
        Some(&text),
        retryable,
        0,
    );
    if is_definitive_http_rejection(error) {
        let _ = state.queue.clear_commit_started_after_rejection(&task.snapshot.operation_id);
    }
    let retry_scheduled = transient
        && task.snapshot.attempt < MAX_AUTO_RETRIES
        && state
            .queue
            .schedule_retry(
                &task.snapshot.operation_id,
                retry_delay_seconds(task.snapshot.attempt),
            )
            .is_ok();
    warn!(operation_id=%task.snapshot.operation_id,error=%text,retry_scheduled,"transfer task failed");
    if retry_scheduled {
        state_event(state, task, TaskState::Queued, None, Some("temporary transfer error; retry scheduled"));
    } else {
        state_event(state, task, new_state, None, Some(&text));
    }
}

fn retry_delay_seconds(attempt: u32) -> u64 {
    1_u64 << attempt.saturating_sub(1).min(6)
}

fn is_transient_code(code: &str) -> bool {
    matches!(
        code,
        "transfer_busy" | "staging_quota_exceeded" | "temporary_unavailable"
    )
}

fn is_transient_error(error: &anyhow::Error) -> bool {
    if error.chain().any(|cause| {
        cause.downcast_ref::<reqwest::Error>().is_some_and(|request| {
            request.is_timeout() || request.is_connect() || request.is_body() || request.is_request()
        })
    }) {
        return true;
    }
    let text = error.to_string();
    for marker in ["transfer_http_", "control_api_http_"] {
        if let Some((_, suffix)) = text.split_once(marker) {
            let digits: String = suffix.chars().take_while(char::is_ascii_digit).collect();
            if let Ok(status) = digits.parse::<u16>() {
                return matches!(status, 408 | 425 | 429)
                    || status >= 500
                    || (marker == "transfer_http_" && status == 401);
            }
        }
    }
    false
}

fn is_definitive_http_rejection(error: &anyhow::Error) -> bool {
    if is_transient_error(error) {
        return false;
    }
    let text = error.to_string();
    ["transfer_http_", "control_api_http_"]
        .iter()
        .any(|marker| {
            text.split_once(marker)
                .and_then(|(_, suffix)| {
                    let digits: String = suffix.chars().take_while(char::is_ascii_digit).collect();
                    digits.parse::<u16>().ok()
                })
                .is_some_and(|status| (400..500).contains(&status))
        })
}

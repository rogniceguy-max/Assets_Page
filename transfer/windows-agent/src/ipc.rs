use std::sync::Arc;

#[cfg(not(windows))]
use anyhow::anyhow;
use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tokio::{
    io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt},
    sync::broadcast,
};
use tracing::debug;
#[cfg(not(windows))]
use tracing::warn;
use uuid::Uuid;

use crate::{
    queue::{NewTask, TaskKind, TaskState},
    watcher::WatcherCommand,
    AgentConfig, AgentEvent, AgentState, AGENT_VERSION,
};

const MAX_FRAME_BYTES: usize = 1024 * 1024;

#[derive(Debug, Deserialize)]
struct RequestEnvelope {
    v: u32,
    id: String,
    command: String,
    #[serde(default)]
    payload: Value,
}

#[derive(Debug, Serialize)]
struct ReplyEnvelope {
    v: u32,
    id: String,
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    payload: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<IpcError>,
}

#[derive(Debug, Serialize)]
struct IpcError {
    code: String,
    message: String,
    retryable: bool,
}

#[derive(Debug, Deserialize)]
struct UploadTaskPayload {
    operation_id: String,
    source_path: String,
    root_id: Option<String>,
    scope_id: Option<String>,
    project_id: Option<String>,
    logical_path: String,
    file_id: Option<String>,
    expected_version: Option<i64>,
    object_id: Option<String>,
    media_type: Option<String>,
    #[serde(default = "default_source")]
    source: String,
    #[serde(default)]
    size: Option<u64>,
    #[serde(default)]
    sha256: Option<String>,
    #[serde(default)]
    ticket: String,
    #[serde(default)]
    transfer_url: String,
    #[serde(default)]
    display_name: Option<String>,
    #[serde(default)]
    folder_group: Option<String>,
}

#[derive(Debug, Deserialize)]
struct DownloadTaskPayload {
    operation_id: String,
    file_id: String,
    revision_id: Option<String>,
    object_id: Option<String>,
    destination_path: String,
    size: Option<u64>,
    sha256: Option<String>,
    #[serde(default)]
    ticket: String,
    #[serde(default)]
    transfer_url: String,
    #[serde(default)]
    display_name: Option<String>,
    #[serde(default)]
    folder_group: Option<String>,
    #[serde(default)]
    logical_path: Option<String>,
    replace_if_sha256: Option<String>,
    #[serde(default = "default_wpf_source")]
    source: String,
    #[serde(default)]
    scope_id: Option<String>,
    #[serde(default)]
    project_id: Option<String>,
}

fn default_wpf_source() -> String {
    "wpf".into()
}

#[derive(Debug, Deserialize)]
struct BatchPayload {
    #[serde(default)]
    batch_id: Option<String>,
    #[serde(default)]
    items: Vec<UploadTaskPayload>,
    #[serde(default)]
    directories: Vec<muying_transfer_common::protocol::LogicalDirectory>,
}

#[derive(Debug, Deserialize)]
struct TaskIdPayload {
    task_id: String,
}

#[derive(Debug, Deserialize)]
struct RootIdPayload {
    root_id: String,
}

#[derive(Debug, Deserialize)]
struct ProjectIdPayload {
    project_id: String,
}

#[derive(Debug, Deserialize)]
struct RootIdsPayload {
    #[serde(default)]
    root_ids: Vec<String>,
}

fn default_source() -> String {
    "wpf".to_owned()
}

fn reply_ok(id: &str, payload: Value) -> ReplyEnvelope {
    ReplyEnvelope {
        v: 1,
        id: id.to_owned(),
        ok: true,
        payload: Some(payload),
        error: None,
    }
}

fn reply_error(id: &str, code: &str, message: impl Into<String>, retryable: bool) -> ReplyEnvelope {
    ReplyEnvelope {
        v: 1,
        id: id.to_owned(),
        ok: false,
        payload: None,
        error: Some(IpcError {
            code: code.to_owned(),
            message: message.into(),
            retryable,
        }),
    }
}

async fn handle_request(
    state: &Arc<AgentState>,
    request: RequestEnvelope,
) -> (ReplyEnvelope, bool, bool) {
    if request.v != 1 || Uuid::parse_str(&request.id).is_err() {
        return (
            reply_error(
                &request.id,
                "invalid_request",
                "protocol version or request id is invalid",
                false,
            ),
            false,
            false,
        );
    }
    let result: Result<Value, (String, String, bool)> = match request.command.as_str() {
        "hello" => Ok(json!({"agent_version":AGENT_VERSION,"protocol_version":1})),
        "configure" => configure(state, &request.payload).await,
        "get_state" => state_snapshot(state).await,
        "list_tasks" => task_list(state),
        "enqueue_upload" => enqueue_upload(state, &request.payload),
        "enqueue_batch" => enqueue_batch(state, &request.payload),
        "enqueue_download" => enqueue_download(state, &request.payload),
        "pause" => set_task_state(state, &request.payload, TaskState::Paused),
        "pause_for_shutdown" => pause_for_shutdown(state, &request.payload),
        "resume" => set_task_state(state, &request.payload, TaskState::Queued),
        "resume_sync_roots" => resume_sync_roots(state, &request.payload),
        "resume_project_asset_downloads" => resume_project_asset_downloads(state, &request.payload),
        "cancel" => cancel_task(state, &request.payload).await,
        "rescan" => rescan(state, &request.payload).await,
        "stop_watching" => stop_watching(state).await,
        "logout" => logout(state).await,
        "shutdown" => Ok(json!({"stopping":true})),
        "subscribe_events" => Ok(json!({"subscribed":true})),
        _ => Err((
            "unknown_command".into(),
            "command is not supported by this agent version".into(),
            false,
        )),
    };
    match result {
        Ok(payload) => (
            reply_ok(&request.id, payload),
            request.command == "subscribe_events",
            request.command == "shutdown",
        ),
        Err((code, message, retryable)) => (
            reply_error(&request.id, &code, message, retryable),
            false,
            false,
        ),
    }
}

async fn configure(
    state: &Arc<AgentState>,
    payload: &Value,
) -> Result<Value, (String, String, bool)> {
    let config: AgentConfig = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_config".into(),
            "configuration payload is missing required fields".into(),
            false,
        )
    })?;
    if config.api_base_url.trim().is_empty()
        || config.bearer_token.trim().is_empty()
        || config.client_id.trim().is_empty()
    {
        return Err((
            "invalid_config".into(),
            "API URL, bearer token, and client ID are required".into(),
            false,
        ));
    }
    if config.sync_roots.iter().any(|root| {
        root.path.trim().is_empty()
            || root.logical_path_prefix.starts_with('/')
            || root.logical_path_prefix.contains("..")
    }) {
        return Err((
            "invalid_sync_root".into(),
            "sync root path or logical prefix is invalid".into(),
            false,
        ));
    }
    let _worker_gate = state.worker_gate.lock().await;
    stop_watcher_roots(state).await?;
    wait_for_worker_idle(state).await;
    state.queue.recover_interrupted().map_err(db_error)?;
    *state.config.write().await = Some(config.clone());
    state
        .watcher_commands
        .send(WatcherCommand::Configure(config.sync_roots.clone()))
        .await
        .map_err(|_| {
            (
                "watcher_unavailable".into(),
                "local directory watcher is unavailable".into(),
                true,
            )
        })?;
    state.emit(AgentEvent {
        event: "task_state".into(),
        seq: 0,
        task_id: None,
        operation_id: None,
        payload: json!({"state":"configured"}),
    });
    Ok(json!({"configured":true,"watch_roots":config.sync_roots.len()}))
}

async fn state_snapshot(state: &AgentState) -> Result<Value, (String, String, bool)> {
    let configured = state.config.read().await.is_some();
    let tasks = state.queue.list_tasks().map_err(db_error)?;
    let queue_depth = tasks
        .iter()
        .filter(|task| !matches!(task.state.as_str(), "completed" | "cancelled"))
        .count();
    let active_count = tasks
        .iter()
        .filter(|task| {
            matches!(
                task.state.as_str(),
                "hashing" | "transferring" | "verifying" | "staged" | "committing"
            )
        })
        .count();
    Ok(
        json!({"agent_version":AGENT_VERSION,"configured":configured,"queue_depth":queue_depth,"active_count":active_count,"tasks":tasks}),
    )
}

fn task_list(state: &AgentState) -> Result<Value, (String, String, bool)> {
    let tasks = state.queue.list_tasks().map_err(db_error)?;
    Ok(json!({"tasks":tasks}))
}

fn upload_task(
    task: UploadTaskPayload,
    batch_parent_id: Option<String>,
) -> Result<NewTask, (String, String, bool)> {
    let operation_id = Uuid::parse_str(&task.operation_id)
        .map_err(|_| {
            (
                "invalid_operation_id".into(),
                "operation_id must be a UUID".into(),
                false,
            )
        })?
        .to_string();
    let path = std::path::Path::new(&task.source_path);
    if !path.is_absolute() || task.source_path.trim().is_empty() {
        return Err((
            "invalid_source_path".into(),
            "source_path must be an absolute local path".into(),
            false,
        ));
    }
    if task.logical_path.trim().is_empty()
        || task.logical_path.starts_with('/')
        || task
            .logical_path
            .split(['\\', '/'])
            .any(|part| part == "..")
    {
        return Err((
            "invalid_logical_path".into(),
            "logical_path must be relative and cannot escape its scope".into(),
            false,
        ));
    }
    if !matches!(task.source.as_str(), "wpf" | "sync") {
        return Err((
            "invalid_source".into(),
            "source must be wpf or sync".into(),
            false,
        ));
    }
    let display_name = task.display_name.unwrap_or_else(|| {
        path.file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("File")
            .to_owned()
    });
    Ok(NewTask {
        root_id: task.root_id,
        operation_id,
        kind: TaskKind::Upload,
        source_path: Some(task.source_path),
        destination_path: None,
        logical_path: Some(task.logical_path),
        scope_id: task.scope_id,
        project_id: task.project_id,
        display_name,
        folder_group: task.folder_group.unwrap_or_default(),
        file_id: task.file_id,
        revision_id: None,
        object_id: task.object_id,
        expected_version: task.expected_version,
        media_type: task.media_type,
        source: task.source,
        transfer_url: task.transfer_url,
        ticket: task.ticket,
        bytes_total: task.size,
        replace_if_hash: None,
        batch_parent_id,
    })
}

fn enqueue_upload(state: &AgentState, payload: &Value) -> Result<Value, (String, String, bool)> {
    let parsed: UploadTaskPayload = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_upload_task".into(),
            "upload task payload is malformed".into(),
            false,
        )
    })?;
    let task = upload_task(parsed, None)?;
    let op_id = task.operation_id.clone();
    state.queue.enqueue(&task).map_err(db_error)?;
    let snapshot = state
        .queue
        .list_tasks()
        .map_err(db_error)?
        .into_iter()
        .find(|t| t.operation_id == op_id)
        .ok_or_else(|| {
            (
                "queue_read_failed".into(),
                "queued task could not be read back".into(),
                true,
            )
        })?;
    state.emit(AgentEvent {
        event: "task_state".into(),
        seq: 0,
        task_id: Some(op_id.clone()),
        operation_id: Some(op_id),
        payload: json!({"state":"queued","task":snapshot}),
    });
    Ok(json!({"queued":true,"task":snapshot}))
}

fn enqueue_batch(state: &AgentState, payload: &Value) -> Result<Value, (String, String, bool)> {
    let mut batch: BatchPayload = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_batch".into(),
            "batch payload is malformed".into(),
            false,
        )
    })?;
    if batch.items.is_empty() && batch.directories.is_empty() {
        return Err((
            "empty_batch".into(),
            "batch must contain files or logical directories".into(),
            false,
        ));
    }
    let batch_id = batch.batch_id.unwrap_or_else(|| Uuid::new_v4().to_string());
    let batch_id = Uuid::parse_str(&batch_id)
        .map_err(|_| {
            (
                "invalid_batch_id".into(),
                "batch_id must be a UUID".into(),
                false,
            )
        })?
        .to_string();
    let tasks = batch
        .items
        .drain(..)
        .map(|item| upload_task(item, Some(batch_id.clone())))
        .collect::<Result<Vec<_>, _>>()?;
    let directories_json = serde_json::to_string(&batch.directories).map_err(|_| {
        (
            "invalid_directories".into(),
            "directory manifest cannot be serialized".into(),
            false,
        )
    })?;
    state
        .queue
        .enqueue_batch(&batch_id, &tasks, &directories_json)
        .map_err(db_error)?;
    let all = state.queue.list_tasks().map_err(db_error)?;
    let children: Vec<_> = all
        .into_iter()
        .filter(|task| {
            task.operation_id != batch_id
                && tasks
                    .iter()
                    .any(|item| item.operation_id == task.operation_id)
        })
        .collect();
    for task in &children {
        state.emit(AgentEvent {
            event: "task_state".into(),
            seq: 0,
            task_id: Some(task.task_id.clone()),
            operation_id: Some(task.operation_id.clone()),
            payload: json!({"state":"queued","task":task}),
        });
    }
    Ok(
        json!({"queued":true,"batch_id":batch_id,"tasks":children,"items_total":tasks.len(),"items_queued":tasks.len()}),
    )
}

fn enqueue_download(state: &AgentState, payload: &Value) -> Result<Value, (String, String, bool)> {
    let task: DownloadTaskPayload = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_download_task".into(),
            "download task payload is malformed".into(),
            false,
        )
    })?;
    let operation = Uuid::parse_str(&task.operation_id)
        .map_err(|_| {
            (
                "invalid_operation_id".into(),
                "operation_id must be a UUID".into(),
                false,
            )
        })?
        .to_string();
    if !std::path::Path::new(&task.destination_path).is_absolute() {
        return Err((
            "invalid_destination_path".into(),
            "destination_path must be absolute".into(),
            false,
        ));
    }
    let new_task = NewTask {
        root_id: None,
        operation_id: operation.clone(),
        kind: TaskKind::Download,
        source_path: None,
        destination_path: Some(task.destination_path),
        logical_path: task.logical_path,
        scope_id: task.scope_id,
        project_id: task.project_id,
        display_name: task.display_name.unwrap_or_else(|| "Download".into()),
        folder_group: task.folder_group.unwrap_or_default(),
        file_id: Some(task.file_id),
        revision_id: task.revision_id,
        object_id: task.object_id,
        expected_version: None,
        media_type: None,
        source: task.source,
        transfer_url: task.transfer_url,
        ticket: task.ticket,
        bytes_total: task.size,
        replace_if_hash: task.replace_if_sha256,
        batch_parent_id: None,
    };
    state.queue.enqueue(&new_task).map_err(db_error)?;
    let snapshot = state
        .queue
        .list_tasks()
        .map_err(db_error)?
        .into_iter()
        .find(|t| t.operation_id == operation)
        .ok_or_else(|| {
            (
                "queue_read_failed".into(),
                "queued task could not be read back".into(),
                true,
            )
        })?;
    state.emit(AgentEvent {
        event: "task_state".into(),
        seq: 0,
        task_id: Some(operation.clone()),
        operation_id: Some(operation),
        payload: json!({"state":"queued","task":snapshot}),
    });
    Ok(json!({"queued":true,"task":snapshot}))
}

fn set_task_state(
    state: &AgentState,
    payload: &Value,
    new_state: TaskState,
) -> Result<Value, (String, String, bool)> {
    let payload: TaskIdPayload = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_task_id".into(),
            "task_id is required".into(),
            false,
        )
    })?;
    let task_id = Uuid::parse_str(&payload.task_id)
        .map_err(|_| {
            (
                "invalid_task_id".into(),
                "task_id must be a UUID".into(),
                false,
            )
        })?
        .to_string();
    let updated = state
        .queue
        .set_user_task_state(&task_id, new_state.clone())
        .map_err(db_error)?;
    if !updated {
        return Err((
            "task_state_changed".into(),
            "task state changed before the requested action could be applied".into(),
            false,
        ));
    }
    let state_name = serde_json::to_value(new_state).unwrap_or(json!("unknown"));
    state.emit(AgentEvent {
        event: "task_state".into(),
        seq: 0,
        task_id: Some(task_id.clone()),
        operation_id: Some(task_id.clone()),
        payload: json!({"state":state_name}),
    });
    Ok(json!({"task_id":task_id,"state":state_name}))
}

fn pause_for_shutdown(
    state: &AgentState,
    payload: &Value,
) -> Result<Value, (String, String, bool)> {
    let payload: TaskIdPayload = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_task_id".into(),
            "task_id is required".into(),
            false,
        )
    })?;
    let task_id = Uuid::parse_str(&payload.task_id)
        .map_err(|_| {
            (
                "invalid_task_id".into(),
                "task_id must be a UUID".into(),
                false,
            )
        })?
        .to_string();
    if !state
        .queue
        .set_shutdown_paused(&task_id)
        .map_err(db_error)?
    {
        return Err((
            "task_state_changed".into(),
            "task is no longer active and could not be paused for shutdown".into(),
            false,
        ));
    }
    state.emit(AgentEvent {
        event: "task_state".into(),
        seq: 0,
        task_id: Some(task_id.clone()),
        operation_id: Some(task_id.clone()),
        payload: json!({"state":"paused","reason":"shutdown"}),
    });
    Ok(json!({"task_id":task_id,"state":"paused","reason":"shutdown"}))
}

fn resume_sync_roots(state: &AgentState, payload: &Value) -> Result<Value, (String, String, bool)> {
    let payload: RootIdsPayload = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_root_ids".into(),
            "root_ids must be an array of sync root IDs".into(),
            false,
        )
    })?;
    if payload
        .root_ids
        .iter()
        .any(|root_id| root_id.trim().is_empty() || root_id.len() > 256)
    {
        return Err((
            "invalid_root_ids".into(),
            "sync root ID is invalid".into(),
            false,
        ));
    }
    let resumed = state
        .queue
        .resume_shutdown_paused_sync_tasks(&payload.root_ids)
        .map_err(db_error)?;
    Ok(json!({"resumed":resumed}))
}

fn resume_project_asset_downloads(
    state: &AgentState,
    payload: &Value,
) -> Result<Value, (String, String, bool)> {
    let payload: ProjectIdPayload = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_project_id".into(),
            "project_id is required".into(),
            false,
        )
    })?;
    if payload.project_id.trim().is_empty()
        || payload.project_id.len() > 512
        || payload.project_id.chars().any(char::is_control)
    {
        return Err((
            "invalid_project_id".into(),
            "project_id is invalid".into(),
            false,
        ));
    }
    let resumed = state
        .queue
        .resume_shutdown_paused_asset_downloads(&payload.project_id)
        .map_err(db_error)?;
    Ok(json!({"resumed":resumed}))
}

async fn cancel_task(
    state: &Arc<AgentState>,
    payload: &Value,
) -> Result<Value, (String, String, bool)> {
    let parsed: TaskIdPayload = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_task_id".into(),
            "task_id is required".into(),
            false,
        )
    })?;
    let operation_id = Uuid::parse_str(&parsed.task_id)
        .map_err(|_| {
            (
                "invalid_task_id".into(),
                "task_id must be a UUID".into(),
                false,
            )
        })?
        .to_string();
    let task = state
        .queue
        .task_record(&operation_id)
        .map_err(db_error)?
        .ok_or_else(|| {
            (
                "task_not_found".into(),
                "the task does not exist".into(),
                false,
            )
        })?;
    let current = task.snapshot.state.as_str();
    if task.commit_started || matches!(current, "completed" | "committing") {
        return Err((
            "task_not_cancellable".into(),
            "the upload has entered its commit phase or is complete".into(),
            false,
        ));
    }
    let already_cancelled = current == "cancelled";
    if !already_cancelled {
        let updated = state
            .queue
            .set_user_task_state(&operation_id, TaskState::Cancelled)
            .map_err(db_error)?;
        if !updated {
            return Err((
                "task_state_changed".into(),
                "the task entered commit or another terminal state before cancellation".into(),
                false,
            ));
        }
        state.emit(AgentEvent {
            event: "task_state".into(),
            seq: 0,
            task_id: Some(operation_id.clone()),
            operation_id: Some(operation_id.clone()),
            payload: json!({"state":"cancelled"}),
        });
    }
    let mut remote_cleanup_error = None;
    if matches!(task.snapshot.kind.as_str(), "upload" | "batch_upload") && task.object_id.is_some()
    {
        if let Some(config) = state.config.read().await.clone() {
            let expected_state = task.control_state.as_deref().unwrap_or("receiving");
            match crate::api::abort_upload(&state.api, &config, &operation_id, expected_state).await
            {
                Ok(abort) if abort.state == "aborted" => {}
                Ok(abort) => {
                    remote_cleanup_error = Some(format!(
                        "API returned state '{}' while cancelling",
                        abort.state
                    ));
                }
                Err(error) => remote_cleanup_error = Some(error.to_string()),
            }
            if remote_cleanup_error.is_none()
                && !task.ticket.is_empty()
                && !task.transfer_url.is_empty()
            {
                match crate::api::abort_transfer_upload(
                    &state.api,
                    &task.transfer_url,
                    &operation_id,
                    &task.ticket,
                )
                .await
                {
                    Ok(result)
                        if result.upload_id == operation_id
                            && matches!(result.state.as_str(), "aborted" | "expired") => {}
                    Ok(result) => {
                        remote_cleanup_error = Some(format!(
                            "transfer service returned state '{}' while cancelling",
                            result.state
                        ));
                    }
                    Err(error) => remote_cleanup_error = Some(error.to_string()),
                }
            }
        } else {
            remote_cleanup_error =
                Some("API credentials are unavailable to clean up this upload".into());
        }
    }
    // The durable local queue state has already been changed to cancelled. Report
    // remote cleanup separately so a transient API failure cannot leave the UI
    // believing that its local cancellation failed.
    Ok(json!({
        "task_id":operation_id,
        "state":"cancelled",
        "remote_cleanup_succeeded":remote_cleanup_error.is_none(),
        "remote_cleanup_error":remote_cleanup_error
    }))
}

async fn rescan(state: &AgentState, payload: &Value) -> Result<Value, (String, String, bool)> {
    let payload: RootIdPayload = serde_json::from_value(payload.clone()).map_err(|_| {
        (
            "invalid_root_id".into(),
            "root_id is required".into(),
            false,
        )
    })?;
    state
        .watcher_commands
        .send(WatcherCommand::Rescan(payload.root_id.clone()))
        .await
        .map_err(|_| {
            (
                "watcher_unavailable".into(),
                "local directory watcher is unavailable".into(),
                true,
            )
        })?;
    Ok(json!({"root_id":payload.root_id,"queued":true}))
}

async fn stop_watching(state: &AgentState) -> Result<Value, (String, String, bool)> {
    let _worker_gate = state.worker_gate.lock().await;
    stop_watcher_roots(state).await?;
    Ok(json!({"watching":false,"configuration_preserved":true}))
}

async fn logout(state: &AgentState) -> Result<Value, (String, String, bool)> {
    let _worker_gate = state.worker_gate.lock().await;
    *state.config.write().await = None;
    stop_watcher_roots(state).await?;
    wait_for_worker_idle(state).await;
    state.queue.recover_interrupted().map_err(db_error)?;
    Ok(json!({"logged_out":true,"queue_preserved":true}))
}

async fn stop_watcher_roots(state: &AgentState) -> Result<(), (String, String, bool)> {
    let (acknowledge, acknowledged) = tokio::sync::oneshot::channel();
    state
        .watcher_commands
        .send(WatcherCommand::Logout(acknowledge))
        .await
        .map_err(|_| {
            (
                "watcher_unavailable".into(),
                "local directory watcher is unavailable".into(),
                true,
            )
        })?;
    acknowledged.await.map_err(|_| {
        (
            "watcher_unavailable".into(),
            "local directory watcher stopped before logout completed".into(),
            true,
        )
    })
}

async fn wait_for_worker_idle(state: &AgentState) {
    let mut status = state.worker_status.subscribe();
    while *status.borrow() {
        if status.changed().await.is_err() {
            break;
        }
    }
}

fn db_error(error: anyhow::Error) -> (String, String, bool) {
    ("local_queue_error".into(), error.to_string(), true)
}

async fn read_frame<R: AsyncRead + Unpin>(reader: &mut R) -> Result<Option<Vec<u8>>> {
    let mut prefix = [0_u8; 4];
    match reader.read_exact(&mut prefix).await {
        Ok(_) => {}
        Err(error) if error.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(error) => return Err(error.into()),
    }
    let length = u32::from_le_bytes(prefix) as usize;
    if length == 0 || length > MAX_FRAME_BYTES {
        anyhow::bail!("IPC frame length is outside the allowed range");
    }
    let mut buffer = vec![0_u8; length];
    reader.read_exact(&mut buffer).await?;
    Ok(Some(buffer))
}

async fn write_frame<W: AsyncWrite + Unpin, T: Serialize>(writer: &mut W, value: &T) -> Result<()> {
    let bytes = serde_json::to_vec(value)?;
    if bytes.is_empty() || bytes.len() > MAX_FRAME_BYTES {
        anyhow::bail!("IPC response frame exceeds the size limit");
    }
    writer
        .write_all(&(bytes.len() as u32).to_le_bytes())
        .await?;
    writer.write_all(&bytes).await?;
    writer.flush().await?;
    Ok(())
}

#[cfg(windows)]
pub async fn run_pipe_server(pipe_name: String, state: Arc<AgentState>) -> Result<()> {
    use tokio::net::windows::named_pipe::NamedPipeServer;
    use windows_sys::Win32::{
        Foundation::{LocalFree, INVALID_HANDLE_VALUE},
        Security::{
            Authorization::{
                ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
            },
            SECURITY_ATTRIBUTES,
        },
        Storage::FileSystem::{FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX},
        System::Pipes::{
            CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE,
            PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
        },
    };
    const PIPE_BUFFER: u32 = 64 * 1024;
    let mut connections = tokio::task::JoinSet::new();
    let wide_name: Vec<u16> = pipe_name.encode_utf16().chain(std::iter::once(0)).collect();
    let sddl: Vec<u16> = "D:P(A;;GA;;;OW)(A;;GA;;;SY)"
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();

    loop {
        if state
            .shutting_down
            .load(std::sync::atomic::Ordering::Acquire)
        {
            return Ok(());
        }
        let mut descriptor = std::ptr::null_mut();
        let converted = unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                SDDL_REVISION_1,
                &mut descriptor,
                std::ptr::null_mut(),
            )
        };
        if converted == 0 {
            anyhow::bail!("could not build same-user pipe ACL");
        }
        let mut attributes = SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: descriptor,
            bInheritHandle: 0,
        };
        let handle = unsafe {
            CreateNamedPipeW(
                wide_name.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                PIPE_UNLIMITED_INSTANCES,
                PIPE_BUFFER,
                PIPE_BUFFER,
                0,
                &mut attributes,
            )
        };
        unsafe {
            LocalFree(descriptor);
        }
        if handle == INVALID_HANDLE_VALUE {
            anyhow::bail!("CreateNamedPipeW failed");
        }
        let server = unsafe { NamedPipeServer::from_raw_handle(handle.cast()) }
            .context("wrap named pipe server handle")?;
        if state
            .shutting_down
            .load(std::sync::atomic::Ordering::Acquire)
        {
            connections.abort_all();
            while connections.join_next().await.is_some() {}
            return Ok(());
        }
        // Keep this listener's connect future alive while reaping completed
        // requests. Dropping it whenever a request finishes can break a client
        // that is connecting concurrently (including list_tasks and cancel).
        let accepted = {
            let connect = server.connect();
            tokio::pin!(connect);
            loop {
                tokio::select! {
                    result = &mut connect => {
                        result.context("wait for agent pipe client")?;
                        break true;
                    },
                    joined = connections.join_next(), if !connections.is_empty() => {
                        if let Some(result) = joined {
                            match result {
                                Ok(true) => {
                                    // The shutdown handler has completed its reply write and has
                                    // returned from the connection task. Only now notify workers
                                    // and abort the other long-lived IPC connections.
                                    state
                                        .shutting_down
                                        .store(true, std::sync::atomic::Ordering::Release);
                                    state.shutdown_notify.notify_waiters();
                                    connections.abort_all();
                                    while let Some(result) = connections.join_next().await {
                                        if let Err(error) = result {
                                            debug!(%error,"agent pipe connection task failed during shutdown");
                                        }
                                    }
                                    return Ok(());
                                }
                                Ok(false) => {}
                                Err(error) => debug!(%error,"agent pipe connection task failed"),
                            }
                        }
                    },
                }
            }
        };
        if accepted {
            let connected_state = state.clone();
            connections.spawn(async move {
                match serve_connection(server, connected_state).await {
                    Ok(shutdown) => shutdown,
                    Err(error) => {
                        debug!(%error,"agent pipe connection closed");
                        false
                    }
                }
            });
        }
    }
}

#[cfg(windows)]
async fn serve_connection(
    mut stream: tokio::net::windows::named_pipe::NamedPipeServer,
    state: Arc<AgentState>,
) -> Result<bool> {
    let Some(frame) = read_frame(&mut stream).await? else {
        return Ok(false);
    };
    let request: RequestEnvelope =
        serde_json::from_slice(&frame).context("decode agent pipe request")?;
    let id = request.id.clone();
    let mut event_receiver = state.events.subscribe();
    let (reply, subscribe, shutdown) = handle_request(&state, request).await;
    write_frame(&mut stream, &reply).await?;
    if shutdown {
        return Ok(true);
    }
    if subscribe {
        loop {
            let event_result = tokio::select! {
                result = event_receiver.recv() => Some(result),
                _ = state.shutdown_notify.notified() => None,
            };
            let Some(event_result) = event_result else {
                break;
            };
            match event_result {
                Ok(event) => {
                    let envelope = json!({"v":1,"event":event.event,"seq":event.seq,"task_id":event.task_id,"operation_id":event.operation_id,"payload":event.payload});
                    if let Err(error) = write_frame(&mut stream, &envelope).await {
                        debug!(%error,"event subscriber disconnected");
                        break;
                    }
                }
                Err(broadcast::error::RecvError::Lagged(_)) => {
                    let event = json!({"v":1,"event":"scan_warning","seq":0,"task_id":null,"operation_id":null,"payload":{"code":"event_stream_lagged","message":"event stream overflowed; query list_tasks for current state"}});
                    if write_frame(&mut stream, &event).await.is_err() {
                        break;
                    }
                }
                Err(broadcast::error::RecvError::Closed) => break,
            }
        }
    }
    let _ = id;
    Ok(false)
}

#[cfg(not(windows))]
pub async fn run_pipe_server(_pipe_name: String, _state: Arc<AgentState>) -> Result<()> {
    warn!("named pipe IPC is available only on Windows");
    Err(anyhow!(
        "Windows named pipe support is unavailable on this target"
    ))
}

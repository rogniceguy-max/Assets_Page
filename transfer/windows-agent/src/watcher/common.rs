use super::*;

pub(super) async fn current_config_for_root(
    state: &AgentState,
    root: &RootContext,
) -> Result<AgentConfig> {
    if state.shutting_down.load(Ordering::Acquire) {
        anyhow::bail!("agent is shutting down");
    }
    let config = state
        .config
        .read()
        .await
        .clone()
        .ok_or_else(|| anyhow!("agent logged out"))?;
    let configured = config.sync_roots.iter().any(|candidate| {
        candidate.root_id == root.config.root_id
            && candidate
                .path
                .trim_end_matches(|character| character == '\\' || character == '/')
                .eq_ignore_ascii_case(
                    root.config
                        .path
                        .trim_end_matches(|character| character == '\\' || character == '/'),
                )
            && candidate.logical_path_prefix == root.config.logical_path_prefix
            && candidate.scope_id == root.config.scope_id
            && candidate.project_id == root.config.project_id
    });
    if !configured {
        anyhow::bail!("sync root configuration changed");
    }
    Ok(config)
}

pub(super) async fn find_catalog_entry(
    state: &AgentState,
    root: &RootContext,
    logical_path: &str,
) -> Result<Option<FileCatalogItem>> {
    let config = current_config_for_root(state, root).await?;
    let parent = parent_logical_path(logical_path);
    let mut cursor = None;
    loop {
        let response = api::list_files(
            &state.api,
            &config,
            root.config.scope_id.as_deref().unwrap_or_default(),
            root.config.project_id.clone(),
            &parent,
            cursor.as_deref(),
        )
        .await?;
        if let Some(item) = response
            .items
            .into_iter()
            .find(|item| item.logical_path == logical_path)
        {
            return Ok(Some(item));
        }
        cursor = response.next_cursor.filter(|value| !value.is_empty());
        if cursor.is_none() {
            return Ok(None);
        }
    }
}

pub(super) fn baseline_from_item(
    root: &RootContext,
    item: &FileCatalogItem,
) -> Result<SyncBaseline> {
    Ok(SyncBaseline {
        root_id: root.config.root_id.clone(),
        logical_path: item.logical_path.clone(),
        scope_id: item.scope_id.clone(),
        project_id: item.project_id.clone(),
        file_id: item
            .file_id
            .clone()
            .ok_or_else(|| anyhow!("catalog file has no id"))?,
        revision_id: item
            .current_revision_id
            .clone()
            .ok_or_else(|| anyhow!("catalog file has no current revision"))?,
        version: item.version.unwrap_or(0),
        size: item.size,
        sha256: item.sha256.clone().unwrap_or_default(),
        deleted: false,
        conflicted: false,
    })
}

pub(super) fn queue_download(
    state: &AgentState,
    root: &RootContext,
    item: &FileCatalogItem,
    destination: &Path,
    replace_hash: Option<&str>,
    remote_conflict: bool,
) -> Result<()> {
    let file_id = item
        .file_id
        .clone()
        .ok_or_else(|| anyhow!("download catalog item has no file id"))?;
    let operation_id = Uuid::new_v4().to_string();
    let task = NewTask {
        root_id: Some(root.config.root_id.clone()),
        operation_id: operation_id.clone(),
        kind: TaskKind::Download,
        source_path: None,
        destination_path: Some(destination.to_string_lossy().into_owned()),
        logical_path: Some(item.logical_path.clone()),
        scope_id: Some(item.scope_id.clone()),
        project_id: item.project_id.clone(),
        display_name: destination
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or(&item.name)
            .to_owned(),
        folder_group: if remote_conflict {
            format!("{}-conflicts", root.config.root_id)
        } else {
            root.config.root_id.clone()
        },
        file_id: Some(file_id),
        revision_id: item.current_revision_id.clone(),
        object_id: None,
        expected_version: item.version,
        media_type: item.media_type.clone(),
        source: "sync".into(),
        transfer_url: String::new(),
        ticket: String::new(),
        bytes_total: Some(item.size),
        replace_if_hash: replace_hash.map(str::to_owned),
        batch_parent_id: None,
    };
    suppress_path(state, root, destination)?;
    state.queue.enqueue(&task)?;
    emit_task_queued(state, &task);
    Ok(())
}

pub(super) fn queue_upload(
    state: &AgentState,
    root: &RootContext,
    local: &LocalFile,
    baseline: Option<&SyncBaseline>,
) -> Result<()> {
    if state
        .queue
        .has_active_path(&root.config.root_id, &local.logical_path)?
    {
        return Ok(());
    }
    if baseline.is_some_and(|baseline| baseline.deleted || baseline.conflicted) {
        emit_conflict(
            state,
            root,
            &local.logical_path,
            "local file is in conflict with a server tombstone or unresolved revision",
        );
        return Ok(());
    }
    let operation_id = Uuid::new_v4().to_string();
    let task = NewTask {
        root_id: Some(root.config.root_id.clone()),
        operation_id: operation_id.clone(),
        kind: TaskKind::Upload,
        source_path: Some(local.path.to_string_lossy().into_owned()),
        destination_path: None,
        logical_path: Some(local.logical_path.clone()),
        scope_id: root.config.scope_id.clone(),
        project_id: root.config.project_id.clone(),
        display_name: local
            .path
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("File")
            .to_owned(),
        folder_group: root.config.root_id.clone(),
        file_id: baseline.map(|item| item.file_id.clone()),
        revision_id: baseline.map(|item| item.revision_id.clone()),
        object_id: None,
        expected_version: baseline.map(|item| item.version),
        media_type: media_type(&local.path),
        source: "sync".into(),
        transfer_url: String::new(),
        ticket: String::new(),
        bytes_total: Some(local.size),
        replace_if_hash: None,
        batch_parent_id: None,
    };
    state.queue.enqueue(&task)?;
    emit_task_queued(state, &task);
    Ok(())
}

pub(super) fn pseudo_task(
    operation_id: &str,
    baseline: &SyncBaseline,
    root: &RootContext,
    kind: &str,
) -> TaskRecord {
    let now = unix_now();
    TaskRecord {
        snapshot: TaskSnapshot {
            task_id: operation_id.to_owned(),
            operation_id: operation_id.to_owned(),
            kind: kind.to_owned(),
            source: "sync".into(),
            state: "queued".into(),
            display_name: Path::new(&baseline.logical_path)
                .file_name()
                .and_then(|n| n.to_str())
                .unwrap_or("File")
                .into(),
            folder_group: root.config.root_id.clone(),
            logical_path: Some(baseline.logical_path.clone()),
            file_id: Some(baseline.file_id.clone()),
            revision_id: Some(baseline.revision_id.clone()),
            object_id: None,
            expected_version: Some(baseline.version),
            version: Some(baseline.version),
            batch_id: None,
            bytes_total: Some(baseline.size),
            bytes_done: 0,
            created_at: now,
            updated_at: now,
            attempt: 0,
            retryable: false,
            error_code: None,
            message: None,
            root_id: Some(root.config.root_id.clone()),
            commit_started: false,
        },
        source_path: None,
        destination_path: None,
        scope_id: Some(baseline.scope_id.clone()),
        project_id: baseline.project_id.clone(),
        file_id: Some(baseline.file_id.clone()),
        revision_id: Some(baseline.revision_id.clone()),
        object_id: None,
        object_kind: None,
        expected_version: Some(baseline.version),
        media_type: None,
        source: "sync".into(),
        transfer_url: String::new(),
        ticket: String::new(),
        batch_parent_id: None,
        root_id: Some(root.config.root_id.clone()),
        replace_if_hash: None,
        control_state: None,
        commit_started: false,
    }
}

pub(super) fn is_agent_artifact_name(name: &str) -> bool {
    name.ends_with(".partial")
        || [".local-v", ".remote-v", ".moved-v", ".conflict"]
            .iter()
            .any(|marker| name.contains(marker))
}

pub(super) fn logical_for_local(root: &RootContext, path: &Path) -> Option<String> {
    let relative = path.strip_prefix(&root.canonical).ok()?;
    let tail = relative
        .components()
        .map(|component| component.as_os_str().to_string_lossy())
        .collect::<Vec<_>>()
        .join("/");
    if tail.is_empty() {
        return None;
    }
    let prefix = root.config.logical_path_prefix.trim_matches('/');
    Some(if prefix.is_empty() {
        tail
    } else {
        format!("{prefix}/{tail}")
    })
}

pub(super) fn local_for_logical(root: &RootContext, logical_path: &str) -> Result<PathBuf> {
    let prefix = root.config.logical_path_prefix.trim_matches('/');
    let relative = if logical_path == prefix {
        String::new()
    } else if prefix.is_empty() {
        logical_path.to_owned()
    } else {
        logical_path
            .strip_prefix(prefix)
            .and_then(|suffix| suffix.strip_prefix('/'))
            .ok_or_else(|| anyhow!("logical path is outside this sync root"))?
            .to_owned()
    };
    let mut path = root.canonical.clone();
    for component in relative.split('/').filter(|part| !part.is_empty()) {
        if component == "." || component == ".." || component.contains(':') {
            anyhow::bail!("unsafe logical path component");
        }
        path.push(component);
    }
    if !path.starts_with(&root.canonical) {
        anyhow::bail!("local destination escaped configured sync root");
    }
    ensure_safe_local_path(root, &path)?;
    Ok(path)
}

pub(super) fn is_reparse_point(metadata: &std::fs::Metadata) -> bool {
    if metadata.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        return metadata.file_attributes() & 0x400 != 0;
    }
    #[cfg(not(windows))]
    {
        false
    }
}

pub(super) fn ensure_safe_local_path(root: &RootContext, path: &Path) -> Result<()> {
    if !path.starts_with(&root.canonical) {
        anyhow::bail!("local path escaped configured sync root");
    }
    let mut current = root.canonical.clone();
    if let Ok(metadata) = std::fs::symlink_metadata(&current) {
        if is_reparse_point(&metadata) {
            anyhow::bail!("sync root is a reparse point");
        }
    }
    for component in path.strip_prefix(&root.canonical)?.components() {
        if !matches!(component, std::path::Component::Normal(_)) {
            anyhow::bail!("local path contains an unsafe component");
        }
        current.push(component);
        match std::fs::symlink_metadata(&current) {
            Ok(metadata) if is_reparse_point(&metadata) => {
                anyhow::bail!("local path ancestry contains a reparse point")
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => break,
            Err(error) => return Err(error.into()),
        }
    }
    Ok(())
}

pub(super) fn reject_reparse_root_ancestry(path: &Path) -> Result<()> {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()?.join(path)
    };
    let mut normalized = PathBuf::new();
    for component in absolute.components() {
        match component {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                normalized.pop();
            }
            other => normalized.push(other.as_os_str()),
        }
    }
    let mut ancestors: Vec<_> = normalized.ancestors().collect();
    ancestors.reverse();
    for ancestor in ancestors {
        let metadata = std::fs::symlink_metadata(ancestor)?;
        if is_reparse_point(&metadata) {
            anyhow::bail!("sync root ancestry contains a reparse point: {}", ancestor.display());
        }
    }
    Ok(())
}

pub(super) fn belongs_to_root(
    root: &RootContext,
    scope: Option<&str>,
    project: Option<&str>,
    logical_path: &str,
) -> bool {
    let scope_matches = root
        .config
        .scope_id
        .as_deref()
        .is_none_or(|value| Some(value) == scope);
    let project_matches = root
        .config
        .project_id
        .as_deref()
        .is_none_or(|value| Some(value) == project);
    let prefix = root.config.logical_path_prefix.trim_matches('/');
    let path = logical_path.trim_matches('/');
    scope_matches
        && project_matches
        && (prefix.is_empty()
            || path == prefix
            || path
                .strip_prefix(prefix)
                .is_some_and(|rest| rest.starts_with('/')))
}

pub(super) fn needs_local_hash(
    previous: Option<&SyncBaseline>,
    file_id: &str,
    remote_sha256: &str,
    local_size: u64,
    local_modified_ns: i64,
    fingerprint: Option<&(u64, i64)>,
) -> bool {
    let Some(previous) = previous else {
        return true;
    };
    if previous.deleted
        || previous.conflicted
        || previous.file_id != file_id
        || !previous.sha256.eq_ignore_ascii_case(remote_sha256)
    {
        return true;
    }
    fingerprint != Some(&(local_size, local_modified_ns))
}

pub(super) fn parent_logical_path(path: &str) -> String {
    path.rsplit_once('/')
        .map(|(parent, _)| parent.to_owned())
        .unwrap_or_default()
}

pub(super) fn media_type(path: &Path) -> Option<String> {
    let extension = path.extension()?.to_str()?.to_ascii_lowercase();
    Some(
        match extension.as_str() {
            "jpg" | "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "mp4" => "video/mp4",
            "mov" => "video/quicktime",
            "mkv" => "video/x-matroska",
            "webm" => "video/webm",
            "pdf" => "application/pdf",
            "txt" => "text/plain",
            "json" => "application/json",
            "csv" => "text/csv",
            _ => "application/octet-stream",
        }
        .to_owned(),
    )
}

pub(super) fn hash_local(path: &Path) -> Result<String> {
    use std::io::Read;
    let mut file = std::fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = vec![0_u8; 1024 * 1024];
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
    }
    Ok(hex::encode(hasher.finalize()))
}

pub(super) fn modified_ns(metadata: &std::fs::Metadata) -> i64 {
    metadata
        .modified()
        .ok()
        .and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
        .map(|duration| duration.as_nanos().min(i64::MAX as u128) as i64)
        .unwrap_or(0)
}

pub(super) async fn hash_local_stable(path: &Path) -> Result<(u64, String)> {
    reject_reparse_root_ancestry(path)?;
    for _ in 0..8 {
        let before = tokio::fs::metadata(path).await?;
        sleep(Duration::from_millis(350)).await;
        let after = tokio::fs::metadata(path).await?;
        if before.len() != after.len() || before.modified().ok() != after.modified().ok() {
            continue;
        }
        let hash = hash_local(path)?;
        let final_meta = tokio::fs::metadata(path).await?;
        if final_meta.len() == after.len() && final_meta.modified().ok() == after.modified().ok() {
            return Ok((final_meta.len(), hash));
        }
    }
    Err(anyhow!("file kept changing during sync scan"))
}

pub(super) fn suppress_path(state: &AgentState, root: &RootContext, path: &Path) -> Result<()> {
    state.queue.suppress_path(
        &root.config.root_id,
        &path.to_string_lossy(),
        unix_now() + 15,
    )
}

pub(super) async fn remove_empty_local_parents(root: &RootContext, path: &Path) {
    let mut parent = path.parent();
    while let Some(directory) = parent {
        if directory == root.canonical {
            break;
        }
        let empty = std::fs::read_dir(directory)
            .ok()
            .is_some_and(|mut entries| entries.next().is_none());
        if !empty {
            break;
        }
        if tokio::fs::remove_dir(directory).await.is_err() {
            break;
        }
        parent = directory.parent();
    }
}

pub(super) fn conflict_path(path: &Path, label: &str, version: i64) -> PathBuf {
    let stem = path.file_stem().and_then(|v| v.to_str()).unwrap_or("file");
    let extension = path
        .extension()
        .and_then(|v| v.to_str())
        .map(|value| format!(".{value}"))
        .unwrap_or_default();
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    for index in 0..CONFLICT_COPY_LIMIT {
        let suffix = if index == 0 {
            String::new()
        } else {
            format!("-{index}")
        };
        let candidate = parent.join(format!("{stem}.{label}-v{version}{suffix}{extension}"));
        if !candidate.exists() {
            return candidate;
        }
    }
    parent.join(format!(
        "{stem}.{label}-v{version}-{}.conflict{extension}",
        &Uuid::new_v4().simple().to_string()[..8]
    ))
}

pub(super) fn emit_warning(state: &AgentState, root_id: &str, code: &str, message: &str) {
    state.emit(AgentEvent {
        event: "scan_warning".into(),
        seq: 0,
        task_id: None,
        operation_id: None,
        payload: json!({"root_id":root_id,"code":code,"message":message}),
    });
}

pub(super) fn emit_conflict(
    state: &AgentState,
    root: &RootContext,
    logical_path: &str,
    message: &str,
) {
    emit_warning(
        state,
        &root.config.root_id,
        "sync_conflict",
        &format!("{logical_path}: {message}"),
    );
}

pub(super) fn emit_task_queued(state: &AgentState, task: &NewTask) {
    let snapshot = state.queue.list_tasks().ok().and_then(|tasks| {
        tasks
            .into_iter()
            .find(|item| item.operation_id == task.operation_id)
    });
    state.emit(AgentEvent {
        event: "task_state".into(),
        seq: 0,
        task_id: Some(task.operation_id.clone()),
        operation_id: Some(task.operation_id.clone()),
        payload: json!({"state":"queued","task":snapshot}),
    });
}

pub(super) fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

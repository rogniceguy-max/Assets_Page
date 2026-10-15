use super::*;
use super::{common::*, feed::poll_changes, reconcile::full_reconcile};

enum SyncWork {
    Poll(HashMap<String, RootContext>),
    Reconcile(RootContext, &'static str),
}

#[derive(Default)]
struct SyncWorkState {
    poll_pending: AtomicBool,
    reconcile_pending: std::sync::Mutex<HashSet<String>>,
}

pub async fn run(state: Arc<AgentState>, mut commands: mpsc::Receiver<WatcherCommand>) {
    let (event_tx, mut event_rx) = mpsc::channel::<notify::Result<Event>>(4096);
    let (sync_work_tx, sync_work_rx) = mpsc::unbounded_channel();
    let sync_work_state = Arc::new(SyncWorkState::default());
    tokio::spawn(run_sync_worker(
        state.clone(),
        sync_work_rx,
        sync_work_state.clone(),
    ));
    let overflow = Arc::new(AtomicBool::new(false));
    let mut watcher: Option<RecommendedWatcher> = None;
    let mut roots: HashMap<String, RootContext> = HashMap::new();
    let mut feed_poll = interval(FEED_POLL);
    let mut reconcile_poll = interval(RECONCILE_INTERVAL);
    reconcile_poll.tick().await;

    loop {
        if state
            .shutting_down
            .load(std::sync::atomic::Ordering::Acquire)
        {
            return;
        }
        tokio::select! {
            command=commands.recv()=>match command {
                Some(WatcherCommand::Configure(configured))=>{
                    watcher.take();
                    roots.clear();
                    match install_watchers(configured,&event_tx,overflow.clone()) {
                        Ok((next,contexts))=>{
                            watcher=Some(next);
                            roots=contexts;
                            schedule_poll(&sync_work_tx, &sync_work_state, roots.clone());
                        }
                        Err(error)=>emit_warning(&state,"","watcher_start_failed",&error.to_string()),
                    }
                }
                Some(WatcherCommand::Rescan(root_id))=>{
                    if root_id=="*"{
                        schedule_reconcile(&sync_work_tx, &sync_work_state,
                            roots.values().cloned(), "rescan_failed");
                    }else if let Some(root)=roots.get(&root_id){
                        schedule_reconcile(&sync_work_tx, &sync_work_state,
                            std::iter::once(root.clone()), "rescan_failed");
                    }else{emit_warning(&state,&root_id,"unknown_sync_root","the requested sync root is not configured");}
                }
                Some(WatcherCommand::Logout(acknowledged))=>{
                    watcher.take();
                    roots.clear();
                    let _ = acknowledged.send(());
                }
                Some(WatcherCommand::Shutdown)=>return,
                None=>return,
            },
            event=event_rx.recv()=>if let Some(event)=event{
                match event{
                    Ok(event)=>{
                        if let Err(error)=record_event(&state,&roots,event).await{
                            warn!(%error,"could not persist filesystem notification");
                            overflow.store(true,Ordering::Release);
                        }
                    }
                    Err(error)=>{
                        emit_warning(&state,"","watcher_overflow_or_error",&error.to_string());
                        overflow.store(true,Ordering::Release);
                    }
                }
            },
            _=feed_poll.tick()=>{
                if overflow.swap(false,Ordering::AcqRel){
                    emit_warning(&state,"","watcher_rescan","filesystem notifications may have been lost; reconciling configured roots");
                    schedule_reconcile(&sync_work_tx, &sync_work_state,
                        roots.values().cloned(), "rescan_failed");
                }
                if !roots.is_empty(){
                    schedule_poll(&sync_work_tx, &sync_work_state, roots.clone());
                }
            }
            _=reconcile_poll.tick()=>{
                schedule_reconcile(&sync_work_tx, &sync_work_state,
                    roots.values().cloned(), "periodic_reconcile_failed");
            }
        }
    }
}

fn schedule_reconcile(
    sender: &mpsc::UnboundedSender<SyncWork>,
    state: &SyncWorkState,
    roots: impl IntoIterator<Item = RootContext>,
    event: &'static str,
) {
    for root in roots {
        let root_id = root.config.root_id.clone();
        let inserted = mark_reconcile_pending(state, &root_id);
        if inserted && sender.send(SyncWork::Reconcile(root, event)).is_err() {
            clear_reconcile_pending(state, &root_id);
            return;
        }
    }
}

fn mark_reconcile_pending(state: &SyncWorkState, root_id: &str) -> bool {
    state.reconcile_pending.lock()
        .map(|mut pending| pending.insert(root_id.to_owned()))
        .unwrap_or(false)
}

fn clear_reconcile_pending(state: &SyncWorkState, root_id: &str) {
    if let Ok(mut pending) = state.reconcile_pending.lock() {
        pending.remove(root_id);
    }
}

fn schedule_poll(
    sender: &mpsc::UnboundedSender<SyncWork>,
    state: &SyncWorkState,
    roots: HashMap<String, RootContext>,
) {
    if roots.is_empty() || state.poll_pending.swap(true, Ordering::AcqRel) {
        return;
    }
    if sender.send(SyncWork::Poll(roots)).is_err() {
        state.poll_pending.store(false, Ordering::Release);
    }
}

async fn run_sync_worker(
    state: Arc<AgentState>,
    mut work: mpsc::UnboundedReceiver<SyncWork>,
    work_state: Arc<SyncWorkState>,
) {
    while let Some(item) = work.recv().await {
        if state.shutting_down.load(Ordering::Acquire) {
            break;
        }
        match item {
            SyncWork::Poll(roots) => {
                let result = async {
                    let mut active = HashMap::new();
                    for (root_id, root) in roots {
                        if current_config_for_root(&state, &root).await.is_ok() {
                            active.insert(root_id, root);
                        }
                    }
                    if active.is_empty() {
                        return Ok(());
                    }
                    process_pending_events(&state, &active, &work_state).await?;
                    if let Some((config, sequence)) = poll_changes(&state, &active).await? {
                        for root in active.values() {
                            full_reconcile(&state, root).await?;
                        }
                        state.queue.set_sync_cursor(&config.client_id, sequence)?;
                        api::acknowledge_changes(&state.api, &config, sequence).await?;
                    }
                    Ok::<(), anyhow::Error>(())
                }.await;
                work_state.poll_pending.store(false, Ordering::Release);
                if let Err(error) = result {
                    warn!(%error, "background sync work failed; it will retry");
                }
            }
            SyncWork::Reconcile(root, warning) => {
                let result = if current_config_for_root(&state, &root).await.is_ok() {
                    full_reconcile(&state, &root).await
                } else {
                    Ok(())
                };
                clear_reconcile_pending(&work_state, &root.config.root_id);
                if let Err(error) = result {
                    if error.to_string() != "agent logged out"
                        && error.to_string() != "sync root configuration changed"
                    {
                        emit_warning(&state, &root.config.root_id, warning, &error.to_string());
                    }
                }
            }
        }
    }
}

pub(super) fn install_watchers(
    configured: Vec<SyncRoot>,
    sender: &mpsc::Sender<notify::Result<Event>>,
    overflow: Arc<AtomicBool>,
) -> Result<(RecommendedWatcher, HashMap<String, RootContext>)> {
    let event_sender = sender.clone();
    let overflow_sender = overflow.clone();
    let mut watcher = notify::recommended_watcher(move |event: notify::Result<Event>| {
        if event_sender.try_send(event).is_err() {
            overflow_sender.store(true, Ordering::Release);
        }
    })?;
    let mut roots = HashMap::new();
    for root in configured {
        if root.root_id.trim().is_empty() || root.logical_path_prefix.trim().is_empty() {
            anyhow::bail!("sync root is missing root_id or logical_path_prefix");
        }
        if root.scope_id.as_deref().unwrap_or_default().is_empty() {
            anyhow::bail!("sync root {} has no scope_id", root.root_id);
        }
        reject_reparse_root_ancestry(Path::new(&root.path))?;
        let canonical = std::fs::canonicalize(&root.path)
            .with_context(|| format!("canonicalize sync root {}", root.path))?;
        if !canonical.is_dir() {
            anyhow::bail!("sync root is not a directory: {}", canonical.display());
        }
        watcher.watch(&canonical, RecursiveMode::Recursive)?;
        roots.insert(
            root.root_id.clone(),
            RootContext {
                config: root,
                canonical,
            },
        );
    }
    Ok((watcher, roots))
}

pub(super) async fn record_event(
    state: &AgentState,
    roots: &HashMap<String, RootContext>,
    event: Event,
) -> Result<()> {
    if matches!(event.kind, EventKind::Access(_)) {
        return Ok(());
    }
    if matches!(event.kind, EventKind::Any | EventKind::Other) {
        emit_warning(
            state,
            "",
            "watcher_rescan",
            "watcher delivered an unspecified event; a rescan was scheduled",
        );
        // Insert an explicit marker that the poll loop recognizes as a rescan request.
        for root in roots.values() {
            state.queue.record_watcher_event(
                &root.config.root_id,
                &root.canonical.to_string_lossy(),
                "rescan",
                None,
            )?;
        }
        return Ok(());
    }
    if let EventKind::Modify(ModifyKind::Name(RenameMode::Both)) = event.kind {
        if event.paths.len() >= 2 {
            let old = &event.paths[0];
            let new = &event.paths[1];
            let old_root = find_root(roots, old);
            let new_root = find_root(roots, new);
            if let (Some(source_root), Some(target_root)) = (old_root, new_root) {
                if source_root.config.root_id != target_root.config.root_id {
                    if state
                        .queue
                        .path_is_suppressed(&source_root.config.root_id, &old.to_string_lossy())?
                        || state
                            .queue
                            .path_is_suppressed(&target_root.config.root_id, &new.to_string_lossy())?
                    {
                        return Ok(());
                    }
                    state.queue.record_watcher_event(
                        &source_root.config.root_id,
                        &old.to_string_lossy(),
                        "cross_root_rename",
                        Some(&new.to_string_lossy()),
                    )?;
                    return Ok(());
                }
            }
            if old_root.is_none() {
                if let Some(root) = new_root {
                    state.queue.record_watcher_event(
                        &root.config.root_id,
                        &new.to_string_lossy(),
                        "change",
                        None,
                    )?;
                }
                return Ok(());
            }
            if let Some(root) = old_root.or(new_root) {
                if state
                    .queue
                    .path_is_suppressed(&root.config.root_id, &old.to_string_lossy())?
                    || state
                        .queue
                        .path_is_suppressed(&root.config.root_id, &new.to_string_lossy())?
                {
                    return Ok(());
                }
                state.queue.record_watcher_event(
                    &root.config.root_id,
                    &old.to_string_lossy(),
                    "rename",
                    Some(&new.to_string_lossy()),
                )?;
            }
            return Ok(());
        }
    }
    if matches!(
        event.kind,
        EventKind::Modify(ModifyKind::Name(RenameMode::From | RenameMode::To))
    ) {
        // Some Windows rename sequences arrive as separate old/new notifications. A full
        // reconcile pairs unique baseline hashes; never treat an unmatched old path as delete.
        for root in roots.values() {
            state.queue.record_watcher_event(
                &root.config.root_id,
                &root.canonical.to_string_lossy(),
                "rescan",
                None,
            )?;
        }
        return Ok(());
    }
    let kind = match event.kind {
        EventKind::Create(_) => "change",
        EventKind::Remove(_) => "delete",
        EventKind::Modify(_) => "change",
        _ => return Ok(()),
    };
    for path in event.paths {
        let Some(root) = find_root(roots, &path) else {
            continue;
        };
        if state
            .queue
            .path_is_suppressed(&root.config.root_id, &path.to_string_lossy())?
        {
            continue;
        }
        state.queue.record_watcher_event(
            &root.config.root_id,
            &path.to_string_lossy(),
            kind,
            None,
        )?;
    }
    Ok(())
}

pub(super) fn find_root<'a>(
    roots: &'a HashMap<String, RootContext>,
    path: &Path,
) -> Option<&'a RootContext> {
    roots
        .values()
        .filter(|root| path.starts_with(&root.canonical))
        .max_by_key(|root| root.canonical.components().count())
}

async fn process_pending_events(
    state: &Arc<AgentState>,
    roots: &HashMap<String, RootContext>,
    work_state: &SyncWorkState,
) -> Result<()> {
    let threshold = unix_now() - EVENT_DEBOUNCE.as_secs() as i64;
    let events = state.queue.pending_watcher_events(threshold)?;
    for (root_id, path, kind, other_path) in events {
        let Some(root) = roots.get(&root_id) else {
            state.queue.remove_watcher_event(&root_id, &path)?;
            continue;
        };
        if current_config_for_root(state, root).await.is_err() {
            state.queue.remove_watcher_event(&root_id, &path)?;
            continue;
        }
        if kind == "rescan" {
            let inserted = mark_reconcile_pending(work_state, &root.config.root_id);
            let result = if inserted { full_reconcile(state, root).await } else { Ok(()) };
            if inserted {
                clear_reconcile_pending(work_state, &root.config.root_id);
            }
            result?;
            state.queue.remove_watcher_event(&root_id, &path)?;
            continue;
        }
        let path = PathBuf::from(path);
        let path_is_artifact = path
            .file_name()
            .and_then(|value| value.to_str())
            .is_some_and(is_agent_artifact_name);
        let other_is_artifact = other_path.as_deref().is_some_and(|value| {
            Path::new(value)
                .file_name()
                .and_then(|name| name.to_str())
                .is_some_and(is_agent_artifact_name)
        });
        if path_is_artifact || other_is_artifact {
            state
                .queue
                .remove_watcher_event(&root_id, &path.to_string_lossy())?;
            continue;
        }
        if kind == "cross_root_rename" {
            let Some(other) = other_path else {
                state.queue.remove_watcher_event(&root_id, &path.to_string_lossy())?;
                continue;
            };
            let next = PathBuf::from(other);
            let Some(target_root) = find_root(roots, &next) else {
                state.queue.remove_watcher_event(&root_id, &path.to_string_lossy())?;
                continue;
            };
            let Some(relative) = logical_for_local(root, &path) else {
                state.queue.remove_watcher_event(&root_id, &path.to_string_lossy())?;
                continue;
            };
            if process_cross_root_rename(state, root, &relative, target_root, &next).await? {
                state.queue.remove_watcher_event(&root_id, &path.to_string_lossy())?;
            }
            continue;
        }
        let relative = match logical_for_local(root, &path) {
            Some(value) => value,
            None => {
                state
                    .queue
                    .remove_watcher_event(&root_id, &path.to_string_lossy())?;
                continue;
            }
        };
        let completed = match kind.as_str() {
            "rename" => {
                let Some(other) = other_path else {
                    state
                        .queue
                        .remove_watcher_event(&root_id, &path.to_string_lossy())?;
                    continue;
                };
                let next = PathBuf::from(other);
                process_local_rename(state, root, &relative, &next).await?
            }
            "delete" => process_local_delete(state, root, &path, &relative).await?,
            _ => process_local_change(state, root, &path, &relative).await?,
        };
        if completed {
            state
                .queue
                .remove_watcher_event(&root_id, &path.to_string_lossy())?;
        }
    }
    Ok(())
}

pub(super) async fn process_local_change(
    state: &Arc<AgentState>,
    root: &RootContext,
    path: &Path,
    logical_path: &str,
) -> Result<bool> {
    if path
        .file_name()
        .and_then(|value| value.to_str())
        .is_some_and(is_agent_artifact_name)
    {
        return Ok(true);
    }
    let metadata = match std::fs::symlink_metadata(path) {
        Ok(meta) => meta,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return process_local_delete(state, root, path, logical_path).await;
        }
        Err(error) => return Err(error.into()),
    };
    if is_reparse_point(&metadata) {
        emit_warning(
            state,
            &root.config.root_id,
            "sync_symlink_ignored",
            "symbolic links are not uploaded",
        );
        return Ok(true);
    }
    if metadata.is_dir() {
        let Some(scope_id) = root.config.scope_id.clone() else {
            return Ok(true);
        };
        let config = state
            .config
            .read()
            .await
            .clone()
            .ok_or_else(|| anyhow!("agent logged out"))?;
        api::create_directory(
            &state.api,
            &config,
            &Uuid::new_v4().to_string(),
            &scope_id,
            root.config.project_id.as_deref(),
            logical_path,
        )
        .await?;
        return Ok(true);
    }
    if !metadata.is_file() {
        return Ok(true);
    }
    if state
        .queue
        .has_active_path(&root.config.root_id, logical_path)?
    {
        return Ok(false);
    }
    let baseline = state.queue.baseline(&root.config.root_id, logical_path)?;
    if let Some(baseline) = baseline {
        if baseline.deleted || baseline.conflicted {
            emit_conflict(
                state,
                root,
                logical_path,
                "local change targets a deleted or conflicted file; it was not uploaded",
            );
            return Ok(true);
        }
    }
    let operation_id = Uuid::new_v4().to_string();
    let baseline = state.queue.baseline(&root.config.root_id, logical_path)?;
    let new_task = NewTask {
        root_id: Some(root.config.root_id.clone()),
        operation_id: operation_id.clone(),
        kind: TaskKind::Upload,
        source_path: Some(path.to_string_lossy().into_owned()),
        destination_path: None,
        logical_path: Some(logical_path.to_owned()),
        scope_id: root.config.scope_id.clone(),
        project_id: root.config.project_id.clone(),
        display_name: path
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("File")
            .to_owned(),
        folder_group: root.config.root_id.clone(),
        file_id: baseline.as_ref().map(|b| b.file_id.clone()),
        revision_id: baseline.as_ref().map(|b| b.revision_id.clone()),
        object_id: None,
        expected_version: baseline.as_ref().map(|b| b.version),
        media_type: media_type(path),
        source: "sync".into(),
        transfer_url: String::new(),
        ticket: String::new(),
        bytes_total: Some(metadata.len()),
        replace_if_hash: None,
        batch_parent_id: None,
    };
    state.queue.enqueue(&new_task)?;
    emit_task_queued(state, &new_task);
    Ok(true)
}

pub(super) async fn process_local_delete(
    state: &Arc<AgentState>,
    root: &RootContext,
    path: &Path,
    logical_path: &str,
) -> Result<bool> {
    let metadata = std::fs::symlink_metadata(path).ok();
    let remote_entry = find_catalog_entry(state, root, logical_path).await?;
    if remote_entry
        .as_ref()
        .is_some_and(|entry| entry.is_directory)
        || metadata.as_ref().is_some_and(|m| m.is_dir())
    {
        if let Some(entry) = remote_entry.filter(|entry| entry.is_directory) {
            if let (Some(directory_id), Some(version)) =
                (entry.directory_id.as_deref(), entry.version)
            {
                let config = state
                    .config
                    .read()
                    .await
                    .clone()
                    .ok_or_else(|| anyhow!("agent logged out"))?;
                api::delete_directory(
                    &state.api,
                    &config,
                    directory_id,
                    &Uuid::new_v4().to_string(),
                    version,
                )
                .await?;
                return Ok(true);
            }
        }
        // If a local directory has no logical directory record, only remove known file entries.
        let baselines = state
            .queue
            .baselines_under_prefix(&root.config.root_id, logical_path)?;
        for baseline in baselines
            .into_iter()
            .filter(|item| !item.deleted && !item.conflicted)
        {
            if state
                .queue
                .has_active_path(&root.config.root_id, &baseline.logical_path)?
            {
                continue;
            }
            queue_delete_task(state, root, &baseline)?;
        }
        return Ok(true);
    }
    if let Some(baseline) = state.queue.baseline(&root.config.root_id, logical_path)? {
        if baseline.deleted {
            return Ok(true);
        }
        if baseline.conflicted {
            emit_conflict(
                state,
                root,
                logical_path,
                "local deletion conflicts with an unresolved file change",
            );
            return Ok(true);
        }
        if !state
            .queue
            .has_active_path(&root.config.root_id, logical_path)?
        {
            queue_delete_task(state, root, &baseline)?;
        }
    }
    Ok(true)
}

async fn process_cross_root_rename(
    state: &Arc<AgentState>,
    source_root: &RootContext,
    old_logical: &str,
    target_root: &RootContext,
    new_path: &Path,
) -> Result<bool> {
    let Some(new_logical) = logical_for_local(target_root, new_path) else {
        return Ok(false);
    };
    ensure_safe_local_path(target_root, new_path)?;
    let metadata = match std::fs::symlink_metadata(new_path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error.into()),
    };
    if is_reparse_point(&metadata) {
        emit_conflict(
            state,
            source_root,
            old_logical,
            "cross-root rename targets a reparse point; no server move was made",
        );
        return Ok(true);
    }
    if state
        .queue
        .baseline(&target_root.config.root_id, &new_logical)?
        .is_some_and(|baseline| !baseline.deleted)
    {
        emit_conflict(
            state,
            target_root,
            &new_logical,
            "cross-root rename target already has a server baseline",
        );
        return Ok(true);
    }
    let target_scope = target_root
        .config
        .scope_id
        .as_deref()
        .ok_or_else(|| anyhow!("target sync root has no scope_id"))?;
    let config = state
        .config
        .read()
        .await
        .clone()
        .ok_or_else(|| anyhow!("agent logged out"))?;

    if metadata.is_dir() {
        let Some(entry) = find_catalog_entry(state, source_root, old_logical).await? else {
            emit_conflict(
                state,
                source_root,
                old_logical,
                "cross-root directory rename has no matching server directory",
            );
            return Ok(true);
        };
        let Some(directory_id) = entry.directory_id else {
            emit_conflict(
                state,
                source_root,
                old_logical,
                "cross-root directory rename did not resolve to a server directory",
            );
            return Ok(true);
        };
        let version = entry
            .version
            .ok_or_else(|| anyhow!("catalog directory is missing version"))?;
        api::move_directory(
            &state.api,
            &config,
            &directory_id,
            &Uuid::new_v4().to_string(),
            version,
            target_scope,
            target_root.config.project_id.as_deref(),
            &new_logical,
        )
        .await?;
        state.queue.move_baseline_prefix_to_root(
            &source_root.config.root_id,
            old_logical,
            &target_root.config.root_id,
            &new_logical,
            target_scope,
            target_root.config.project_id.as_deref(),
        )?;
        return Ok(true);
    }
    if !metadata.is_file() {
        return Ok(true);
    }

    let Some(baseline) = state
        .queue
        .baseline(&source_root.config.root_id, old_logical)?
    else {
        let remote = find_catalog_entry(state, source_root, old_logical).await?;
        if remote.is_some() {
            emit_conflict(
                state,
                source_root,
                old_logical,
                "cross-root rename has a server entry but no local baseline",
            );
            return Ok(true);
        }
        let local = LocalFile {
            path: new_path.to_path_buf(),
            logical_path: new_logical,
            size: metadata.len(),
            modified_ns: modified_ns(&metadata),
        };
        queue_upload(state, target_root, &local, None)?;
        return Ok(true);
    };
    if baseline.deleted || baseline.conflicted {
        emit_conflict(
            state,
            source_root,
            old_logical,
            "cross-root rename conflicts with a deleted or conflicted baseline",
        );
        return Ok(true);
    }

    let (size, hash) = hash_local_stable(new_path).await?;
    let move_id = Uuid::new_v4().to_string();
    let task = pseudo_task(&move_id, &baseline, source_root, "move");
    let moved = api::move_file(
        &state.api,
        &config,
        &task,
        &new_logical,
        target_scope,
        target_root.config.project_id.as_deref(),
    )
    .await?;
    let mut old = baseline.clone();
    old.deleted = true;
    old.conflicted = false;
    state.queue.upsert_baseline(&old)?;
    let mut current = baseline.clone();
    current.root_id = target_root.config.root_id.clone();
    current.logical_path = new_logical.clone();
    current.scope_id = target_scope.to_owned();
    current.project_id = target_root.config.project_id.clone();
    current.version = moved.version.unwrap_or(baseline.version.saturating_add(1));
    current.deleted = false;
    current.conflicted = false;
    state.queue.upsert_baseline(&current)?;
    state
        .queue
        .remove_local_fingerprint(&source_root.config.root_id, old_logical)?;
    if hash.eq_ignore_ascii_case(&baseline.sha256) {
        state.queue.set_local_fingerprint(
            &target_root.config.root_id,
            &new_logical,
            size,
            modified_ns(&metadata),
        )?;
    } else {
        let local = LocalFile {
            path: new_path.to_path_buf(),
            logical_path: new_logical,
            size,
            modified_ns: modified_ns(&metadata),
        };
        queue_upload(state, target_root, &local, Some(&current))?;
    }
    Ok(true)
}

pub(super) fn queue_delete_task(
    state: &AgentState,
    root: &RootContext,
    baseline: &SyncBaseline,
) -> Result<String> {
    let operation_id = Uuid::new_v4().to_string();
    let task = NewTask {
        root_id: Some(root.config.root_id.clone()),
        operation_id: operation_id.clone(),
        kind: TaskKind::Delete,
        source_path: None,
        destination_path: None,
        logical_path: Some(baseline.logical_path.clone()),
        scope_id: Some(baseline.scope_id.clone()),
        project_id: baseline.project_id.clone(),
        display_name: Path::new(&baseline.logical_path)
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("File")
            .to_owned(),
        folder_group: root.config.root_id.clone(),
        file_id: Some(baseline.file_id.clone()),
        revision_id: Some(baseline.revision_id.clone()),
        object_id: None,
        expected_version: Some(baseline.version),
        media_type: None,
        source: "sync".into(),
        transfer_url: String::new(),
        ticket: String::new(),
        bytes_total: Some(baseline.size),
        replace_if_hash: None,
        batch_parent_id: None,
    };
    state.queue.enqueue(&task)?;
    emit_task_queued(state, &task);
    Ok(operation_id)
}

pub(super) async fn process_local_rename(
    state: &Arc<AgentState>,
    root: &RootContext,
    old_logical: &str,
    new_path: &Path,
) -> Result<bool> {
    let Some(new_logical) = logical_for_local(root, new_path) else {
        let entry = find_catalog_entry(state, root, old_logical).await?;
        if let Some(entry) = entry.filter(|entry| entry.is_directory) {
            if let (Some(directory_id), Some(version)) =
                (entry.directory_id.as_deref(), entry.version)
            {
                let config = state
                    .config
                    .read()
                    .await
                    .clone()
                    .ok_or_else(|| anyhow!("agent logged out"))?;
                api::delete_directory(
                    &state.api,
                    &config,
                    directory_id,
                    &Uuid::new_v4().to_string(),
                    version,
                )
                .await?;
            }
        } else if let Some(baseline) = state.queue.baseline(&root.config.root_id, old_logical)? {
            if !baseline.deleted {
                queue_delete_task(state, root, &baseline)?;
            }
        }
        return Ok(true);
    };
    let metadata = match std::fs::symlink_metadata(new_path) {
        Ok(meta) => meta,
        Err(_) => return Ok(false),
    };
    if is_reparse_point(&metadata) {
        emit_conflict(
            state,
            root,
            old_logical,
            "renamed path is a symbolic link; no server move was made",
        );
        return Ok(true);
    }
    if metadata.is_dir() {
        let Some(entry) = find_catalog_entry(state, root, old_logical).await? else {
            return Ok(false);
        };
        let Some(directory_id) = entry.directory_id else {
            return Ok(false);
        };
        let config = state
            .config
            .read()
            .await
            .clone()
            .ok_or_else(|| anyhow!("agent logged out"))?;
        let operation_id = Uuid::new_v4().to_string();
        let version = entry
            .version
            .ok_or_else(|| anyhow!("catalog directory is missing version"))?;
        let moved = api::move_directory(
            &state.api,
            &config,
            &directory_id,
            &operation_id,
            version,
            &entry.scope_id,
            entry.project_id.as_deref(),
            &new_logical,
        )
        .await?;
        state
            .queue
            .move_baseline_prefix(&root.config.root_id, old_logical, &new_logical)?;
        return Ok(true);
    }
    let Some(baseline) = state.queue.baseline(&root.config.root_id, old_logical)? else {
        emit_conflict(
            state,
            root,
            old_logical,
            "rename could not be matched to a server file baseline",
        );
        return Ok(true);
    };
    if baseline.deleted || baseline.conflicted {
        emit_conflict(
            state,
            root,
            old_logical,
            "rename conflicts with a deleted or conflicted file",
        );
        return Ok(true);
    }
    let target = state.queue.baseline(&root.config.root_id, &new_logical)?;
    if target.as_ref().is_some_and(|entry| !entry.deleted) {
        emit_conflict(
            state,
            root,
            &new_logical,
            "rename target already has a server file",
        );
        return Ok(true);
    }
    let (size, hash) = hash_local_stable(new_path).await?;
    let move_id = Uuid::new_v4().to_string();
    let task = pseudo_task(&move_id, &baseline, root, "move");
    let config = state
        .config
        .read()
        .await
        .clone()
        .ok_or_else(|| anyhow!("agent logged out"))?;
    let moved = api::move_file(
        &state.api,
        &config,
        &task,
        &new_logical,
        root.config.scope_id.as_deref().unwrap_or_default(),
        root.config.project_id.as_deref(),
    )
    .await?;
    let new_version = moved.version.unwrap_or(baseline.version + 1);
    let mut old = baseline.clone();
    old.deleted = true;
    old.conflicted = false;
    state.queue.upsert_baseline(&old)?;
    let mut current = baseline.clone();
    current.logical_path = new_logical.clone();
    current.version = new_version;
    current.deleted = false;
    current.conflicted = false;
    state.queue.upsert_baseline(&current)?;
    state
        .queue
        .remove_local_fingerprint(&root.config.root_id, old_logical)?;
    if !hash.eq_ignore_ascii_case(&baseline.sha256) {
        if state
            .queue
            .has_active_path(&root.config.root_id, &new_logical)?
        {
            return Ok(true);
        }
        let upload = NewTask {
            root_id: Some(root.config.root_id.clone()),
            operation_id: Uuid::new_v4().to_string(),
            kind: TaskKind::Upload,
            source_path: Some(new_path.to_string_lossy().into_owned()),
            destination_path: None,
            logical_path: Some(new_logical.clone()),
            scope_id: Some(baseline.scope_id.clone()),
            project_id: baseline.project_id.clone(),
            display_name: new_path
                .file_name()
                .and_then(|n| n.to_str())
                .unwrap_or("File")
                .into(),
            folder_group: root.config.root_id.clone(),
            file_id: Some(baseline.file_id.clone()),
            revision_id: Some(baseline.revision_id.clone()),
            object_id: None,
            expected_version: Some(new_version),
            media_type: media_type(new_path),
            source: "sync".into(),
            transfer_url: String::new(),
            ticket: String::new(),
            bytes_total: Some(size),
            replace_if_hash: None,
            batch_parent_id: None,
        };
        state.queue.enqueue(&upload)?;
        emit_task_queued(state, &upload);
    } else if let Ok(metadata) = std::fs::metadata(new_path) {
        state.queue.set_local_fingerprint(
            &root.config.root_id,
            &new_logical,
            metadata.len(),
            modified_ns(&metadata),
        )?;
    }
    Ok(true)
}

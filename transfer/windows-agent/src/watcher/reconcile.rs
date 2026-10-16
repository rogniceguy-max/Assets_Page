use super::common::*;
use super::{events::queue_delete_task, *};

pub(super) async fn full_reconcile(state: &Arc<AgentState>, root: &RootContext) -> Result<()> {
    let config = current_config_for_root(state, root).await?;
    let mut catalog = list_catalog_tree(state, &config, root).await?;
    current_config_for_root(state, root).await?;
    let mut local_files = Vec::new();
    let mut local_directories = Vec::new();
    walk_local(
        root,
        &root.canonical,
        &mut local_files,
        &mut local_directories,
    )?;
    current_config_for_root(state, root).await?;
    let mut ambiguous_dirs = HashSet::new();
    for _ in 0..32 {
        current_config_for_root(state, root).await?;
        if !pair_local_directory_renames(
            state,
            root,
            &catalog,
            &local_files,
            &local_directories,
            &mut ambiguous_dirs,
        )
        .await?
        {
            break;
        }
        ambiguous_dirs.clear();
        catalog = list_catalog_tree(state, &config, root).await?;
        current_config_for_root(state, root).await?;
        local_files.clear();
        local_directories.clear();
        walk_local(
            root,
            &root.canonical,
            &mut local_files,
            &mut local_directories,
        )?;
        current_config_for_root(state, root).await?;
    }
    current_config_for_root(state, root).await?;
    if pair_local_file_renames(state, root, &catalog, &local_files, &ambiguous_dirs).await? {
        catalog = list_catalog_tree(state, &config, root).await?;
        current_config_for_root(state, root).await?;
        local_files.clear();
        local_directories.clear();
        walk_local(
            root,
            &root.canonical,
            &mut local_files,
            &mut local_directories,
        )?;
        current_config_for_root(state, root).await?;
    }
    let remote_paths: HashSet<_> = catalog
        .iter()
        .filter(|item| !item.is_directory)
        .map(|item| item.logical_path.clone())
        .collect();
    let remote_directories: HashSet<_> = catalog
        .iter()
        .filter(|item| item.is_directory)
        .map(|item| item.logical_path.clone())
        .collect();
    for item in catalog.iter().filter(|item| item.is_directory) {
        current_config_for_root(state, root).await?;
        if is_under_ambiguous_directory(&item.logical_path, &ambiguous_dirs) {
            continue;
        }
        let local = local_for_logical(root, &item.logical_path)?;
        if !local.exists() {
            suppress_path(state, root, &local)?;
            tokio::fs::create_dir_all(&local).await?;
        }
    }
    for item in catalog.iter().filter(|item| !item.is_directory) {
        current_config_for_root(state, root).await?;
        if is_under_ambiguous_directory(&item.logical_path, &ambiguous_dirs) {
            continue;
        }
        reconcile_catalog_file(state, root, item).await?;
    }
    for directory in local_directories {
        let config = current_config_for_root(state, root).await?;
        if is_under_ambiguous_directory(&directory.logical_path, &ambiguous_dirs) {
            continue;
        }
        if remote_directories.contains(&directory.logical_path) {
            continue;
        }
        let Some(scope_id) = root.config.scope_id.as_deref() else {
            continue;
        };
        api::create_directory(
            &state.api,
            &config,
            &Uuid::new_v4().to_string(),
            scope_id,
            root.config.project_id.as_deref(),
            &directory.logical_path,
        )
        .await?;
    }
    for local in local_files {
        current_config_for_root(state, root).await?;
        if is_under_ambiguous_directory(&local.logical_path, &ambiguous_dirs) {
            continue;
        }
        if remote_paths.contains(&local.logical_path) {
            continue;
        }
        match state
            .queue
            .baseline(&root.config.root_id, &local.logical_path)?
        {
            Some(baseline) if baseline.deleted || baseline.conflicted => emit_conflict(
                state,
                root,
                &local.logical_path,
                "local file conflicts with a deleted or unresolved server file",
            ),
            Some(_baseline) => {
                // A server file absent from the listing may have a tombstone in the feed. Keep
                // local content until that event is applied; do not silently resurrect it.
            }
            None => {
                queue_upload(state, root, &local, None)?;
            }
        }
    }
    Ok(())
}

pub(super) async fn pair_local_file_renames(
    state: &Arc<AgentState>,
    root: &RootContext,
    catalog: &[FileCatalogItem],
    local_files: &[LocalFile],
    blocked_directories: &HashSet<String>,
) -> Result<bool> {
    let remote_by_path: HashMap<_, _> = catalog
        .iter()
        .filter(|item| !item.is_directory)
        .map(|item| (item.logical_path.as_str(), item))
        .collect();
    let local_by_path: HashMap<_, _> = local_files
        .iter()
        .map(|item| (item.logical_path.as_str(), item))
        .collect();
    let mut origins: HashMap<(u64, String), Vec<SyncBaseline>> = HashMap::new();
    for baseline in state.queue.baselines_for_root(&root.config.root_id)? {
        if is_under_ambiguous_directory(&baseline.logical_path, blocked_directories)
            || baseline.deleted
            || baseline.conflicted
            || local_by_path.contains_key(baseline.logical_path.as_str())
        {
            continue;
        }
        let Some(item) = remote_by_path.get(baseline.logical_path.as_str()) else {
            continue;
        };
        if item.file_id.as_deref() != Some(baseline.file_id.as_str()) {
            continue;
        }
        origins
            .entry((baseline.size, baseline.sha256.to_ascii_lowercase()))
            .or_default()
            .push(baseline);
    }
    if origins.is_empty() {
        return Ok(false);
    }
    let remote_paths: HashSet<_> = remote_by_path.keys().copied().collect();
    let mut targets: HashMap<(u64, String), Vec<&LocalFile>> = HashMap::new();
    for local in local_files {
        if is_under_ambiguous_directory(&local.logical_path, blocked_directories)
            || remote_paths.contains(local.logical_path.as_str())
            || state
                .queue
                .baseline(&root.config.root_id, &local.logical_path)?
                .is_some()
        {
            continue;
        }
        let (size, sha256) = hash_local_stable(&local.path).await?;
        targets
            .entry((size, sha256.to_ascii_lowercase()))
            .or_default()
            .push(local);
    }
    for (key, old_matches) in &origins {
        let Some(new_matches) = targets.get(key) else {
            continue;
        };
        if old_matches.len() != 1 || new_matches.len() != 1 {
            for old in old_matches {
                let mut conflict = old.clone();
                conflict.conflicted = true;
                state.queue.upsert_baseline(&conflict)?;
            }
            let old = old_matches.first().expect("nonempty origin group");
            for new in new_matches {
                let mut conflict = old.clone();
                conflict.logical_path = new.logical_path.clone();
                conflict.conflicted = true;
                state.queue.upsert_baseline(&conflict)?;
                emit_conflict(
                    state,
                    root,
                    &new.logical_path,
                    "rescan found multiple same-content move candidates; local data was kept without creating another server file",
                );
            }
            continue;
        }
        let baseline = &old_matches[0];
        let new = new_matches[0];
        let operation_id = Uuid::new_v4().to_string();
        let task = pseudo_task(&operation_id, baseline, root, "move");
        let config = current_config_for_root(state, root).await?;
        match api::move_file(
            &state.api,
            &config,
            &task,
            &new.logical_path,
            root.config.scope_id.as_deref().unwrap_or_default(),
            root.config.project_id.as_deref(),
        )
        .await
        {
            Ok(moved) => {
                let mut old = baseline.clone();
                old.deleted = true;
                state.queue.upsert_baseline(&old)?;
                let mut current = baseline.clone();
                current.logical_path = new.logical_path.clone();
                current.deleted = false;
                current.version = moved.version.unwrap_or(baseline.version + 1);
                state.queue.upsert_baseline(&current)?;
                state
                    .queue
                    .remove_local_fingerprint(&root.config.root_id, &baseline.logical_path)?;
                if key.1.eq_ignore_ascii_case(&baseline.sha256) {
                    state.queue.set_local_fingerprint(
                        &root.config.root_id,
                        &new.logical_path,
                        new.size,
                        new.modified_ns,
                    )?;
                } else {
                    queue_upload(state, root, new, Some(&current))?;
                }
                return Ok(true);
            }
            Err(error) => {
                let mut conflict = baseline.clone();
                conflict.conflicted = true;
                state.queue.upsert_baseline(&conflict)?;
                let mut new_conflict = conflict.clone();
                new_conflict.logical_path = new.logical_path.clone();
                state.queue.upsert_baseline(&new_conflict)?;
                emit_conflict(
                    state,
                    root,
                    &new.logical_path,
                    &format!("rescan matched a rename but the server move failed: {error}"),
                );
            }
        }
    }
    Ok(false)
}

pub(super) async fn pair_local_directory_renames(
    state: &Arc<AgentState>,
    root: &RootContext,
    catalog: &[FileCatalogItem],
    local_files: &[LocalFile],
    local_directories: &[LocalDirectory],
    ambiguous_directories: &mut HashSet<String>,
) -> Result<bool> {
    let remote_directories: HashSet<_> = catalog
        .iter()
        .filter(|item| item.is_directory)
        .map(|item| item.logical_path.as_str())
        .collect();
    let local_directories_by_path: HashMap<_, _> = local_directories
        .iter()
        .map(|item| (item.logical_path.as_str(), item))
        .collect();
    let old_all: Vec<&FileCatalogItem> = catalog
        .iter()
        .filter(|item| {
            item.is_directory
                && !local_directories_by_path.contains_key(item.logical_path.as_str())
                && !local_for_logical(root, &item.logical_path).is_ok_and(|path| path.exists())
        })
        .collect();
    let old_paths: Vec<_> = old_all
        .iter()
        .map(|item| item.logical_path.as_str())
        .collect();
    let old_candidates: Vec<_> = old_all
        .into_iter()
        .filter(|candidate| {
            !old_paths.iter().any(|other| {
                *other != candidate.logical_path.as_str()
                    && candidate
                        .logical_path
                        .strip_prefix(&format!("{}/", other))
                        .is_some()
            })
        })
        .collect();
    let new_all: Vec<&LocalDirectory> = local_directories
        .iter()
        .filter(|item| !remote_directories.contains(item.logical_path.as_str()))
        .collect();
    let new_paths: Vec<_> = new_all
        .iter()
        .map(|item| item.logical_path.as_str())
        .collect();
    let new_candidates: Vec<_> = new_all
        .into_iter()
        .filter(|candidate| {
            !new_paths.iter().any(|other| {
                *other != candidate.logical_path.as_str()
                    && candidate
                        .logical_path
                        .strip_prefix(&format!("{}/", other))
                        .is_some()
            })
        })
        .collect();
    if old_candidates.is_empty() || new_candidates.is_empty() {
        return Ok(false);
    }
    let old_candidate_paths: Vec<_> = old_candidates
        .iter()
        .map(|item| item.logical_path.clone())
        .collect();
    let new_candidate_paths: Vec<_> = new_candidates
        .iter()
        .map(|item| item.logical_path.clone())
        .collect();

    let mut origins: HashMap<Vec<(u64, String)>, Vec<&FileCatalogItem>> = HashMap::new();
    for directory in old_candidates {
        let baselines = state
            .queue
            .baselines_under_prefix(&root.config.root_id, &directory.logical_path)?;
        let mut signature: Vec<_> = baselines
            .into_iter()
            .filter(|item| !item.deleted && !item.conflicted)
            .map(|item| (item.size, item.sha256.to_ascii_lowercase()))
            .collect();
        if signature.is_empty() {
            continue;
        }
        signature.sort();
        origins.entry(signature).or_default().push(directory);
    }
    let remote_file_paths: HashSet<_> = catalog
        .iter()
        .filter(|item| !item.is_directory)
        .map(|item| item.logical_path.as_str())
        .collect();
    let mut target_groups: HashMap<Vec<(u64, String)>, Vec<&LocalDirectory>> = HashMap::new();
    for directory in new_candidates {
        let mut signature = Vec::new();
        for file in local_files.iter().filter(|file| {
            file.logical_path == directory.logical_path
                || file
                    .logical_path
                    .strip_prefix(&format!("{}/", directory.logical_path))
                    .is_some()
        }) {
            if remote_file_paths.contains(file.logical_path.as_str()) {
                continue;
            }
            let (size, sha) = hash_local_stable(&file.path).await?;
            signature.push((size, sha.to_ascii_lowercase()));
        }
        if signature.is_empty() {
            continue;
        }
        signature.sort();
        target_groups.entry(signature).or_default().push(directory);
    }
    let mut matching_signatures_found = false;
    for (signature, old_matches) in origins {
        let Some(new_matches) = target_groups.get(&signature) else {
            continue;
        };
        matching_signatures_found = true;
        if old_matches.len() != 1 || new_matches.len() != 1 {
            for directory in old_matches {
                ambiguous_directories.insert(directory.logical_path.clone());
                emit_warning(
                    state,
                    &root.config.root_id,
                    "directory_rename_ambiguous",
                    &format!(
                        "directory rename near '{}' has multiple matching candidates; no directory was created or moved",
                        directory.logical_path
                    ),
                );
            }
            for directory in new_matches {
                ambiguous_directories.insert(directory.logical_path.clone());
            }
            continue;
        }
        let old = old_matches[0];
        let new = new_matches[0];
        let (Some(directory_id), Some(version)) = (old.directory_id.as_deref(), old.version) else {
            continue;
        };
        let config = current_config_for_root(state, root).await?;
        match api::move_directory(
            &state.api,
            &config,
            directory_id,
            &Uuid::new_v4().to_string(),
            version,
            &old.scope_id,
            old.project_id.as_deref(),
            &new.logical_path,
        )
        .await
        {
            Ok(moved) => {
                state.queue.move_baseline_prefix(
                    &root.config.root_id,
                    &old.logical_path,
                    &new.logical_path,
                )?;
                return Ok(true);
            }
            Err(error) => {
                ambiguous_directories.insert(old.logical_path.clone());
                ambiguous_directories.insert(new.logical_path.clone());
                emit_warning(
                    state,
                    &root.config.root_id,
                    "directory_rename_failed",
                    &format!("{}: {error}", new.logical_path),
                );
            }
        }
    }
    if !matching_signatures_found {
        ambiguous_directories.extend(old_candidate_paths.iter().cloned());
        ambiguous_directories.extend(new_candidate_paths.iter().cloned());
        emit_warning(
            state,
            &root.config.root_id,
            "directory_rename_ambiguous",
            "directory move candidates could not be uniquely matched by their known file baselines; affected folders were left unchanged for review",
        );
    }
    Ok(false)
}

fn is_under_ambiguous_directory(path: &str, directories: &HashSet<String>) -> bool {
    directories.iter().any(|directory| {
        path == directory
            || path
                .strip_prefix(directory)
                .is_some_and(|suffix| suffix.starts_with('/'))
    })
}

pub(super) async fn list_catalog_tree(
    state: &AgentState,
    config: &AgentConfig,
    root: &RootContext,
) -> Result<Vec<FileCatalogItem>> {
    let scope = root
        .config
        .scope_id
        .as_deref()
        .ok_or_else(|| anyhow!("sync root has no scope_id"))?;
    let mut directories =
        VecDeque::from([root.config.logical_path_prefix.trim_matches('/').to_owned()]);
    let mut seen = HashSet::new();
    let mut files = Vec::new();
    while let Some(parent) = directories.pop_front() {
        if !seen.insert(parent.clone()) {
            continue;
        }
        let mut cursor: Option<String> = None;
        loop {
            let response = api::list_files(
                &state.api,
                config,
                scope,
                root.config.project_id.clone(),
                &parent,
                cursor.as_deref(),
            )
            .await?;
            for item in response.items {
                if item.is_directory {
                    if !item.logical_path.is_empty() {
                        directories.push_back(item.logical_path.clone());
                    }
                }
                files.push(item);
            }
            match response.next_cursor.filter(|value| !value.is_empty()) {
                Some(next) => cursor = Some(next),
                None => break,
            }
        }
    }
    Ok(files)
}

pub(super) async fn reconcile_catalog_file(
    state: &Arc<AgentState>,
    root: &RootContext,
    item: &FileCatalogItem,
) -> Result<()> {
    current_config_for_root(state, root).await?;
    let Some(file_id) = item.file_id.as_deref() else {
        return Ok(());
    };
    let Some(revision_id) = item.current_revision_id.as_deref() else {
        return Ok(());
    };
    let Some(sha256) = item.sha256.as_deref() else {
        emit_warning(
            state,
            &root.config.root_id,
            "catalog_hash_missing",
            "file catalog item has no SHA-256",
        );
        return Ok(());
    };
    let Some(version) = item.version else {
        return Ok(());
    };
    let path = local_for_logical(root, &item.logical_path)?;
    if state
        .queue
        .has_active_path(&root.config.root_id, &item.logical_path)?
    {
        return Ok(());
    }
    let previous = state
        .queue
        .baseline(&root.config.root_id, &item.logical_path)?;
    let metadata = std::fs::metadata(&path).ok().filter(|meta| meta.is_file());
    let current = SyncBaseline {
        root_id: root.config.root_id.clone(),
        logical_path: item.logical_path.clone(),
        scope_id: item.scope_id.clone(),
        project_id: item.project_id.clone(),
        file_id: file_id.to_owned(),
        revision_id: revision_id.to_owned(),
        version,
        size: item.size,
        sha256: sha256.to_owned(),
        deleted: false,
        conflicted: false,
    };
    if let Some(metadata) = metadata {
        let modified_ns = modified_ns(&metadata);
        let fingerprint = state
            .queue
            .local_fingerprint(&root.config.root_id, &item.logical_path)?;
        if !needs_local_hash(
            previous.as_ref(),
            file_id,
            sha256,
            metadata.len(),
            modified_ns,
            fingerprint.as_ref(),
        ) {
            // A known-unchanged local file needs no content read during startup or overflow scans.
            current_config_for_root(state, root).await?;
            state.queue.upsert_baseline(&current)?;
            return Ok(());
        }
        let local_hash = hash_local_stable(&path).await?.1;
        current_config_for_root(state, root).await?;
        if local_hash.eq_ignore_ascii_case(sha256) {
            state.queue.upsert_baseline(&current)?;
            state.queue.set_local_fingerprint(
                &root.config.root_id,
                &item.logical_path,
                metadata.len(),
                modified_ns,
            )?;
            return Ok(());
        }
        if let Some(old) = &previous {
            if !old.deleted
                && !old.conflicted
                && old.file_id == file_id
                && local_hash.eq_ignore_ascii_case(&old.sha256)
            {
                current_config_for_root(state, root).await?;
                // The server revision changed while local bytes still match the previous baseline.
                queue_download(state, root, item, &path, Some(&old.sha256), false)?;
                return Ok(());
            }
        }
        current_config_for_root(state, root).await?;
        state.queue.upsert_baseline(&SyncBaseline {
            conflicted: true,
            ..current.clone()
        })?;
        let remote_copy = conflict_path(&path, "remote", version);
        queue_download(state, root, item, &remote_copy, None, true)?;
        emit_conflict(
            state,
            root,
            &item.logical_path,
            "local bytes differ from the server and were kept; the server version is downloading to a conflict copy",
        );
        return Ok(());
    }
    if let Some(old) = previous {
        if !old.deleted && !old.conflicted {
            current_config_for_root(state, root).await?;
            // The local deletion is authoritative for an already-associated sync file.
            queue_delete_task(state, root, &old)?;
            return Ok(());
        }
    }
    current_config_for_root(state, root).await?;
    state.queue.upsert_baseline(&current)?;
    queue_download(state, root, item, &path, None, false)?;
    Ok(())
}

pub(super) fn walk_local(
    root: &RootContext,
    directory: &Path,
    files: &mut Vec<LocalFile>,
    directories: &mut Vec<LocalDirectory>,
) -> Result<()> {
    let entries = match std::fs::read_dir(directory) {
        Ok(entries) => entries,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.into()),
    };
    for entry in entries {
        let entry = entry?;
        let path = entry.path();
        let metadata = std::fs::symlink_metadata(&path)?;
        if is_reparse_point(&metadata) {
            continue;
        }
        if metadata.is_dir() {
            if let Some(logical_path) = logical_for_local(root, &path) {
                directories.push(LocalDirectory {
                    path: path.clone(),
                    logical_path,
                });
            }
            walk_local(root, &path, files, directories)?;
            continue;
        }
        if !metadata.is_file() {
            continue;
        }
        let Some(logical_path) = logical_for_local(root, &path) else {
            continue;
        };
        if path
            .file_name()
            .and_then(|v| v.to_str())
            .is_some_and(is_agent_artifact_name)
        {
            continue;
        }
        files.push(LocalFile {
            path,
            logical_path,
            size: metadata.len(),
            modified_ns: modified_ns(&metadata),
        });
    }
    Ok(())
}

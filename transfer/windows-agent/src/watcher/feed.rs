use super::*;
use super::{common::*, reconcile::{full_reconcile, reconcile_catalog_file}};

pub(super) async fn poll_changes(
    state: &Arc<AgentState>,
    roots: &HashMap<String, RootContext>,
) -> Result<Option<(AgentConfig, i64)>> {
    let Some(config) = state.config.read().await.clone() else {
        return Ok(None);
    };
    let mut cursor = state.queue.sync_cursor(&config.client_id)?;
    for _ in 0..8 {
        let feed = api::sync_changes(&state.api, &config, cursor).await?;
        if feed.reset_required {
            return Ok(Some((config, feed.next_sequence)));
        }
        for change in &feed.items {
            for root in roots_for_change(roots, change) {
                if current_config_for_root(state, root).await.is_err() {
                    continue;
                }
                if change.entity_kind == "directory" {
                    apply_directory_change(state, root, change).await?;
                } else if change.entity_kind == "file" {
                    apply_file_change(state, root, change, &config).await?;
                }
            }
            // The cursor is global per client: persist only after the event was applied to
            // every configured root it affects, including both sides of cross-root moves.
            cursor = change.sequence;
            state.queue.set_sync_cursor(&config.client_id, cursor)?;
        }
        // next_sequence is the last raw event scanned, including entries hidden by
        // the control API's scope filter. Advance to that boundary only after every
        // visible item in this page has been applied to its affected roots.
        cursor = cursor.max(feed.next_sequence);
        state.queue.set_sync_cursor(&config.client_id, cursor)?;
        api::acknowledge_changes(&state.api, &config, cursor).await?;
        if !feed.has_more {
            break;
        }
    }
    Ok(None)
}

pub(super) fn roots_for_change<'a>(
    roots: &'a HashMap<String, RootContext>,
    change: &ChangeItem,
) -> Vec<&'a RootContext> {
    roots
        .values()
        .filter(|root| change_touches_root(root, change))
        .collect()
}

pub(super) fn change_touches_root(root: &RootContext, change: &ChangeItem) -> bool {
    let action = change.action.to_ascii_lowercase();
    if action.contains("move") || action.contains("rename") || action == "retarget_deleted" {
        let previous_scope = change
            .previous_scope_id
            .as_deref()
            .or(change.scope_id.as_deref());
        let previous_project = change
            .previous_project_id
            .as_deref()
            .or(change.project_id.as_deref());
        let old = change.previous_logical_path.as_deref().unwrap_or("");
        let new = change.logical_path.as_deref().unwrap_or("");
        belongs_to_root(root, previous_scope, previous_project, old)
            || belongs_to_root(
                root,
                change.scope_id.as_deref(),
                change.project_id.as_deref(),
                new,
            )
    } else {
        let path = change
            .logical_path
            .as_deref()
            .or(change.previous_logical_path.as_deref())
            .unwrap_or("");
        belongs_to_root(
            root,
            change.scope_id.as_deref(),
            change.project_id.as_deref(),
            path,
        )
    }
}

pub(super) async fn apply_file_change(
    state: &Arc<AgentState>,
    root: &RootContext,
    change: &ChangeItem,
    config: &AgentConfig,
) -> Result<()> {
    let action = change.action.to_ascii_lowercase();
    if action == "retarget_deleted" {
        apply_retarget_deleted(state, root, change).await?;
        return Ok(());
    }
    if action.contains("delete") || action == "tombstone" {
        let logical = change
            .logical_path
            .as_deref()
            .or(change.previous_logical_path.as_deref())
            .unwrap_or("");
        apply_remote_delete_at(state, root, change, logical).await?;
        return Ok(());
    }
    if action.contains("move") || action.contains("rename") {
        let old = change.previous_logical_path.as_deref().unwrap_or("");
        let new = change.logical_path.as_deref().unwrap_or("");
        let old_scope = change
            .previous_scope_id
            .as_deref()
            .or(change.scope_id.as_deref());
        let old_project = change
            .previous_project_id
            .as_deref()
            .or(change.project_id.as_deref());
        let from = belongs_to_root(root, old_scope, old_project, old);
        let to = belongs_to_root(
            root,
            change.scope_id.as_deref(),
            change.project_id.as_deref(),
            new,
        );
        match (from, to) {
            (true, true) => apply_remote_move(state, root, change, old, new).await?,
            (true, false) => apply_remote_move_out(state, root, change, old).await?,
            (false, true) => apply_remote_move_in(state, root, change, new).await?,
            (false, false) => {}
        }
        return Ok(());
    }
    let logical = change.logical_path.as_deref().unwrap_or("");
    if !belongs_to_root(
        root,
        change.scope_id.as_deref(),
        change.project_id.as_deref(),
        logical,
    ) {
        return Ok(());
    }
    if let Some(item) = find_catalog_entry(state, root, logical).await? {
        reconcile_catalog_file(state, root, &item).await?;
    }
    Ok(())
}

async fn apply_retarget_deleted(
    state: &AgentState,
    root: &RootContext,
    change: &ChangeItem,
) -> Result<()> {
    let old_path = change.previous_logical_path.as_deref().unwrap_or("");
    let new_path = change.logical_path.as_deref().unwrap_or("");
    let old_scope = change
        .previous_scope_id
        .as_deref()
        .or(change.scope_id.as_deref());
    let old_project = change
        .previous_project_id
        .as_deref()
        .or(change.project_id.as_deref());
    let from = belongs_to_root(root, old_scope, old_project, old_path);
    if !from {
        return Ok(());
    }
    apply_remote_delete_at(state, root, change, old_path).await?;
    let Some(file_id) = change.file_id.as_deref() else {
        return Ok(());
    };
    let to = belongs_to_root(
        root,
        change.scope_id.as_deref(),
        change.project_id.as_deref(),
        new_path,
    );
    state.queue.retarget_deleted_baseline(
        &root.config.root_id,
        old_path,
        file_id,
        to.then_some(new_path),
        if to {
            change.scope_id.as_deref()
        } else {
            None
        },
        if to {
            change.project_id.as_deref()
        } else {
            None
        },
        change.revision_id.as_deref(),
        change.version,
    )?;
    Ok(())
}

async fn apply_remote_delete_at(
    state: &AgentState,
    root: &RootContext,
    change: &ChangeItem,
    logical: &str,
) -> Result<()> {
    if !belongs_to_root(
        root,
        change.scope_id.as_deref(),
        change.project_id.as_deref(),
        logical,
    ) && !belongs_to_root(
        root,
        change.previous_scope_id
            .as_deref()
            .or(change.scope_id.as_deref()),
        change.previous_project_id
            .as_deref()
            .or(change.project_id.as_deref()),
        logical,
    ) {
        return Ok(());
    }
    let Some(mut tombstone) = state.queue.baseline(&root.config.root_id, logical)? else {
        return Ok(());
    };
    if change
        .file_id
        .as_deref()
        .is_some_and(|file_id| file_id != tombstone.file_id)
    {
        return Ok(());
    }
    let local = local_for_logical(root, logical)?;
    let mut conflicted = tombstone.conflicted;
    if local.is_file() {
        let hash = hash_local_stable(&local).await?.1;
        if hash.eq_ignore_ascii_case(&tombstone.sha256) {
            suppress_path(state, root, &local)?;
            tokio::fs::remove_file(&local).await?;
            remove_empty_local_parents(root, &local).await;
            state
                .queue
                .remove_local_fingerprint(&root.config.root_id, logical)?;
        } else {
            conflicted = true;
            let conflict =
                conflict_path(&local, "local", change.version.unwrap_or(tombstone.version));
            if let Some(parent) = conflict.parent() {
                tokio::fs::create_dir_all(parent).await?;
            }
            suppress_path(state, root, &local)?;
            suppress_path(state, root, &conflict)?;
            tokio::fs::rename(&local, &conflict).await?;
            emit_conflict(
                state,
                root,
                logical,
                "remote deletion arrived while local bytes were modified; local bytes were kept as a conflict copy",
            );
        }
    }
    tombstone.deleted = true;
    tombstone.conflicted = conflicted;
    if let Some(version) = change.version {
        tombstone.version = version;
    }
    if let Some(revision_id) = change.revision_id.as_deref() {
        tombstone.revision_id = revision_id.to_owned();
    }
    state.queue.upsert_baseline(&tombstone)?;
    Ok(())
}

pub(super) async fn apply_remote_move(
    state: &Arc<AgentState>,
    root: &RootContext,
    change: &ChangeItem,
    old_path: &str,
    new_path: &str,
) -> Result<()> {
    if let Some(mut existing) = state.queue.baseline(&root.config.root_id, new_path)? {
        if Some(existing.file_id.as_str()) == change.file_id.as_deref() {
            if let Some(version) = change.version {
                existing.version = version;
            }
            if let Some(revision_id) = change.revision_id.as_deref() {
                existing.revision_id = revision_id.to_owned();
            }
            existing.deleted = false;
            state.queue.upsert_baseline(&existing)?;
            return Ok(());
        }
    }
    let Some(mut old) = state.queue.baseline(&root.config.root_id, old_path)? else {
        emit_conflict(
            state,
            root,
            new_path,
            "remote move arrived without a local baseline; full reconciliation was requested",
        );
        full_reconcile(state, root).await?;
        return Ok(());
    };
    let old_local = local_for_logical(root, old_path)?;
    let new_local = local_for_logical(root, new_path)?;
    if old_local.is_file() {
        let hash = hash_local_stable(&old_local).await?.1;
        if hash.eq_ignore_ascii_case(&old.sha256) && !new_local.exists() {
            if let Some(parent) = new_local.parent() {
                tokio::fs::create_dir_all(parent).await?;
            }
            suppress_path(state, root, &old_local)?;
            suppress_path(state, root, &new_local)?;
            tokio::fs::rename(&old_local, &new_local).await?;
            state
                .queue
                .move_local_fingerprint(&root.config.root_id, old_path, new_path)?;
        } else {
            old.conflicted = true;
            emit_conflict(
                state,
                root,
                old_path,
                "remote move conflicts with modified local bytes or an occupied target; local bytes were kept",
            );
        }
    }
    old.deleted = true;
    state.queue.upsert_baseline(&old)?;
    if let Some(item) = find_catalog_entry(state, root, new_path).await? {
        let mut current = baseline_from_item(root, &item)?;
        current.conflicted = old.conflicted;
        state.queue.upsert_baseline(&current)?;
        if !new_local.exists() {
            queue_download(state, root, &item, &new_local, None, false)?;
        }
    }
    Ok(())
}

pub(super) async fn apply_remote_move_out(
    state: &AgentState,
    root: &RootContext,
    change: &ChangeItem,
    old_path: &str,
) -> Result<()> {
    let Some(mut baseline) = state.queue.baseline(&root.config.root_id, old_path)? else {
        return Ok(());
    };
    let local = local_for_logical(root, old_path)?;
    let mut conflicted = false;
    if local.is_file() {
        let actual = hash_local_stable(&local).await?.1;
        if actual.eq_ignore_ascii_case(&baseline.sha256) {
            suppress_path(state, root, &local)?;
            tokio::fs::remove_file(&local).await?;
            state
                .queue
                .remove_local_fingerprint(&root.config.root_id, old_path)?;
            remove_empty_local_parents(root, &local).await;
        } else {
            conflicted = true;
            let conflict =
                conflict_path(&local, "moved", change.version.unwrap_or(baseline.version));
            if let Some(parent) = conflict.parent() {
                tokio::fs::create_dir_all(parent).await?;
            }
            suppress_path(state, root, &local)?;
            suppress_path(state, root, &conflict)?;
            tokio::fs::rename(&local, &conflict).await?;
            emit_conflict(
                state,
                root,
                old_path,
                "file moved outside this sync root while local bytes were modified; local bytes were retained as a conflict copy",
            );
        }
    }
    baseline.deleted = true;
    baseline.conflicted = conflicted;
    if let Some(version) = change.version {
        baseline.version = version;
    }
    state.queue.upsert_baseline(&baseline)?;
    Ok(())
}

pub(super) async fn apply_remote_move_in(
    state: &AgentState,
    root: &RootContext,
    change: &ChangeItem,
    new_path: &str,
) -> Result<()> {
    let Some(item) = find_catalog_entry(state, root, new_path).await? else {
        return Ok(());
    };
    let current = baseline_from_item(root, &item)?;
    let local = local_for_logical(root, new_path)?;
    if local.exists() {
        let conflict = conflict_path(&local, "local", current.version);
        state.queue.upsert_baseline(&SyncBaseline {
            conflicted: true,
            ..current
        })?;
        queue_download(state, root, &item, &conflict, None, true)?;
        emit_conflict(
            state,
            root,
            new_path,
            "file moved into this sync root where local content already exists; local content was retained",
        );
    } else {
        state.queue.upsert_baseline(&current)?;
        queue_download(state, root, &item, &local, None, false)?;
    }
    Ok(())
}

pub(super) async fn apply_directory_change(
    state: &AgentState,
    root: &RootContext,
    change: &ChangeItem,
) -> Result<()> {
    let logical = change.logical_path.as_deref().unwrap_or("");
    let action = change.action.to_ascii_lowercase();
    if action.contains("move") || action.contains("rename") {
        let old = change.previous_logical_path.as_deref().unwrap_or("");
        let old_scope = change
            .previous_scope_id
            .as_deref()
            .or(change.scope_id.as_deref());
        let old_project = change
            .previous_project_id
            .as_deref()
            .or(change.project_id.as_deref());
        let from = belongs_to_root(root, old_scope, old_project, old);
        let to = belongs_to_root(
            root,
            change.scope_id.as_deref(),
            change.project_id.as_deref(),
            logical,
        );
        match (from, to) {
            (true, true) => {
                let source = local_for_logical(root, old)?;
                let target = local_for_logical(root, logical)?;
                if source.is_dir() && !target.exists() {
                    if let Some(parent) = target.parent() {
                        tokio::fs::create_dir_all(parent).await?;
                    }
                    suppress_path(state, root, &source)?;
                    suppress_path(state, root, &target)?;
                    tokio::fs::rename(&source, &target).await?;
                    state
                        .queue
                        .move_baseline_prefix(&root.config.root_id, old, logical)?;
                } else if source.exists() {
                    emit_conflict(
                        state,
                        root,
                        old,
                        "remote directory move target is occupied; local directory was left in place",
                    );
                }
            }
            (true, false) => {
                let source = local_for_logical(root, old)?;
                if source.is_dir()
                    && std::fs::read_dir(&source).is_ok_and(|mut entries| entries.next().is_none())
                {
                    suppress_path(state, root, &source)?;
                    let _ = tokio::fs::remove_dir(&source).await;
                }
            }
            (false, true) => {
                let target = local_for_logical(root, logical)?;
                if !target.exists() {
                    suppress_path(state, root, &target)?;
                    tokio::fs::create_dir_all(&target).await?;
                }
            }
            (false, false) => {}
        }
        return Ok(());
    }
    if !belongs_to_root(
        root,
        change.scope_id.as_deref(),
        change.project_id.as_deref(),
        logical,
    ) {
        return Ok(());
    }
    let local = local_for_logical(root, logical)?;
    if action.contains("delete") {
        if local.is_dir() {
            match tokio::fs::remove_dir(&local).await {
                Ok(()) => {}
                Err(error) if error.kind() == std::io::ErrorKind::DirectoryNotEmpty => {
                    emit_conflict(
                        state,
                        root,
                        logical,
                        "remote directory deletion left local files in place",
                    )
                }
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
                Err(error) => return Err(error.into()),
            }
        }
    } else if !local.exists() {
        suppress_path(state, root, &local)?;
        tokio::fs::create_dir_all(&local).await?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn root(root_id: &str, scope_id: &str, project_id: &str, prefix: &str) -> RootContext {
        RootContext {
            config: SyncRoot {
                root_id: root_id.into(),
                path: format!(r"C:\sync\{root_id}"),
                logical_path_prefix: prefix.into(),
                scope_id: Some(scope_id.into()),
                project_id: Some(project_id.into()),
            },
            canonical: PathBuf::from(format!(r"C:\sync\{root_id}")),
        }
    }

    fn change(scope: &str, project: &str, path: &str) -> ChangeItem {
        ChangeItem {
            sequence: 1,
            entity_kind: "file".into(),
            entity_id: "file-id".into(),
            action: "update".into(),
            file_id: Some("file-id".into()),
            scope_id: Some(scope.into()),
            project_id: Some(project.into()),
            logical_path: Some(path.into()),
            previous_scope_id: None,
            previous_project_id: None,
            previous_logical_path: None,
            revision_id: None,
            version: Some(2),
            source: None,
            actor_id: None,
            created_at: None,
        }
    }

    #[test]
    fn one_global_feed_page_routes_events_to_each_matching_root() {
        let roots = HashMap::from([
            (
                "episode-a".into(),
                root("episode-a", "scope-a", "project-a", "Episode/01"),
            ),
            (
                "episode-b".into(),
                root("episode-b", "scope-b", "project-b", "Episode/02"),
            ),
        ]);
        let page = vec![
            change("scope-a", "project-a", "Episode/01/beat01.mp4"),
            change("scope-b", "project-b", "Episode/02/beat02.mp4"),
        ];
        let routed: Vec<Vec<_>> = page
            .iter()
            .map(|item| {
                roots_for_change(&roots, item)
                    .iter()
                    .map(|root| root.config.root_id.as_str())
                    .collect()
            })
            .collect();
        assert_eq!(routed, vec![vec!["episode-a"], vec!["episode-b"]]);
    }

    #[test]
    fn cross_root_move_is_applied_to_both_source_and_target_roots() {
        let roots = HashMap::from([
            (
                "episode-a".into(),
                root("episode-a", "scope-a", "project-a", "Episode/01"),
            ),
            (
                "episode-b".into(),
                root("episode-b", "scope-b", "project-b", "Episode/02"),
            ),
        ]);
        let mut event = change("scope-b", "project-b", "Episode/02/beat01.mp4");
        event.action = "move".into();
        event.previous_scope_id = Some("scope-a".into());
        event.previous_project_id = Some("project-a".into());
        event.previous_logical_path = Some("Episode/01/beat01.mp4".into());
        let mut ids: Vec<_> = roots_for_change(&roots, &event)
            .iter()
            .map(|root| root.config.root_id.as_str())
            .collect();
        ids.sort();
        assert_eq!(ids, vec!["episode-a", "episode-b"]);
    }

    #[test]
    fn deleted_retarget_is_routed_to_both_old_and_new_roots() {
        let roots = HashMap::from([
            (
                "episode-a".into(),
                root("episode-a", "scope-a", "project-a", "Episode/01"),
            ),
            (
                "episode-b".into(),
                root("episode-b", "scope-b", "project-b", "Episode/02"),
            ),
        ]);
        let mut event = change("scope-b", "project-b", "Episode/02/beat01.mp4");
        event.action = "retarget_deleted".into();
        event.previous_scope_id = Some("scope-a".into());
        event.previous_project_id = Some("project-a".into());
        event.previous_logical_path = Some("Episode/01/beat01.mp4".into());
        let mut ids: Vec<_> = roots_for_change(&roots, &event)
            .iter()
            .map(|root| root.config.root_id.as_str())
            .collect();
        ids.sort();
        assert_eq!(ids, vec!["episode-a", "episode-b"]);
    }

    #[test]
    fn unchanged_large_file_fingerprint_skips_content_hash() {
        let baseline = SyncBaseline {
            root_id: "r".into(),
            logical_path: "p".into(),
            scope_id: "s".into(),
            project_id: Some("project".into()),
            file_id: "f".into(),
            revision_id: "rev".into(),
            version: 4,
            size: 2_000_000_000,
            sha256: "a".repeat(64),
            deleted: false,
            conflicted: false,
        };
        let fingerprint = (2_000_000_000, 123_456_789);
        assert!(!needs_local_hash(
            Some(&baseline),
            "f",
            &baseline.sha256,
            fingerprint.0,
            fingerprint.1,
            Some(&fingerprint)
        ));
    }
}

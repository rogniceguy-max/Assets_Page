#![allow(unused_imports)]

use std::{
    collections::{HashMap, HashSet, VecDeque},
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use anyhow::{Context, Result, anyhow};
use notify::{
    Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher,
    event::{ModifyKind, RenameMode},
};
use serde_json::json;
use sha2::{Digest, Sha256};
use tokio::{
    sync::mpsc,
    time::{interval, sleep},
};
use tracing::{debug, warn};
use uuid::Uuid;

use crate::{
    AgentConfig, AgentEvent, AgentState, SyncRoot,
    api::{self, ChangeItem, FileCatalogItem},
    queue::{NewTask, SyncBaseline, TaskKind, TaskRecord, TaskSnapshot, TaskState},
};
use common::{ensure_safe_local_path, reject_reparse_root_ancestry};

const EVENT_DEBOUNCE: Duration = Duration::from_millis(900);
const FEED_POLL: Duration = Duration::from_secs(2);
const RECONCILE_INTERVAL: Duration = Duration::from_secs(30 * 60);
const CONFLICT_COPY_LIMIT: usize = 64;

#[derive(Debug)]
pub enum WatcherCommand {
    Configure(Vec<SyncRoot>),
    Rescan(String),
    Logout(tokio::sync::oneshot::Sender<()>),
    Shutdown,
}

pub(crate) fn ensure_safe_sync_path(
    config: &AgentConfig,
    root_id: &str,
    path: &Path,
) -> Result<()> {
    let root_config = config
        .sync_roots
        .iter()
        .find(|root| root.root_id == root_id)
        .ok_or_else(|| anyhow!("sync root is no longer configured"))?;
    reject_reparse_root_ancestry(Path::new(&root_config.path))?;
    let canonical = std::fs::canonicalize(&root_config.path)
        .with_context(|| format!("canonicalize sync root {}", root_config.path))?;
    let context = RootContext {
        config: root_config.clone(),
        canonical,
    };
    ensure_safe_local_path(&context, path)
}

#[derive(Clone)]
struct RootContext {
    config: SyncRoot,
    canonical: PathBuf,
}

struct LocalFile {
    path: PathBuf,
    logical_path: String,
    size: u64,
    modified_ns: i64,
}

struct LocalDirectory {
    path: PathBuf,
    logical_path: String,
}

mod common;
mod events;
mod feed;
mod reconcile;
pub(crate) use events::run;

#[cfg(test)]
mod path_safety_tests {
    use super::*;

    fn test_config(root_id: &str, path: &Path) -> AgentConfig {
        AgentConfig {
            api_base_url: "https://example.invalid".into(),
            bearer_token: "test".into(),
            client_id: "test-client".into(),
            sync_roots: vec![SyncRoot {
                root_id: root_id.into(),
                path: path.to_string_lossy().into_owned(),
                logical_path_prefix: "Episode".into(),
                scope_id: Some("scope".into()),
                project_id: None,
            }],
        }
    }

    #[test]
    fn sync_download_path_must_remain_inside_its_configured_root() {
        let base = std::env::temp_dir().join(format!("transfer-path-{}", Uuid::new_v4()));
        let root = base.join("sync");
        let outside = base.join("outside");
        std::fs::create_dir_all(&root).unwrap();
        std::fs::create_dir_all(&outside).unwrap();
        let config = test_config("root", &root);

        assert!(ensure_safe_sync_path(&config, "root", &outside.join("file.mp4")).is_err());
        let _ = std::fs::remove_dir_all(base);
    }

    #[cfg(unix)]
    #[test]
    fn sync_download_path_rejects_symlink_ancestry() {
        use std::os::unix::fs::symlink;

        let base = std::env::temp_dir().join(format!("transfer-path-{}", Uuid::new_v4()));
        let root = base.join("sync");
        let outside = base.join("outside");
        std::fs::create_dir_all(&root).unwrap();
        std::fs::create_dir_all(&outside).unwrap();
        symlink(&outside, root.join("redirect")).unwrap();
        let config = test_config("root", &root);

        assert!(ensure_safe_sync_path(
            &config,
            "root",
            &root.join("redirect").join("file.mp4")
        )
        .is_err());
        let _ = std::fs::remove_dir_all(base);
    }
}

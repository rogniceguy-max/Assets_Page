mod api;
mod ipc;
mod queue;
mod transfer;
mod watcher;

use std::{env, path::PathBuf, sync::Arc};

use anyhow::{Context, Result};
use queue::QueueStore;
use sha2::{Digest, Sha256};
use tokio::sync::{RwLock, broadcast, mpsc};
use tracing::{info, warn};

pub const AGENT_VERSION: &str = env!("CARGO_PKG_VERSION");

#[derive(Clone, Debug, serde::Deserialize, serde::Serialize)]
pub struct AgentConfig {
    pub api_base_url: String,
    #[serde(skip_serializing)]
    pub bearer_token: String,
    pub client_id: String,
    #[serde(default)]
    pub sync_roots: Vec<SyncRoot>,
}

#[derive(Clone, Debug, serde::Deserialize, serde::Serialize)]
pub struct SyncRoot {
    pub root_id: String,
    pub path: String,
    pub logical_path_prefix: String,
    #[serde(default)]
    pub scope_id: Option<String>,
    #[serde(default)]
    pub project_id: Option<String>,
}

#[derive(Clone, Debug, serde::Serialize)]
pub struct AgentEvent {
    pub event: String,
    pub seq: u64,
    pub task_id: Option<String>,
    pub operation_id: Option<String>,
    pub payload: serde_json::Value,
}

pub struct AgentState {
    pub config: RwLock<Option<AgentConfig>>,
    pub worker_gate: tokio::sync::Mutex<()>,
    pub queue: QueueStore,
    pub events: broadcast::Sender<AgentEvent>,
    pub watcher_commands: mpsc::Sender<watcher::WatcherCommand>,
    pub api: reqwest::Client,
    pub next_event_seq: std::sync::atomic::AtomicU64,
    pub shutting_down: std::sync::atomic::AtomicBool,
    pub shutdown_notify: tokio::sync::Notify,
    pub worker_status: tokio::sync::watch::Sender<bool>,
}

impl AgentState {
    fn emit(&self, mut event: AgentEvent) {
        event.seq = self
            .next_event_seq
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let _ = self.events.send(event);
    }
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::from_default_env().add_directive("info".parse()?),
        )
        .init();

    #[cfg(not(windows))]
    {
        anyhow::bail!("muying-transfer-agent is a Windows per-user process");
    }

    #[cfg(windows)]
    run_windows().await
}

#[cfg(windows)]
async fn run_windows() -> Result<()> {
    let pipe_name = parse_pipe_name()?;
    let state_namespace = parse_state_namespace()?;
    let data_dir = local_state_dir(&state_namespace)?;
    std::fs::create_dir_all(&data_dir).context("create local transfer state directory")?;
    let queue = QueueStore::open(data_dir.join("agent.sqlite"))?;
    let (events, _) = broadcast::channel(512);
    let (watcher_commands, watcher_rx) = mpsc::channel(128);
    let (worker_status, _) = tokio::sync::watch::channel(false);
    let state = Arc::new(AgentState {
        config: RwLock::new(None),
        worker_gate: tokio::sync::Mutex::new(()),
        queue,
        events,
        watcher_commands,
        api: reqwest::Client::builder()
            .connect_timeout(std::time::Duration::from_secs(10))
            .timeout(std::time::Duration::from_secs(120))
            .build()?,
        next_event_seq: std::sync::atomic::AtomicU64::new(1),
        shutting_down: std::sync::atomic::AtomicBool::new(false),
        shutdown_notify: tokio::sync::Notify::new(),
        worker_status,
    });

    let watcher_task = tokio::spawn(watcher::run(state.clone(), watcher_rx));
    let worker_task = tokio::spawn(transfer::run_workers(state.clone()));

    info!("per-user transfer agent ready");
    let pipe_result = ipc::run_pipe_server(pipe_name, state.clone()).await;
    state
        .shutting_down
        .store(true, std::sync::atomic::Ordering::Release);
    state.shutdown_notify.notify_waiters();
    let _ = state.watcher_commands.send(watcher::WatcherCommand::Shutdown).await;
    let _ = worker_task.await;
    let _ = watcher_task.await;
    pipe_result
}

fn parse_pipe_name() -> Result<String> {
    let mut args = env::args().skip(1);
    let mut pipe = None;
    while let Some(arg) = args.next() {
        if arg == "--pipe-name" {
            pipe = args.next();
        }
    }
    let pipe = pipe.context("missing --pipe-name")?;
    let normalized_input = pipe.replace('/', "\\").to_ascii_lowercase();
    let candidate = if normalized_input.starts_with(r"\\.\pipe\") {
        normalized_input
            .strip_prefix(r"\\.\pipe\")
            .unwrap_or_default()
    } else {
        normalized_input.as_str()
    };
    let prefix = "muyingdrive.transferagent.v1.";
    if !candidate.starts_with(prefix)
        || candidate.len() != prefix.len() + 24
        || !candidate[prefix.len()..]
            .bytes()
            .all(|byte| byte.is_ascii_hexdigit())
        || candidate.contains('/')
        || candidate.contains('\\')
    {
        anyhow::bail!("pipe name is outside the expected namespace");
    }
    let normalized_name = format!("MuYingDrive.TransferAgent.v1.{}", &candidate[prefix.len()..]);
    Ok(format!(r"\\.\pipe\{normalized_name}"))
}

fn parse_state_namespace() -> Result<String> {
    let mut args = env::args().skip(1);
    let mut namespace = None;
    while let Some(arg) = args.next() {
        if arg == "--state-namespace" {
            if namespace.is_some() {
                anyhow::bail!("--state-namespace may only be specified once");
            }
            namespace = args.next();
        }
    }
    let namespace = namespace.context("missing --state-namespace")?;
    if namespace.trim().is_empty()
        || namespace.len() > 1024
        || namespace.chars().any(char::is_control)
    {
        anyhow::bail!("--state-namespace must be a non-empty opaque value of at most 1024 bytes");
    }
    Ok(namespace)
}

fn state_namespace_component(namespace: &str) -> Result<String> {
    if namespace.trim().is_empty()
        || namespace.len() > 1024
        || namespace.chars().any(char::is_control)
    {
        anyhow::bail!("invalid state namespace");
    }
    Ok(hex::encode(Sha256::digest(namespace.as_bytes())))
}

fn local_state_dir(namespace: &str) -> Result<PathBuf> {
    let base = env::var_os("LOCALAPPDATA").context("LOCALAPPDATA is not set")?;
    Ok(PathBuf::from(base)
        .join("MuYingDrive")
        .join("transfer")
        .join(state_namespace_component(namespace)?))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn app_state_namespaces_are_hashed_and_isolated() {
        let employee = state_namespace_component("employee:42").unwrap();
        let visitor = state_namespace_component("visitor:session-hash").unwrap();
        assert_ne!(employee, visitor);
        assert_eq!(employee.len(), 64);
        assert!(!employee.contains("employee"));
    }

    #[test]
    fn empty_or_unbounded_state_namespaces_are_rejected() {
        assert!(state_namespace_component("  ").is_err());
        assert!(state_namespace_component(&"x".repeat(1025)).is_err());
    }
}

#[cfg(not(windows))]
fn _unsupported_platform_notice() {
    warn!("this binary is only supported on Windows");
}

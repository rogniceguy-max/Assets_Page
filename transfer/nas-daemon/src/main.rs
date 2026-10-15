mod server;
mod store;

use std::{env, path::PathBuf, sync::Arc};

use anyhow::{Context, Result, bail};
use server::{TransferConfig, TransferState};
use tokio::sync::Semaphore;
use tracing::info;
use muying_transfer_common::protocol::MAX_BATCH_ITEMS;

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::from_default_env().add_directive("info".parse()?),
        )
        .init();

    let config = TransferConfig::from_env()?;
    std::fs::create_dir_all(&config.objects_root)?;
    std::fs::create_dir_all(&config.staging_root)?;
    let state = Arc::new(TransferState {
        store: store::TransferStore::open(&config.state_db)?,
        config: config.clone(),
        large_slots: Arc::new(Semaphore::new(config.max_large_uploads)),
        batch_slots: Arc::new(Semaphore::new(config.max_small_batches)),
        request_slots: Arc::new(Semaphore::new(config.max_active_requests)),
        download_slots: Arc::new(Semaphore::new(config.max_active_downloads)),
        hash_slots: Arc::new(Semaphore::new(config.max_concurrent_hashes)),
        object_locks: Default::default(),
    });
    tokio::spawn(server::run_staging_cleanup(state.clone()));

    let listener = tokio::net::TcpListener::bind(&config.bind)
        .await
        .with_context(|| format!("bind transfer listener at {}", config.bind))?;
    info!(bind = %config.bind, "NAS transfer daemon listening");
    axum::serve(listener, server::router(state)).await?;
    Ok(())
}

fn required(name: &str) -> Result<String> {
    env::var(name).with_context(|| format!("missing required environment variable {name}"))
}

fn positive_usize(name: &str, default: usize, max: usize) -> Result<usize> {
    let value = env::var(name).unwrap_or_else(|_| default.to_string());
    let parsed: usize = value
        .parse()
        .with_context(|| format!("{name} must be an integer"))?;
    if parsed == 0 || parsed > max {
        bail!("{name} must be between 1 and {max}");
    }
    Ok(parsed)
}

fn positive_u64(name: &str, default: u64, min: u64, max: u64) -> Result<u64> {
    let value = env::var(name).unwrap_or_else(|_| default.to_string());
    let parsed: u64 = value
        .parse()
        .with_context(|| format!("{name} must be an integer"))?;
    if parsed < min || parsed > max {
        bail!("{name} must be between {min} and {max}");
    }
    Ok(parsed)
}

impl TransferConfig {
    fn from_env() -> Result<Self> {
        let objects_root = PathBuf::from(required("MUYING_V2_TRANSFER_OBJECTS_ROOT")?);
        let staging_root = PathBuf::from(required("MUYING_V2_TRANSFER_STAGING_ROOT")?);
        let state_db = PathBuf::from(
            env::var("MUYING_V2_TRANSFER_STATE_DB")
                .unwrap_or_else(|_| "/var/lib/muying-transfer/state.sqlite".to_owned()),
        );
        let bind =
            env::var("MUYING_V2_TRANSFER_BIND").unwrap_or_else(|_| "127.0.0.1:8787".to_owned());
        let secret_text = required("MUYING_V2_TRANSFER_TICKET_SECRET")?;
        let ticket_secret = decode_secret(&secret_text)?;
        Ok(Self {
            bind,
            objects_root,
            staging_root,
            state_db,
            ticket_secret: Arc::new(ticket_secret),
            max_large_uploads: positive_usize("MUYING_V2_TRANSFER_MAX_LARGE_UPLOADS", 1, 16)?,
            max_small_batches: positive_usize("MUYING_V2_TRANSFER_MAX_SMALL_BATCHES", 1, 16)?,
            max_active_requests: positive_usize("MUYING_V2_TRANSFER_MAX_REQUESTS", 4, 128)?,
            max_active_downloads: positive_usize("MUYING_V2_TRANSFER_MAX_DOWNLOADS", 2, 16)?,
            max_concurrent_hashes: positive_usize("MUYING_V2_TRANSFER_MAX_HASHES", 1, 4)?,
            max_upload_bytes: env::var("MUYING_V2_TRANSFER_MAX_UPLOAD_BYTES")
                .ok()
                .and_then(|value| value.parse().ok())
                .unwrap_or(100 * 1024 * 1024 * 1024),
            max_staging_bytes_per_user: env::var("MUYING_V2_TRANSFER_MAX_STAGING_BYTES_PER_USER")
                .ok()
                .and_then(|value| value.parse().ok())
                .unwrap_or(256 * 1024 * 1024 * 1024),
            max_receiving_uploads_per_user: positive_usize(
                "MUYING_V2_TRANSFER_MAX_RECEIVING_UPLOADS_PER_USER",
                32,
                4096,
            )?,
            max_staging_bytes_total: env::var("MUYING_V2_TRANSFER_MAX_STAGING_BYTES_TOTAL")
                .ok()
                .and_then(|value| value.parse().ok())
                .unwrap_or(1024 * 1024 * 1024 * 1024),
            max_receiving_uploads_total: positive_usize(
                "MUYING_V2_TRANSFER_MAX_RECEIVING_UPLOADS_TOTAL",
                128,
                16384,
            )?,
            staging_ttl_seconds: positive_u64(
                "MUYING_V2_TRANSFER_STAGING_TTL_SECONDS",
                7 * 24 * 60 * 60,
                60 * 60,
                90 * 24 * 60 * 60,
            )?,
            max_batch_items: positive_usize(
                "MUYING_V2_TRANSFER_MAX_BATCH_ITEMS",
                MAX_BATCH_ITEMS,
                MAX_BATCH_ITEMS,
            )?,
            max_batch_item_bytes: env::var("MUYING_V2_TRANSFER_MAX_BATCH_ITEM_BYTES")
                .ok()
                .and_then(|value| value.parse().ok())
                .unwrap_or(16 * 1024 * 1024),
            max_batch_bytes: env::var("MUYING_V2_TRANSFER_MAX_BATCH_BYTES")
                .ok()
                .and_then(|value| value.parse().ok())
                .unwrap_or(256 * 1024 * 1024),
        })
    }
}

fn decode_secret(secret_text: &str) -> Result<Vec<u8>> {
    // Match the Python API contract exactly: the environment variable's UTF-8 bytes are
    // the HMAC key. Do not decode prefixes or reinterpret the secret.
    let decoded = secret_text.as_bytes().to_vec();
    if decoded.len() < 32 {
        anyhow::bail!("transfer ticket secret must be at least 32 bytes");
    }
    Ok(decoded)
}

use std::{
    collections::{HashMap, HashSet},
    path::{Path, PathBuf},
    sync::{Arc, Weak},
    time::{SystemTime, UNIX_EPOCH},
};

use axum::{
    Json, Router,
    body::Body,
    extract::{DefaultBodyLimit, Multipart, Path as AxumPath, State},
    http::{HeaderMap, StatusCode, header},
    response::{IntoResponse, Response},
    routing::{get, head, post, put},
};
use chrono::{SecondsFormat, Utc};
use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tokio::{
    fs::{self, File, OpenOptions},
    io::{AsyncReadExt, AsyncSeekExt, AsyncWriteExt, SeekFrom},
    sync::{Mutex, Semaphore},
};
use tokio_util::io::ReaderStream;
use tracing::{debug, warn};
use uuid::Uuid;

use muying_transfer_common::{
    protocol::{
        BatchUploadManifest, BatchUploadResponse, BatchUploadResult, ByteRange, DEFAULT_CHUNK_SIZE,
        MAX_BATCH_ITEMS, MAX_CHUNK_SIZE, StagedObjectReceipt, UploadCommitResponse, UploadInitRequest,
        UploadInitResponse,
    },
    ticket::{TicketClaims, sign_receipt, verify_ticket, verify_ticket_for_resume},
};

use crate::store::{DownloadRow, TransferStore, UploadRow};

#[derive(Clone)]
pub struct TransferConfig {
    pub bind: String,
    /// Filesystem root corresponding to the logical `_objects` directory itself.
    pub objects_root: PathBuf,
    pub staging_root: PathBuf,
    pub state_db: PathBuf,
    pub ticket_secret: Arc<Vec<u8>>,
    pub max_large_uploads: usize,
    pub max_small_batches: usize,
    pub max_active_requests: usize,
    pub max_active_downloads: usize,
    pub max_concurrent_hashes: usize,
    pub max_upload_bytes: u64,
    pub max_staging_bytes_per_user: u64,
    pub max_receiving_uploads_per_user: usize,
    pub max_staging_bytes_total: u64,
    pub max_receiving_uploads_total: usize,
    pub staging_ttl_seconds: u64,
    pub max_batch_items: usize,
    pub max_batch_item_bytes: u64,
    pub max_batch_bytes: u64,
}

pub struct TransferState {
    pub store: TransferStore,
    pub config: TransferConfig,
    pub large_slots: Arc<Semaphore>,
    pub batch_slots: Arc<Semaphore>,
    pub request_slots: Arc<Semaphore>,
    pub download_slots: Arc<Semaphore>,
    pub hash_slots: Arc<Semaphore>,
    pub object_locks: Mutex<HashMap<String, Weak<Mutex<()>>>>,
}

pub fn router(state: Arc<TransferState>) -> Router {
    let batch_limit = (state.config.max_batch_bytes as usize).saturating_add(1024 * 1024);
    let chunk_routes = Router::new()
        .route("/transfer/v1/uploads/{upload_id}/chunks", put(put_chunk))
        .layer(DefaultBodyLimit::max(MAX_CHUNK_SIZE as usize));
    let batch_routes = Router::new()
        .route("/transfer/v1/uploads/batch", post(upload_batch))
        .layer(DefaultBodyLimit::max(batch_limit));
    Router::new()
        .route("/healthz", get(|| async { StatusCode::NO_CONTENT }))
        .route("/transfer/v1/uploads", post(init_upload))
        .route("/transfer/v1/uploads/{upload_id}", head(head_upload))
        .route(
            "/transfer/v1/uploads/{upload_id}/commit",
            post(commit_upload),
        )
        .route(
            "/transfer/v1/uploads/{upload_id}/abort",
            post(abort_upload_session),
        )
        .route("/transfer/v1/downloads", post(init_download))
        .route(
            "/transfer/v1/downloads/{download_id}",
            get(download_object).head(download_head),
        )
        .merge(chunk_routes)
        .merge(batch_routes)
        .with_state(state)
}

#[derive(Debug)]
struct ApiError {
    status: StatusCode,
    code: &'static str,
    message: String,
}

impl ApiError {
    fn new(status: StatusCode, code: &'static str, message: impl Into<String>) -> Self {
        Self {
            status,
            code,
            message: message.into(),
        }
    }
    fn unauthorized(message: impl Into<String>) -> Self {
        Self::new(StatusCode::UNAUTHORIZED, "invalid_transfer_ticket", message)
    }
    fn bad_request(code: &'static str, message: impl Into<String>) -> Self {
        Self::new(StatusCode::BAD_REQUEST, code, message)
    }
    fn conflict(code: &'static str, message: impl Into<String>) -> Self {
        Self::new(StatusCode::CONFLICT, code, message)
    }
    fn internal(message: impl Into<String>) -> Self {
        Self::new(
            StatusCode::INTERNAL_SERVER_ERROR,
            "transfer_internal_error",
            message,
        )
    }
}

#[derive(Serialize)]
struct ErrorEnvelope {
    error: ErrorDetail,
}

#[derive(Serialize)]
struct ErrorDetail {
    code: &'static str,
    message: String,
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let body = Json(ErrorEnvelope {
            error: ErrorDetail {
                code: self.code,
                message: self.message,
            },
        });
        (self.status, body).into_response()
    }
}

fn internal(error: impl std::fmt::Display) -> ApiError {
    ApiError::internal(error.to_string())
}

fn upload_store_error(error: anyhow::Error) -> ApiError {
    let message = error.to_string();
    if message.starts_with("staging_quota_exceeded") {
        ApiError::new(
            StatusCode::TOO_MANY_REQUESTS,
            "staging_quota_exceeded",
            "the user's incomplete upload staging quota is full",
        )
    } else if message.starts_with("upload_expired") {
        ApiError::new(
            StatusCode::GONE,
            "upload_expired",
            "the incomplete upload expired and must be restarted",
        )
    } else if message.starts_with("upload_aborted") {
        ApiError::new(
            StatusCode::GONE,
            "upload_aborted",
            "the upload was aborted and cannot be resumed",
        )
    } else {
        internal(error)
    }
}

fn now_seconds() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

fn verify_new_ticket(state: &TransferState, token: &str) -> Result<TicketClaims, ApiError> {
    verify_ticket(token, &state.config.ticket_secret, now_seconds())
        .map_err(|_| ApiError::unauthorized("ticket is invalid or expired"))
}

fn verify_resumable_ticket(
    state: &TransferState,
    token: &str,
    existing: bool,
) -> Result<TicketClaims, ApiError> {
    match verify_new_ticket(state, token) {
        Ok(claims) => Ok(claims),
        Err(error) if existing => {
            verify_ticket_for_resume(token, &state.config.ticket_secret).map_err(|_| error)
        }
        Err(error) => Err(error),
    }
}

fn validate_upload_claims(
    claims: &TicketClaims,
    manifest: &muying_transfer_common::protocol::UploadManifest,
) -> Result<(), ApiError> {
    if claims.action != "upload"
        || claims.direction != "upload"
        || claims.op_id != manifest.op_id
        || claims.object_id != manifest.object_id
        || claims.object_kind != manifest.object_kind
        || claims.expected_version != manifest.expected_version
        || claims.size.is_some_and(|size| size != manifest.size)
        || claims
            .sha256
            .as_ref()
            .is_some_and(|hash| !hash.eq_ignore_ascii_case(&manifest.sha256))
    {
        return Err(ApiError::unauthorized(
            "ticket does not authorize this upload manifest",
        ));
    }
    if Uuid::parse_str(&manifest.op_id).is_err() || Uuid::parse_str(&manifest.object_id).is_err() {
        return Err(ApiError::bad_request(
            "invalid_identifier",
            "operation and object IDs must be UUIDs",
        ));
    }
    if manifest.sha256.len() != 64 || !manifest.sha256.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(ApiError::bad_request(
            "invalid_hash",
            "manifest SHA-256 must be 64 hexadecimal characters",
        ));
    }
    Ok(())
}

async fn acquire_slot(
    semaphore: &Semaphore,
    queue_name: &'static str,
) -> Result<tokio::sync::OwnedSemaphorePermit, ApiError> {
    semaphore.clone().try_acquire_owned().map_err(|_| {
        ApiError::new(
            StatusCode::TOO_MANY_REQUESTS,
            "transfer_busy",
            format!("{queue_name} queue is at capacity; retry shortly"),
        )
    })
}

async fn init_upload(
    State(state): State<Arc<TransferState>>,
    Json(request): Json<UploadInitRequest>,
) -> Result<Json<UploadInitResponse>, ApiError> {
    let _request_slot = acquire_slot(&state.request_slots, "request").await?;
    let claims = verify_new_ticket(&state, &request.ticket)?;
    validate_upload_claims(&claims, &request.manifest)?;
    if request.manifest.size > state.config.max_upload_bytes {
        return Err(ApiError::new(
            StatusCode::PAYLOAD_TOO_LARGE,
            "upload_too_large",
            "upload exceeds the configured size limit",
        ));
    }
    let chunk_size = DEFAULT_CHUNK_SIZE;
    let row = state
        .store
        .ensure_upload(
            &claims.op_id,
            &claims.object_id,
            &claims.object_kind,
            &claims.user_id,
            claims.expected_version,
            request.manifest.size,
            &request.manifest.sha256,
            chunk_size,
            state.config.max_staging_bytes_per_user,
            state.config.max_receiving_uploads_per_user,
            state.config.max_staging_bytes_total,
            state.config.max_receiving_uploads_total,
        )
        .map_err(upload_store_error)?;
    let lock = upload_lock(&state, &row.upload_id).await;
    let _guard = lock.lock().await;
    let row = state
        .store
        .upload(&row.upload_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "upload_not_found",
                "upload session was not found",
            )
        })?;
    validate_upload_row(&claims, &row)?;
    ensure_stage_file(&state, &row).await?;
    let ranges = missing_ranges(&state, &row)?;
    let received_bytes = row.size.saturating_sub(
        ranges
            .iter()
            .map(|range| range.end - range.start + 1)
            .sum::<u64>(),
    );
    Ok(Json(UploadInitResponse {
        upload_id: row.upload_id,
        chunk_size: row.chunk_size,
        received_bytes,
        missing_ranges: ranges,
    }))
}

async fn head_upload(
    State(state): State<Arc<TransferState>>,
    AxumPath(upload_id): AxumPath<String>,
    headers: HeaderMap,
) -> Result<Response, ApiError> {
    let row = state
        .store
        .upload(&upload_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "upload_not_found",
                "upload session was not found",
            )
        })?;
    let token = header_str(&headers, "x-transfer-ticket")?;
    let claims = verify_resumable_ticket(&state, token, true)?;
    validate_upload_row(&claims, &row)?;
    let ranges = missing_ranges(&state, &row)?;
    let ranges_text = ranges
        .iter()
        .map(|range| format!("{}-{}", range.start, range.end))
        .collect::<Vec<_>>()
        .join(",");
    let mut response = Response::builder().status(StatusCode::OK);
    response = response.header("Upload-Length", row.size.to_string());
    response = response.header("Upload-Chunk-Size", row.chunk_size.to_string());
    response = response.header("Upload-Missing-Ranges", ranges_text);
    response.body(Body::empty()).map_err(internal)
}

async fn put_chunk(
    State(state): State<Arc<TransferState>>,
    AxumPath(upload_id): AxumPath<String>,
    headers: HeaderMap,
    body: axum::body::Bytes,
) -> Result<StatusCode, ApiError> {
    let _request_slot = acquire_slot(&state.request_slots, "request").await?;
    let _large_slot = acquire_slot(&state.large_slots, "large-upload").await?;
    let row = state
        .store
        .upload(&upload_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "upload_not_found",
                "upload session was not found",
            )
        })?;
    let token = header_str(&headers, "x-transfer-ticket")?;
    let claims = verify_resumable_ticket(&state, token, true)?;
    validate_upload_row(&claims, &row)?;
    if row.state == "staged" {
        return Ok(StatusCode::OK);
    }
    let range_text = header_str(&headers, "content-range")?;
    let (start, end, total) = parse_content_range(range_text)?;
    if total != row.size
        || start > end
        || end >= total
        || end - start + 1 != body.len() as u64
        || body.len() as u64 > row.chunk_size
        || start % row.chunk_size != 0
        || end != (start + row.chunk_size - 1).min(total.saturating_sub(1))
    {
        return Err(ApiError::bad_request(
            "invalid_content_range",
            "chunk range does not match the upload session",
        ));
    }
    let supplied_hash = header_str(&headers, "x-chunk-sha256")?;
    let chunk_hash = hex::encode(Sha256::digest(&body));
    if !chunk_hash.eq_ignore_ascii_case(supplied_hash) {
        return Err(ApiError::new(
            StatusCode::UNPROCESSABLE_ENTITY,
            "chunk_hash_mismatch",
            "chunk SHA-256 does not match",
        ));
    }
    let lock = upload_lock(&state, &upload_id).await;
    let _guard = lock.lock().await;
    let row = state
        .store
        .upload(&upload_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "upload_not_found",
                "upload session was not found",
            )
        })?;
    validate_upload_row(&claims, &row)?;
    if row.state == "staged" {
        return Ok(StatusCode::OK);
    }
    if let Some(existing_hash) = state
        .store
        .chunk_hash(&upload_id, start)
        .map_err(internal)?
    {
        if existing_hash.eq_ignore_ascii_case(&chunk_hash) {
            return Ok(StatusCode::OK);
        }
        return Err(ApiError::conflict(
            "chunk_conflict",
            "this byte range was already stored with different contents",
        ));
    }
    let path = staging_path(&state, &row);
    let mut file = OpenOptions::new()
        .create(true)
        .read(true)
        .write(true)
        .open(path)
        .await
        .map_err(internal)?;
    file.seek(SeekFrom::Start(start)).await.map_err(internal)?;
    file.write_all(&body).await.map_err(internal)?;
    file.sync_data().await.map_err(internal)?;
    state
        .store
        .record_chunk(&upload_id, start, end, &chunk_hash)
        .map_err(internal)?;
    Ok(StatusCode::CREATED)
}

async fn commit_upload(
    State(state): State<Arc<TransferState>>,
    AxumPath(upload_id): AxumPath<String>,
    headers: HeaderMap,
) -> Result<Json<UploadCommitResponse>, ApiError> {
    let _request_slot = acquire_slot(&state.request_slots, "request").await?;
    let row = state
        .store
        .upload(&upload_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "upload_not_found",
                "upload session was not found",
            )
        })?;
    let token = header_str(&headers, "x-transfer-ticket")?;
    let claims = verify_resumable_ticket(&state, token, true)?;
    validate_upload_row(&claims, &row)?;
    let lock = upload_lock(&state, &upload_id).await;
    let _guard = lock.lock().await;
    if let Some(receipt) = row.receipt {
        return Ok(Json(commit_response(receipt)));
    }
    let row = state
        .store
        .upload(&upload_id)
        .map_err(internal)?
        .ok_or_else(|| ApiError::new(StatusCode::NOT_FOUND, "upload_not_found", "upload session was not found"))?;
    validate_upload_row(&claims, &row)?;
    if let Some(receipt) = row.receipt.clone() {
        return Ok(Json(commit_response(receipt)));
    }
    if !missing_ranges(&state, &row)?.is_empty() {
        return Err(ApiError::conflict(
            "upload_incomplete",
            "one or more chunks are still missing",
        ));
    }
    let stage = staging_path(&state, &row);
    let stage_metadata = fs::metadata(&stage).await;
    match stage_metadata {
        Ok(metadata) => {
            if metadata.len() != row.size {
                return Err(ApiError::conflict(
                    "upload_incomplete",
                    "staged size does not match the manifest",
                ));
            }
            let actual_hash = hash_file(&state, stage.clone()).await.map_err(internal)?;
            if !actual_hash.eq_ignore_ascii_case(&row.sha256) {
                return Err(ApiError::new(
                    StatusCode::UNPROCESSABLE_ENTITY,
                    "file_hash_mismatch",
                    "final SHA-256 does not match the manifest",
                ));
            }
            publish_object(
                &state,
                &row.object_id,
                &row.object_kind,
                &stage,
                row.size,
                &row.sha256,
            )
            .await?;
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            // The publish rename may have completed just before a crash, while the receipt
            // transaction did not. Verify the immutable target and finish receipt creation.
            let published = object_path(&state, &row.object_id, &row.object_kind)?;
            let metadata = fs::metadata(&published).await.map_err(|_| {
                ApiError::conflict(
                    "upload_incomplete",
                    "staged content and its published object are both missing",
                )
            })?;
            if metadata.len() != row.size
                || !hash_file(&state, published).await.map_err(internal)?.eq_ignore_ascii_case(&row.sha256)
            {
                return Err(ApiError::conflict(
                    "object_id_collision",
                    "published object differs from the upload manifest",
                ));
            }
        }
        Err(error) => return Err(internal(error)),
    }
    let receipt = make_receipt(&state, &row).await?;
    state
        .store
        .stage_receipt(&upload_id, &receipt)
        .map_err(internal)?;
    Ok(Json(commit_response(receipt)))
}

#[derive(Serialize)]
struct UploadAbortResponse {
    upload_id: String,
    state: String,
}

async fn abort_upload_session(
    State(state): State<Arc<TransferState>>,
    AxumPath(upload_id): AxumPath<String>,
    headers: HeaderMap,
) -> Result<Json<UploadAbortResponse>, ApiError> {
    let _request_slot = acquire_slot(&state.request_slots, "request").await?;
    let token = header_str(&headers, "x-transfer-ticket")?;
    let claims = verify_resumable_ticket(&state, token, true)?;
    if claims.action != "upload"
        || claims.direction != "upload"
        || claims.op_id != upload_id
    {
        return Err(ApiError::unauthorized(
            "ticket does not authorize this upload session",
        ));
    }
    if state.store.upload(&upload_id).map_err(internal)?.is_none() {
        state
            .store
            .record_aborted_upload(
                &claims.op_id,
                &claims.object_id,
                &claims.object_kind,
                &claims.user_id,
                claims.expected_version,
                claims.size.unwrap_or_default(),
                claims.sha256.as_deref().unwrap_or_default(),
                DEFAULT_CHUNK_SIZE,
            )
            .map_err(internal)?;
    }
    let initial = state
        .store
        .upload(&upload_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "upload_not_found",
                "upload session was not found",
            )
        })?;
    validate_upload_identity(&claims, &initial)?;
    let lock = upload_lock(&state, &upload_id).await;
    let _guard = lock.lock().await;
    let row = state
        .store
        .upload(&upload_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "upload_not_found",
                "upload session was not found",
            )
        })?;
    validate_upload_identity(&claims, &row)?;
    let abort_state = match row.state.as_str() {
        "staged" => {
            return Err(ApiError::conflict(
                "upload_already_staged",
                "the immutable object is already staged and cannot be aborted",
            ));
        }
        "receiving" => state.store.abort_upload(&upload_id).map_err(internal)?,
        "aborted" | "expired" => row.state.clone(),
        _ => {
            return Err(ApiError::conflict(
                "upload_state_invalid",
                "the upload session cannot be aborted in its current state",
            ));
        }
    };
    let aborted = state
        .store
        .upload(&upload_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "upload_not_found",
                "upload session was not found",
            )
        })?;
    remove_staging_file(&state, &aborted).await?;
    Ok(Json(UploadAbortResponse {
        upload_id,
        state: abort_state,
    }))
}

pub async fn run_staging_cleanup(state: Arc<TransferState>) {
    let mut interval = tokio::time::interval(std::time::Duration::from_secs(60 * 60));
    loop {
        interval.tick().await;
        let now = now_seconds();
        if let Err(error) = state.store.purge_expired_downloads(now) {
            warn!(%error, "could not purge expired download sessions");
        }
        let cutoff = now.saturating_sub(state.config.staging_ttl_seconds as i64);
        let rows = match state.store.uploads_for_cleanup(cutoff, 256) {
            Ok(rows) => rows,
            Err(error) => {
                warn!(%error, "could not list transfer staging cleanup candidates");
                continue;
            }
        };
        for candidate in rows {
            let lock = upload_lock(&state, &candidate.upload_id).await;
            let _guard = lock.lock().await;
            let current = match state.store.upload(&candidate.upload_id) {
                Ok(Some(row)) => row,
                Ok(None) => continue,
                Err(error) => {
                    warn!(operation_id=%candidate.op_id,%error,"could not inspect stale upload");
                    continue;
                }
            };
            let mut cleanup_row = current.clone();
            if current.state == "receiving" {
                match state
                    .store
                    .expire_receiving_upload(&current.upload_id, cutoff)
                {
                    Ok(true) => cleanup_row.state = "expired".into(),
                    Ok(false) => continue,
                    Err(error) => {
                        warn!(operation_id=%current.op_id,%error,"could not expire stale upload");
                        continue;
                    }
                }
            }
            if !matches!(cleanup_row.state.as_str(), "aborted" | "expired") {
                continue;
            }
            match remove_staging_file(&state, &cleanup_row).await {
                Ok(()) => debug!(operation_id=%cleanup_row.op_id,bytes=cleanup_row.size,
                    age_seconds=now_seconds().saturating_sub(cleanup_row.created_at),"removed abandoned upload staging"),
                Err(error) => warn!(operation_id=%cleanup_row.op_id,?error,"could not remove abandoned upload staging"),
            }
        }
    }
}

fn commit_response(receipt: StagedObjectReceipt) -> UploadCommitResponse {
    UploadCommitResponse {
        op_id: receipt.op_id.clone(),
        object_id: receipt.object_id.clone(),
        size: receipt.size,
        sha256: receipt.sha256.clone(),
        staged_at: receipt.staged_at.clone(),
        receipt,
    }
}

async fn upload_batch(
    State(state): State<Arc<TransferState>>,
    mut multipart: Multipart,
) -> Result<Json<BatchUploadResponse>, ApiError> {
    let _request_slot = acquire_slot(&state.request_slots, "request").await?;
    let _batch_slot = acquire_slot(&state.batch_slots, "small-batch").await?;
    let first = multipart
        .next_field()
        .await
        .map_err(|_| ApiError::bad_request("invalid_multipart", "could not read batch manifest"))?
        .ok_or_else(|| {
            ApiError::bad_request(
                "missing_manifest",
                "multipart batch must start with a manifest field",
            )
        })?;
    if first.name() != Some("manifest") {
        return Err(ApiError::bad_request(
            "missing_manifest",
            "first multipart field must be named manifest",
        ));
    }
    let manifest_bytes = first
        .bytes()
        .await
        .map_err(|_| ApiError::bad_request("invalid_manifest", "manifest could not be read"))?;
    if manifest_bytes.len() > 1024 * 1024 {
        return Err(ApiError::new(
            StatusCode::PAYLOAD_TOO_LARGE,
            "manifest_too_large",
            "batch manifest exceeds 1 MiB",
        ));
    }
    let manifest: BatchUploadManifest = serde_json::from_slice(&manifest_bytes)
        .map_err(|_| ApiError::bad_request("invalid_manifest", "batch manifest JSON is invalid"))?;
    if manifest.items.len() > state.config.max_batch_items
        || manifest.items.len() > MAX_BATCH_ITEMS
    {
        return Err(ApiError::new(
            StatusCode::PAYLOAD_TOO_LARGE,
            "batch_too_many_items",
            "batch contains too many files",
        ));
    }
    // The logical directory list is control-plane metadata only. This service never creates
    // directories from it and never uses logical_path to derive an object path.
    let _logical_directories = manifest.directories;
    let mut entries = HashMap::new();
    let mut seen_ops = HashSet::new();
    let mut ordered_ops = Vec::new();
    let mut total_bytes = 0_u64;
    for item in manifest.items {
        let op_id = item.manifest.op_id.clone();
        if !seen_ops.insert(op_id.clone()) {
            return Err(ApiError::bad_request(
                "duplicate_operation",
                "batch contains a duplicate operation_id",
            ));
        }
        ordered_ops.push(op_id.clone());
        if item.manifest.size > state.config.max_batch_item_bytes {
            entries.insert(op_id, Err("batch_item_too_large".to_owned()));
            continue;
        }
        total_bytes = total_bytes.saturating_add(item.manifest.size);
        let claims = match verify_new_ticket(&state, &item.ticket) {
            Ok(claims) => claims,
            Err(error) => {
                entries.insert(op_id, Err(error.message));
                continue;
            }
        };
        if let Err(error) = validate_upload_claims(&claims, &item.manifest) {
            entries.insert(op_id, Err(error.message));
            continue;
        }
        if item.manifest.size > state.config.max_upload_bytes {
            entries.insert(op_id, Err("upload_too_large".to_owned()));
            continue;
        }
        entries.insert(op_id, Ok(item));
    }
    if total_bytes > state.config.max_batch_bytes {
        return Err(ApiError::new(
            StatusCode::PAYLOAD_TOO_LARGE,
            "batch_too_large",
            "batch exceeds the configured byte limit",
        ));
    }

    let mut results: HashMap<String, BatchUploadResult> = HashMap::new();
    let mut seen_files = HashSet::new();
    while let Some(field) = multipart
        .next_field()
        .await
        .map_err(|_| ApiError::bad_request("invalid_multipart", "could not read batch file"))?
    {
        let name = field.name().unwrap_or_default().to_owned();
        let Some(op_id) = name.strip_prefix("file.").map(str::to_owned) else {
            return Err(ApiError::bad_request(
                "unknown_multipart_field",
                "batch file fields must be named file.<operation_id>",
            ));
        };
        if !seen_files.insert(op_id.clone()) {
            return Err(ApiError::bad_request(
                "duplicate_file_part",
                "batch contains a duplicate file part",
            ));
        }
        let Some(entry) = entries.remove(&op_id) else {
            return Err(ApiError::bad_request(
                "unknown_operation",
                "file part is not listed in the manifest",
            ));
        };
        let item = match entry {
            Ok(item) => item,
            Err(message) => {
                drain_field(field).await?;
                results.insert(
                    op_id.clone(),
                    BatchUploadResult {
                        op_id,
                        state: "failed".into(),
                        receipt: None,
                        error: Some(error_detail("invalid_item", message)),
                    },
                );
                continue;
            }
        };
        match stage_batch_item(&state, item, field).await {
            Ok(receipt) => {
                results.insert(
                    op_id.clone(),
                    BatchUploadResult {
                        op_id,
                        state: "staged".into(),
                        receipt: Some(receipt),
                        error: None,
                    },
                );
            }
            Err(error) => {
                results.insert(
                    op_id.clone(),
                    BatchUploadResult {
                        op_id,
                        state: "failed".into(),
                        receipt: None,
                        error: Some(error_detail(error.code, error.message)),
                    },
                );
            }
        }
    }
    for (op_id, item) in entries {
        let message = match item {
            Ok(_) => "file part is missing".to_owned(),
            Err(message) => message,
        };
        results.entry(op_id.clone()).or_insert(BatchUploadResult {
            op_id,
            state: "failed".into(),
            receipt: None,
            error: Some(error_detail("missing_file_part", message)),
        });
    }
    let ordered = ordered_ops
        .into_iter()
        .filter_map(|op_id| results.remove(&op_id))
        .collect();
    Ok(Json(BatchUploadResponse { items: ordered }))
}

fn error_detail(
    code: &str,
    message: String,
) -> muying_transfer_common::protocol::TransferErrorDetail {
    muying_transfer_common::protocol::TransferErrorDetail {
        code: code.to_owned(),
        message,
    }
}

async fn drain_field(mut field: axum::extract::multipart::Field<'_>) -> Result<(), ApiError> {
    while field
        .chunk()
        .await
        .map_err(|_| ApiError::bad_request("invalid_multipart", "file part could not be read"))?
        .is_some()
    {}
    Ok(())
}

async fn stage_batch_item(
    state: &TransferState,
    item: muying_transfer_common::protocol::BatchUploadItem,
    mut field: axum::extract::multipart::Field<'_>,
) -> Result<StagedObjectReceipt, ApiError> {
    let claims = verify_new_ticket(state, &item.ticket)?;
    validate_upload_claims(&claims, &item.manifest)?;
    let row = state
        .store
        .ensure_upload(
            &claims.op_id,
            &claims.object_id,
            &claims.object_kind,
            &claims.user_id,
            claims.expected_version,
            item.manifest.size,
            &item.manifest.sha256,
            DEFAULT_CHUNK_SIZE,
            state.config.max_staging_bytes_per_user,
            state.config.max_receiving_uploads_per_user,
            state.config.max_staging_bytes_total,
            state.config.max_receiving_uploads_total,
        )
        .map_err(upload_store_error)?;
    let lock = upload_lock(state, &row.upload_id).await;
    let _guard = lock.lock().await;
    let row = state
        .store
        .upload(&row.upload_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "upload_not_found",
                "upload session was not found",
            )
        })?;
    validate_upload_row(&claims, &row)?;
    if let Some(receipt) = row.receipt {
        drain_field(field).await?;
        return Ok(receipt);
    }
    let path = staging_path(state, &row);
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).await.map_err(internal)?;
    }
    let mut file = OpenOptions::new()
        .create(true)
        .truncate(true)
        .write(true)
        .open(&path)
        .await
        .map_err(internal)?;
    let mut hasher = Sha256::new();
    let mut size = 0_u64;
    while let Some(chunk) = field
        .chunk()
        .await
        .map_err(|_| ApiError::bad_request("invalid_multipart", "file data could not be read"))?
    {
        size = size.saturating_add(chunk.len() as u64);
        if size > item.manifest.size || size > state.config.max_batch_item_bytes {
            drop(file);
            let _ = fs::remove_file(&path).await;
            return Err(ApiError::new(
                StatusCode::PAYLOAD_TOO_LARGE,
                "batch_item_too_large",
                "file part exceeds its declared size",
            ));
        }
        hasher.update(&chunk);
        file.write_all(&chunk).await.map_err(internal)?;
    }
    if size != item.manifest.size {
        drop(file);
        let _ = fs::remove_file(&path).await;
        return Err(ApiError::bad_request(
            "batch_size_mismatch",
            "file part length does not match its manifest",
        ));
    }
    let hash = hex::encode(hasher.finalize());
    if !hash.eq_ignore_ascii_case(&item.manifest.sha256) {
        drop(file);
        let _ = fs::remove_file(&path).await;
        return Err(ApiError::new(
            StatusCode::UNPROCESSABLE_ENTITY,
            "file_hash_mismatch",
            "file part SHA-256 does not match",
        ));
    }
    file.sync_all().await.map_err(internal)?;
    drop(file);
    publish_object(
        state,
        &row.object_id,
        &row.object_kind,
        &path,
        size,
        &hash,
    )
    .await?;
    let receipt = make_receipt(state, &row).await?;
    state
        .store
        .stage_receipt(&row.upload_id, &receipt)
        .map_err(internal)?;
    Ok(receipt)
}

async fn init_download(
    State(state): State<Arc<TransferState>>,
    Json(request): Json<DownloadInitRequest>,
) -> Result<Json<DownloadInitResponse>, ApiError> {
    let _slot = acquire_slot(&state.request_slots, "request").await?;
    let claims = verify_new_ticket(&state, &request.ticket)?;
    if claims.action != "download" || claims.direction != "download" {
        return Err(ApiError::unauthorized("ticket is not a download ticket"));
    }
    let object_path = object_path(&state, &claims.object_id, &claims.object_kind)?;
    let metadata = fs::metadata(&object_path).await.map_err(|_| {
        ApiError::new(
            StatusCode::NOT_FOUND,
            "object_not_found",
            "authorized object is not available",
        )
    })?;
    let size = claims.size.unwrap_or(metadata.len());
    if metadata.len() != size {
        return Err(ApiError::conflict(
            "object_size_mismatch",
            "stored object length differs from the signed ticket",
        ));
    }
    let sha256 = claims.sha256.clone().unwrap_or_default();
    let row = DownloadRow {
        download_id: Uuid::new_v4().to_string(),
        op_id: claims.op_id.clone(),
        object_id: claims.object_id.clone(),
        object_kind: claims.object_kind.clone(),
        user_id: claims.user_id,
        size,
        sha256: sha256.clone(),
        exp: claims.exp,
    };
    state.store.add_download(&row).map_err(internal)?;
    Ok(Json(DownloadInitResponse {
        download_id: row.download_id,
        size,
        sha256,
        chunk_size: DEFAULT_CHUNK_SIZE,
    }))
}

#[derive(Deserialize)]
struct DownloadInitRequest {
    ticket: String,
}

#[derive(Serialize)]
struct DownloadInitResponse {
    download_id: String,
    size: u64,
    sha256: String,
    chunk_size: u64,
}

async fn download_head(
    State(state): State<Arc<TransferState>>,
    AxumPath(download_id): AxumPath<String>,
    headers: HeaderMap,
) -> Result<Response, ApiError> {
    let row = authorized_download(&state, &download_id, &headers)?;
    let mut response = Response::builder()
        .status(StatusCode::OK)
        .header(header::ACCEPT_RANGES, "bytes")
        .header(header::CONTENT_LENGTH, row.size.to_string());
    if !row.sha256.is_empty() {
        response = response.header(header::ETAG, format!("\"{}\"", row.sha256));
    }
    response.body(Body::empty()).map_err(internal)
}

async fn download_object(
    State(state): State<Arc<TransferState>>,
    AxumPath(download_id): AxumPath<String>,
    headers: HeaderMap,
) -> Result<Response, ApiError> {
    let _slot = acquire_slot(&state.request_slots, "request").await?;
    let download_slot = acquire_slot(&state.download_slots, "download").await?;
    let row = authorized_download(&state, &download_id, &headers)?;
    let path = object_path(&state, &row.object_id, &row.object_kind)?;
    let (start, end, partial) = parse_download_range(&headers, row.size)?;
    let length = if row.size == 0 { 0 } else { end - start + 1 };
    let mut file = File::open(path).await.map_err(internal)?;
    file.seek(SeekFrom::Start(start)).await.map_err(internal)?;
    // Hold the download permit until the response body finishes or the client disconnects.
    let stream = ReaderStream::new(file.take(length)).map(move |chunk| {
        let _permit = &download_slot;
        chunk
    });
    let mut response = Response::builder()
        .status(if partial {
            StatusCode::PARTIAL_CONTENT
        } else {
            StatusCode::OK
        })
        .header(header::ACCEPT_RANGES, "bytes")
        .header(header::CONTENT_LENGTH, length.to_string());
    if !row.sha256.is_empty() {
        response = response.header(header::ETAG, format!("\"{}\"", row.sha256));
    }
    if partial && row.size > 0 {
        response = response.header(
            header::CONTENT_RANGE,
            format!("bytes {start}-{end}/{}", row.size),
        );
    }
    response.body(Body::from_stream(stream)).map_err(internal)
}

fn authorized_download(
    state: &TransferState,
    download_id: &str,
    headers: &HeaderMap,
) -> Result<DownloadRow, ApiError> {
    let row = state
        .store
        .download(download_id)
        .map_err(internal)?
        .ok_or_else(|| {
            ApiError::new(
                StatusCode::NOT_FOUND,
                "download_not_found",
                "download session was not found",
            )
        })?;
    let token = header_str(headers, "x-transfer-ticket")?;
    // Downloads can obtain a new control-plane ticket while retaining their local partial
    // file, so server-side range access never needs to extend an expired ticket.
    let claims = verify_ticket(token, &state.config.ticket_secret, now_seconds())
        .map_err(|_| ApiError::unauthorized("ticket is invalid or expired"))?;
    if claims.action != "download"
        || claims.direction != "download"
        || claims.op_id != row.op_id
        || claims.object_id != row.object_id
        || claims.object_kind != row.object_kind
        || claims.user_id != row.user_id
        || row.exp != claims.exp
        || claims.size.is_some_and(|size| size != row.size)
        || claims
            .sha256
            .as_ref()
            .is_some_and(|hash| !hash.eq_ignore_ascii_case(&row.sha256))
    {
        return Err(ApiError::unauthorized(
            "ticket does not authorize this download",
        ));
    }
    Ok(row)
}

fn validate_upload_row(claims: &TicketClaims, row: &UploadRow) -> Result<(), ApiError> {
    validate_upload_identity(claims, row)?;
    if row.state == "aborted" {
        return Err(ApiError::new(
            StatusCode::GONE,
            "upload_aborted",
            "the upload was aborted and cannot be resumed",
        ));
    }
    if row.state == "expired" {
        return Err(ApiError::new(
            StatusCode::GONE,
            "upload_expired",
            "the incomplete upload expired and must be restarted",
        ));
    }
    Ok(())
}

fn validate_upload_identity(claims: &TicketClaims, row: &UploadRow) -> Result<(), ApiError> {
    if claims.action != "upload"
        || claims.direction != "upload"
        || claims.op_id != row.op_id
        || claims.object_id != row.object_id
        || claims.object_kind != row.object_kind
        || claims.user_id != row.user_id
        || claims.expected_version != row.expected_version
        || claims.size.is_some_and(|size| size != row.size)
        || claims
            .sha256
            .as_ref()
            .is_some_and(|hash| !hash.eq_ignore_ascii_case(&row.sha256))
    {
        return Err(ApiError::unauthorized(
            "ticket does not authorize this upload session",
        ));
    }
    Ok(())
}

async fn ensure_stage_file(state: &TransferState, row: &UploadRow) -> Result<(), ApiError> {
    if row.state == "staged" {
        return Ok(());
    }
    let path = staging_path(state, row);
    match fs::metadata(&path).await {
        Ok(_) => return Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(internal(error)),
    }
    let published = object_path(state, &row.object_id, &row.object_kind)?;
    match fs::metadata(&published).await {
        Ok(metadata) => {
            if metadata.len() != row.size
                || !hash_file(state, published)
                    .await
                    .map_err(internal)?
                    .eq_ignore_ascii_case(&row.sha256)
            {
                return Err(ApiError::conflict(
                    "object_id_collision",
                    "published object differs from the upload manifest",
                ));
            }
            return Ok(());
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(internal(error)),
    }
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).await.map_err(internal)?;
    }
    OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(path)
        .await
        .map_err(internal)?;
    Ok(())
}

fn missing_ranges(state: &TransferState, row: &UploadRow) -> Result<Vec<ByteRange>, ApiError> {
    if row.state == "staged" || row.size == 0 {
        return Ok(Vec::new());
    }
    let received = state.store.received_ranges(row).map_err(internal)?;
    let received_starts: HashSet<u64> = received.into_iter().map(|(start, _)| start).collect();
    let count = row.size.div_ceil(row.chunk_size);
    let mut ranges = Vec::new();
    let mut index = 0_u64;
    while index < count {
        if received_starts.contains(&(index * row.chunk_size)) {
            index += 1;
            continue;
        }
        let first = index;
        while index < count && !received_starts.contains(&(index * row.chunk_size)) {
            index += 1;
        }
        ranges.push(ByteRange {
            start: first * row.chunk_size,
            end: (index * row.chunk_size).min(row.size) - 1,
        });
    }
    Ok(ranges)
}

fn parse_content_range(value: &str) -> Result<(u64, u64, u64), ApiError> {
    let rest = value.strip_prefix("bytes ").ok_or_else(|| {
        ApiError::bad_request(
            "invalid_content_range",
            "Content-Range must start with bytes",
        )
    })?;
    let (positions, total) = rest.split_once('/').ok_or_else(|| {
        ApiError::bad_request("invalid_content_range", "Content-Range is malformed")
    })?;
    let (start, end) = positions.split_once('-').ok_or_else(|| {
        ApiError::bad_request("invalid_content_range", "Content-Range is malformed")
    })?;
    let parse = |value: &str| {
        value.parse::<u64>().map_err(|_| {
            ApiError::bad_request(
                "invalid_content_range",
                "Content-Range contains a non-numeric offset",
            )
        })
    };
    Ok((parse(start)?, parse(end)?, parse(total)?))
}

fn parse_download_range(headers: &HeaderMap, size: u64) -> Result<(u64, u64, bool), ApiError> {
    if size == 0 {
        return Ok((0, 0, false));
    }
    let Some(value) = headers.get(header::RANGE) else {
        return Ok((0, size - 1, false));
    };
    let text = value
        .to_str()
        .map_err(|_| ApiError::bad_request("invalid_range", "Range header is invalid"))?;
    let range = text.strip_prefix("bytes=").ok_or_else(|| {
        ApiError::new(
            StatusCode::RANGE_NOT_SATISFIABLE,
            "invalid_range",
            "only byte ranges are supported",
        )
    })?;
    if range.contains(',') {
        return Err(ApiError::new(
            StatusCode::RANGE_NOT_SATISFIABLE,
            "invalid_range",
            "multiple ranges are not supported",
        ));
    }
    let (start_text, end_text) = range.split_once('-').ok_or_else(|| {
        ApiError::new(
            StatusCode::RANGE_NOT_SATISFIABLE,
            "invalid_range",
            "Range header is malformed",
        )
    })?;
    let start = start_text.parse::<u64>().map_err(|_| {
        ApiError::new(
            StatusCode::RANGE_NOT_SATISFIABLE,
            "invalid_range",
            "Range start is invalid",
        )
    })?;
    let end = if end_text.is_empty() {
        size - 1
    } else {
        end_text.parse::<u64>().map_err(|_| {
            ApiError::new(
                StatusCode::RANGE_NOT_SATISFIABLE,
                "invalid_range",
                "Range end is invalid",
            )
        })?
    };
    if start >= size || end < start {
        return Err(ApiError::new(
            StatusCode::RANGE_NOT_SATISFIABLE,
            "range_not_satisfiable",
            "requested byte range is outside the object",
        ));
    }
    Ok((start, end.min(size - 1), true))
}

fn header_str<'a>(headers: &'a HeaderMap, name: &'static str) -> Result<&'a str, ApiError> {
    headers
        .get(name)
        .and_then(|value| value.to_str().ok())
        .ok_or_else(|| {
            ApiError::bad_request(
                "missing_header",
                format!("missing or invalid {name} header"),
            )
        })
}

fn staging_path(state: &TransferState, row: &UploadRow) -> PathBuf {
    state
        .config
        .staging_root
        .join(&row.op_id)
        .join(format!("{}.partial", row.object_id))
}

async fn remove_staging_file(state: &TransferState, row: &UploadRow) -> Result<(), ApiError> {
    let path = staging_path(state, row);
    match fs::remove_file(&path).await {
        Ok(()) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(internal(error)),
    }
    if let Some(parent) = path.parent() {
        match fs::remove_dir(parent).await {
            Ok(()) => {}
            Err(error)
                if matches!(
                    error.kind(),
                    std::io::ErrorKind::NotFound | std::io::ErrorKind::DirectoryNotEmpty
                ) => {}
            Err(error) => return Err(internal(error)),
        }
    }
    state
        .store
        .mark_upload_cleanup_complete(&row.upload_id)
        .map_err(internal)
}

fn object_path(
    state: &TransferState,
    object_id: &str,
    object_kind: &str,
) -> Result<PathBuf, ApiError> {
    if !matches!(object_kind, "image" | "video" | "other" | "thumbnail") {
        return Err(ApiError::bad_request(
            "invalid_object_kind",
            "object_kind is not supported",
        ));
    }
    let parsed = Uuid::parse_str(object_id)
        .map_err(|_| ApiError::bad_request("invalid_object_id", "object_id must be a UUID"))?;
    let compact = parsed.simple().to_string();
    // objects_root is the `_objects` root; kind and two-character shard are path components.
    Ok(state
        .config
        .objects_root
        .join(object_kind)
        .join(&compact[..2])
        .join(compact))
}

async fn publish_object(
    state: &TransferState,
    object_id: &str,
    object_kind: &str,
    staged: &Path,
    size: u64,
    sha256: &str,
) -> Result<(), ApiError> {
    let target = object_path(state, object_id, object_kind)?;
    if let Some(parent) = target.parent() {
        fs::create_dir_all(parent).await.map_err(internal)?;
    }
    if fs::metadata(&target).await.is_ok() {
        let target_metadata = fs::metadata(&target).await.map_err(internal)?;
        if target_metadata.len() != size
            || !hash_file(state, target.clone())
                .await
                .map_err(internal)?
                .eq_ignore_ascii_case(sha256)
        {
            return Err(ApiError::conflict(
                "object_id_collision",
                "immutable object ID already contains different bytes",
            ));
        }
        let _ = fs::remove_file(staged).await;
        return Ok(());
    }
    fs::rename(staged, &target).await.map_err(internal)?;
    if let Some(parent) = target.parent() {
        let parent = parent.to_path_buf();
        tokio::task::spawn_blocking(move || std::fs::File::open(parent)?.sync_all())
            .await
            .map_err(internal)?
            .map_err(internal)?;
    }
    Ok(())
}

async fn hash_file(state: &TransferState, path: PathBuf) -> std::io::Result<String> {
    let _hash_slot = state
        .hash_slots
        .clone()
        .acquire_owned()
        .await
        .map_err(|error| std::io::Error::other(error.to_string()))?;
    tokio::task::spawn_blocking(move || {
        use std::io::Read;
        let mut file = std::fs::File::open(path)?;
        let mut hasher = Sha256::new();
        let mut buffer = vec![0_u8; 1024 * 1024];
        loop {
            let read = file.read(&mut buffer)?;
            if read == 0 {
                break;
            }
            hasher.update(&buffer[..read]);
        }
        Ok(hex::encode(hasher.finalize()))
    })
    .await
    .map_err(std::io::Error::other)?
}

async fn make_receipt(
    state: &TransferState,
    row: &UploadRow,
) -> Result<StagedObjectReceipt, ApiError> {
    let receipt = StagedObjectReceipt {
        v: 1,
        op_id: row.op_id.clone(),
        object_id: row.object_id.clone(),
        size: row.size,
        sha256: row.sha256.clone(),
        staged_at: Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true),
        nonce: Uuid::new_v4().to_string(),
        mac: String::new(),
    };
    sign_receipt(receipt, &state.config.ticket_secret).map_err(internal)
}

async fn upload_lock(state: &TransferState, upload_id: &str) -> Arc<Mutex<()>> {
    let mut locks = state.object_locks.lock().await;
    locks.retain(|_, lock| lock.strong_count() > 0);
    if let Some(lock) = locks.get(upload_id).and_then(Weak::upgrade) {
        return lock;
    }
    let lock = Arc::new(Mutex::new(()));
    locks.insert(upload_id.to_owned(), Arc::downgrade(&lock));
    lock
}

fn validate_object_object_id(object_id: &str) -> Result<(), ApiError> {
    Uuid::parse_str(object_id)
        .map(|_| ())
        .map_err(|_| ApiError::bad_request("invalid_object_id", "object_id must be a UUID"))
}

use serde::{Deserialize, Serialize};

pub const PROTOCOL_VERSION: u32 = 1;
pub const DEFAULT_CHUNK_SIZE: u64 = 4 * 1024 * 1024;
pub const MAX_CHUNK_SIZE: u64 = 64 * 1024 * 1024;
pub const MAX_BATCH_ITEMS: usize = 512;

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct UploadManifest {
    pub op_id: String,
    pub object_id: String,
    pub object_kind: String,
    pub expected_version: i64,
    pub size: u64,
    pub sha256: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub media_type: Option<String>,
    /// Untrusted display/audit metadata only; never use this to derive a NAS path.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub logical_path: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct UploadInitRequest {
    pub ticket: String,
    pub manifest: UploadManifest,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct BatchUploadItem {
    pub ticket: String,
    pub manifest: UploadManifest,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct BatchUploadManifest {
    pub items: Vec<BatchUploadItem>,
    #[serde(default)]
    pub directories: Vec<LogicalDirectory>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct BatchUploadResult {
    pub op_id: String,
    pub state: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub receipt: Option<StagedObjectReceipt>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<TransferErrorDetail>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct BatchUploadResponse {
    pub items: Vec<BatchUploadResult>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct LogicalDirectory {
    pub scope_id: String,
    #[serde(default)]
    pub project_id: Option<String>,
    pub logical_path: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct ByteRange {
    pub start: u64,
    /// Inclusive end offset, matching HTTP Content-Range.
    pub end: u64,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct UploadInitResponse {
    pub upload_id: String,
    pub chunk_size: u64,
    pub received_bytes: u64,
    pub missing_ranges: Vec<ByteRange>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct StagedObjectReceipt {
    pub v: u32,
    pub op_id: String,
    pub object_id: String,
    pub size: u64,
    pub sha256: String,
    pub staged_at: String,
    pub nonce: String,
    pub mac: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct UploadCommitResponse {
    pub op_id: String,
    pub object_id: String,
    pub size: u64,
    pub sha256: String,
    pub staged_at: String,
    pub receipt: StagedObjectReceipt,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct TransferErrorBody {
    pub error: TransferErrorDetail,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct TransferErrorDetail {
    pub code: String,
    pub message: String,
}

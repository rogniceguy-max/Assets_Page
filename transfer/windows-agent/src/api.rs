use anyhow::{Context, Result, anyhow};
use reqwest::{Client, StatusCode};
use serde::Deserialize;
use serde_json::json;

use crate::{AgentConfig, queue::TaskRecord};
use muying_transfer_common::protocol::StagedObjectReceipt;

#[derive(Clone, Debug, Deserialize)]
pub struct BeginUploadResponse {
    pub operation_id: String,
    pub state: String,
    pub file_id: Option<String>,
    pub revision_id: String,
    pub object_id: String,
    pub object_kind: String,
    pub expected_version: i64,
    pub ticket: String,
    pub transfer_url: String,
    pub expires_at: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct CommitUploadResponse {
    pub operation_id: String,
    pub state: String,
    pub file_id: String,
    pub revision_id: String,
    pub version: i64,
    pub change_sequence: i64,
}

#[derive(Clone, Debug, Deserialize)]
pub struct OperationMutationResponse {
    pub operation_id: String,
    pub state: String,
    pub file_id: Option<String>,
    pub version: Option<i64>,
    pub change_sequence: Option<i64>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct DirectoryMutationResponse {
    pub operation_id: String,
    pub state: String,
    pub directory_id: String,
    pub version: Option<i64>,
    pub change_sequence: Option<i64>,
    pub logical_path: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct BatchBeginResponse {
    pub items: Vec<BeginUploadResponse>,
    #[serde(default)]
    pub errors: Vec<BatchBeginError>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct BatchBeginError {
    pub operation_id: String,
    pub code: String,
    pub message: String,
}

#[derive(Clone, Debug, Deserialize)]
pub struct BeginDownloadResponse {
    pub ticket: String,
    pub object_id: String,
    pub object_kind: String,
    pub size: u64,
    pub sha256: String,
    pub revision_id: String,
    pub transfer_url: String,
}

#[derive(Clone, Debug, Deserialize)]
pub struct FileCatalogItem {
    pub is_directory: bool,
    pub directory_id: Option<String>,
    pub file_id: Option<String>,
    pub scope_id: String,
    pub project_id: Option<String>,
    pub logical_path: String,
    pub name: String,
    pub media_kind: Option<String>,
    pub media_type: Option<String>,
    pub size: u64,
    pub sha256: Option<String>,
    pub current_revision_id: Option<String>,
    pub version: Option<i64>,
    pub deleted_at: Option<String>,
    pub updated_at: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct FileListResponse {
    pub items: Vec<FileCatalogItem>,
    pub next_cursor: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ChangeItem {
    pub sequence: i64,
    pub entity_kind: String,
    pub entity_id: String,
    pub action: String,
    pub file_id: Option<String>,
    pub scope_id: Option<String>,
    pub project_id: Option<String>,
    pub logical_path: Option<String>,
    pub previous_scope_id: Option<String>,
    pub previous_project_id: Option<String>,
    pub previous_logical_path: Option<String>,
    pub revision_id: Option<String>,
    pub version: Option<i64>,
    pub source: Option<String>,
    pub actor_id: Option<String>,
    pub created_at: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ChangeFeedResponse {
    pub items: Vec<ChangeItem>,
    pub next_sequence: i64,
    pub has_more: bool,
    pub reset_required: bool,
}

#[derive(Clone, Debug, Deserialize)]
pub struct SyncAckResponse {
    pub client_id: String,
    pub sequence: i64,
}

#[derive(Clone, Debug, Deserialize)]
pub struct AbortUploadResponse {
    pub operation_id: String,
    pub state: String,
    pub file_id: Option<String>,
    pub placeholder_retired: Option<bool>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct TransferAbortResponse {
    pub upload_id: String,
    pub state: String,
}

#[derive(Deserialize)]
struct ApiErrorEnvelope {
    error: ApiErrorDetail,
}

#[derive(Deserialize)]
struct ApiErrorDetail {
    code: String,
    message: String,
}

pub async fn list_files(
    client: &Client,
    config: &AgentConfig,
    scope_id: &str,
    project_id: Option<String>,
    parent_path: &str,
    cursor: Option<&str>,
) -> Result<FileListResponse> {
    let mut request = client
        .get(format!(
            "{}/api/v2/files",
            config.api_base_url.trim_end_matches('/')
        ))
        .bearer_auth(&config.bearer_token)
        .query(&[
            ("scope_id", scope_id.to_owned()),
            ("parent_path", parent_path.to_owned()),
            ("media_kind", "all".to_owned()),
            ("include_deleted", "false".to_owned()),
            ("limit", "100".to_owned()),
        ]);
    if let Some(project_id) = project_id {
        request = request.query(&[("project_id", project_id)]);
    }
    if let Some(cursor) = cursor {
        request = request.query(&[("cursor", cursor)]);
    }
    let response = request.send().await.context("GET logical file catalog")?;
    parse_response(response).await
}

pub async fn sync_changes(
    client: &Client,
    config: &AgentConfig,
    after_sequence: i64,
) -> Result<ChangeFeedResponse> {
    let after = after_sequence.to_string();
    let response = client
        .get(format!(
            "{}/api/v2/sync/changes",
            config.api_base_url.trim_end_matches('/')
        ))
        .bearer_auth(&config.bearer_token)
        .query(&[
            ("client_id", config.client_id.as_str()),
            ("after_sequence", after.as_str()),
            ("limit", "500"),
        ])
        .send()
        .await
        .context("GET file change feed")?;
    parse_response(response).await
}

pub async fn acknowledge_changes(
    client: &Client,
    config: &AgentConfig,
    sequence: i64,
) -> Result<SyncAckResponse> {
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/sync/ack",
            config.api_base_url.trim_end_matches('/')
        ),
        &json!({"client_id":config.client_id,"sequence":sequence}),
    )
    .await
}

pub async fn begin_batch_uploads(
    client: &Client,
    config: &AgentConfig,
    tasks: &[TaskRecord],
    sizes_hashes: &[(u64, String)],
    directories: serde_json::Value,
) -> Result<BatchBeginResponse> {
    if tasks.len() != sizes_hashes.len() {
        anyhow::bail!("batch task/hash count mismatch");
    }
    let items: Vec<_> = tasks
        .iter()
        .zip(sizes_hashes.iter())
        .map(|(task, (size, sha256))| {
            json!({
                "operation_id":task.snapshot.operation_id,
                "action":"upload",
                "scope_id":task.scope_id,
                "project_id":task.project_id,
                "logical_path":task.snapshot.logical_path,
                "file_id":task.file_id,
                "expected_version":task.expected_version.unwrap_or(0),
                "size":size,
                "sha256":sha256,
                "media_type":task.media_type,
            })
        })
        .collect();
    let source = tasks
        .first()
        .map(|task| task.source.as_str())
        .unwrap_or("wpf");
    let body = json!({"client_id":config.client_id,"source":source,"directories":directories,"items":items});
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/file-operations/batch",
            config.api_base_url.trim_end_matches('/')
        ),
        &body,
    )
    .await
}

pub async fn create_directory(
    client: &Client,
    config: &AgentConfig,
    operation_id: &str,
    scope_id: &str,
    project_id: Option<&str>,
    logical_path: &str,
) -> Result<DirectoryMutationResponse> {
    let mut body =
        json!({"operation_id":operation_id,"scope_id":scope_id,"logical_path":logical_path});
    if let Some(project_id) = project_id {
        body["project_id"] = json!(project_id);
    }
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/directories",
            config.api_base_url.trim_end_matches('/')
        ),
        &body,
    )
    .await
}

pub async fn move_directory(
    client: &Client,
    config: &AgentConfig,
    directory_id: &str,
    operation_id: &str,
    expected_version: i64,
    target_scope_id: &str,
    target_project_id: Option<&str>,
    target_logical_path: &str,
) -> Result<DirectoryMutationResponse> {
    let mut body = json!({"operation_id":operation_id,"client_id":config.client_id,"action":"move",
        "expected_version":expected_version,"target_scope_id":target_scope_id,"target_logical_path":target_logical_path});
    if let Some(project_id) = target_project_id {
        body["target_project_id"] = json!(project_id);
    }
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/directories/{}/operations",
            config.api_base_url.trim_end_matches('/'),
            urlencoding::encode(directory_id)
        ),
        &body,
    )
    .await
}

pub async fn delete_directory(
    client: &Client,
    config: &AgentConfig,
    directory_id: &str,
    operation_id: &str,
    expected_version: i64,
) -> Result<DirectoryMutationResponse> {
    post_json(client,config,&format!("{}/api/v2/directories/{}/operations",config.api_base_url.trim_end_matches('/'),urlencoding::encode(directory_id)),
        &json!({"operation_id":operation_id,"client_id":config.client_id,"action":"delete","expected_version":expected_version})).await
}

pub async fn restore_directory(
    client: &Client,
    config: &AgentConfig,
    directory_id: &str,
    operation_id: &str,
    expected_version: i64,
) -> Result<DirectoryMutationResponse> {
    post_json(client,config,&format!("{}/api/v2/directories/{}/operations",config.api_base_url.trim_end_matches('/'),urlencoding::encode(directory_id)),
        &json!({"operation_id":operation_id,"client_id":config.client_id,"action":"restore","expected_version":expected_version})).await
}

pub async fn begin_upload(
    client: &Client,
    config: &AgentConfig,
    task: &TaskRecord,
    size: u64,
    sha256: &str,
) -> Result<BeginUploadResponse> {
    let body = json!({
        "operation_id": task.snapshot.operation_id,
        "action": "upload",
        "client_id": config.client_id,
        "scope_id": task.scope_id,
        "project_id": task.project_id,
        "logical_path": task.snapshot.logical_path,
        "file_id": task.file_id,
        "expected_version": task.expected_version.unwrap_or(0),
        "size": size,
        "sha256": sha256,
        "media_type": task.media_type,
        "source": task.source,
    });
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/file-operations",
            config.api_base_url.trim_end_matches('/')
        ),
        &body,
    )
    .await
}

pub async fn begin_download(
    client: &Client,
    config: &AgentConfig,
    file_id: &str,
    revision_id: Option<&str>,
) -> Result<BeginDownloadResponse> {
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/files/{}/download",
            config.api_base_url.trim_end_matches('/'),
            urlencoding::encode(file_id)
        ),
        &json!({
            "revision_id": revision_id,
            "client_id": config.client_id,
        }),
    )
    .await
}

pub async fn commit_upload(
    client: &Client,
    config: &AgentConfig,
    operation_id: &str,
    receipt: &StagedObjectReceipt,
) -> Result<CommitUploadResponse> {
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/file-operations/{}/commit",
            config.api_base_url.trim_end_matches('/'),
            urlencoding::encode(operation_id)
        ),
        &json!({ "receipt": receipt }),
    )
    .await
}

pub async fn abort_upload(
    client: &Client,
    config: &AgentConfig,
    operation_id: &str,
    expected_state: &str,
) -> Result<AbortUploadResponse> {
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/file-operations/{}/abort",
            config.api_base_url.trim_end_matches('/'),
            urlencoding::encode(operation_id)
        ),
        &json!({"client_id":config.client_id,"expected_state":expected_state}),
    )
    .await
}

pub async fn abort_transfer_upload(
    client: &Client,
    transfer_url: &str,
    operation_id: &str,
    ticket: &str,
) -> Result<TransferAbortResponse> {
    let base = transfer_url.trim_end_matches('/');
    let url = if base.ends_with("/uploads") {
        format!("{base}/{}/abort", urlencoding::encode(operation_id))
    } else {
        format!("{base}/uploads/{}/abort", urlencoding::encode(operation_id))
    };
    let response = client
        .post(&url)
        .header("X-Transfer-Ticket", ticket)
        .send()
        .await
        .with_context(|| format!("POST transfer abort {url}"))?;
    let status = response.status();
    let bytes = response.bytes().await.context("read transfer abort response")?;
    if !status.is_success() {
        let message = serde_json::from_slice::<ApiErrorEnvelope>(&bytes)
            .map(|envelope| envelope.error.message)
            .unwrap_or_else(|_| format!("transfer service returned HTTP {}", status.as_u16()));
        if status == StatusCode::CONFLICT {
            anyhow::bail!("conflict: {message}");
        }
        anyhow::bail!("transfer_http_{}: {message}", status.as_u16());
    }
    serde_json::from_slice(&bytes).context("decode transfer abort response")
}

pub async fn delete_file(
    client: &Client,
    config: &AgentConfig,
    task: &TaskRecord,
) -> Result<OperationMutationResponse> {
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/file-operations",
            config.api_base_url.trim_end_matches('/')
        ),
        &json!({
            "operation_id":task.snapshot.operation_id,
            "action":"delete",
            "client_id":config.client_id,
            "scope_id":task.scope_id,
            "project_id":task.project_id,
            "logical_path":task.snapshot.logical_path,
            "file_id":task.file_id,
            "expected_version":task.expected_version.unwrap_or(0),
            "source":task.source,
        }),
    )
    .await
}

pub async fn move_file(
    client: &Client,
    config: &AgentConfig,
    task: &TaskRecord,
    new_logical_path: &str,
    target_scope_id: &str,
    target_project_id: Option<&str>,
) -> Result<OperationMutationResponse> {
    let body = json!({
        "operation_id":task.snapshot.operation_id,
        "action":"move",
        "client_id":config.client_id,
        "scope_id":task.scope_id,
        "project_id":task.project_id,
        "previous_logical_path":task.snapshot.logical_path,
        "target_scope_id":target_scope_id,
        "target_project_id":target_project_id,
        "target_logical_path":new_logical_path,
        "file_id":task.file_id,
        "expected_version":task.expected_version.unwrap_or(0),
        "source":task.source,
    });
    post_json(
        client,
        config,
        &format!(
            "{}/api/v2/file-operations",
            config.api_base_url.trim_end_matches('/')
        ),
        &body,
    )
    .await
}

async fn post_json<T: for<'de> Deserialize<'de>>(
    client: &Client,
    config: &AgentConfig,
    url: &str,
    body: &serde_json::Value,
) -> Result<T> {
    let response = client
        .post(url)
        .bearer_auth(&config.bearer_token)
        .json(body)
        .send()
        .await
        .with_context(|| format!("POST control API {url}"))?;
    parse_response(response).await
}

async fn parse_response<T: for<'de> Deserialize<'de>>(response: reqwest::Response) -> Result<T> {
    let status = response.status();
    let bytes = response
        .bytes()
        .await
        .context("read control API response")?;
    if !status.is_success() {
        if let Ok(envelope) = serde_json::from_slice::<ApiErrorEnvelope>(&bytes) {
            if status == StatusCode::CONFLICT {
                return Err(anyhow!(
                    "conflict: {} ({})",
                    envelope.error.message,
                    envelope.error.code
                ));
            }
            return Err(anyhow!(
                "control_api_http_{}: {} ({})",
                status.as_u16(),
                envelope.error.message,
                envelope.error.code
            ));
        }
        return Err(anyhow!("control_api_http_{}", status.as_u16()));
    }
    serde_json::from_slice(&bytes).context("decode control API JSON")
}

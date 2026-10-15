using System.Text.Json.Serialization;

namespace MuYingDrive.NativeWpf.Models;

public sealed class LogicalFileListResponse
{
    [JsonPropertyName("items")] public List<LogicalFileEntry> Items { get; init; } = [];
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; init; }
}

public sealed class LogicalFileEntry
{
    [JsonPropertyName("file_id")] public string? FileId { get; init; }
    [JsonPropertyName("directory_id")] public string? DirectoryId { get; init; }
    [JsonPropertyName("scope_id")] public string ScopeId { get; init; } = string.Empty;
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    [JsonPropertyName("owner_user_id")] public string? OwnerUserId { get; init; }
    [JsonPropertyName("logical_path")] public string LogicalPath { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("media_kind")] public string? MediaKind { get; init; }
    [JsonPropertyName("media_type")] public string? MediaType { get; init; }
    [JsonPropertyName("size")] public long? Size { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
    [JsonPropertyName("current_revision_id")] public string? CurrentRevisionId { get; init; }
    [JsonPropertyName("version")] public long Version { get; init; }
    [JsonPropertyName("deleted_at")] public DateTimeOffset? DeletedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; init; }
    [JsonPropertyName("is_directory")] public bool IsDirectory { get; init; }
}

public sealed class FileRevisionListResponse
{
    [JsonPropertyName("file_id")] public string FileId { get; init; } = string.Empty;
    [JsonPropertyName("current_revision_id")] public string? CurrentRevisionId { get; init; }
    [JsonPropertyName("items")] public List<FileRevisionEntry> Items { get; init; } = [];
}

public sealed class FileRevisionEntry
{
    [JsonPropertyName("revision_id")] public string RevisionId { get; init; } = string.Empty;
    [JsonPropertyName("revision_no")] public long RevisionNumber { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
    [JsonPropertyName("media_type")] public string? MediaType { get; init; }
    [JsonPropertyName("actor_id")] public string? ActorId { get; init; }
    [JsonPropertyName("actor_name")] public string? ActorName { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
    [JsonPropertyName("operation_id")] public string OperationId { get; init; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
}

public sealed class FileOperationRequest
{
    [JsonPropertyName("operation_id")] public string OperationId { get; init; } = string.Empty;
    [JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; init; } = "wpf";
    [JsonPropertyName("scope_id")] public string? ScopeId { get; init; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    [JsonPropertyName("logical_path")] public string? LogicalPath { get; init; }
    [JsonPropertyName("file_id")] public string? FileId { get; init; }
    [JsonPropertyName("expected_version")] public long? ExpectedVersion { get; init; }
    [JsonPropertyName("size")] public long? Size { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
    [JsonPropertyName("media_type")] public string? MediaType { get; init; }
    [JsonPropertyName("target_scope_id")] public string? TargetScopeId { get; init; }
    [JsonPropertyName("target_project_id")] public string? TargetProjectId { get; init; }
    [JsonPropertyName("target_logical_path")] public string? TargetLogicalPath { get; init; }
}

public sealed class FileOperationBeginResponse
{
    [JsonPropertyName("operation_id")] public string OperationId { get; init; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
    [JsonPropertyName("file_id")] public string? FileId { get; init; }
    [JsonPropertyName("revision_id")] public string? RevisionId { get; init; }
    [JsonPropertyName("object_id")] public string? ObjectId { get; init; }
    [JsonPropertyName("expected_version")] public long? ExpectedVersion { get; init; }
    [JsonPropertyName("ticket")] public string? Ticket { get; init; }
    [JsonPropertyName("transfer_url")] public string? TransferUrl { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("version")] public long? Version { get; init; }
    [JsonPropertyName("change_sequence")] public long? ChangeSequence { get; init; }
}

public sealed class FileOperationsBatchRequest
{
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; init; } = "wpf";
    [JsonPropertyName("items")] public List<FileOperationRequest> Items { get; init; } = [];
    [JsonPropertyName("directories")] public List<FileDirectoryBatchEntry> Directories { get; init; } = [];
}

public sealed class FileOperationsBatchResponse
{
    [JsonPropertyName("items")] public List<FileOperationBeginResponse> Items { get; init; } = [];
    [JsonPropertyName("errors")] public List<FileOperationError> Errors { get; init; } = [];
}

public sealed class FileOperationError
{
    [JsonPropertyName("operation_id")] public string OperationId { get; init; } = string.Empty;
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}

public sealed class FileOperationCommitRequest
{
    [JsonPropertyName("receipt")] public FileTransferReceipt Receipt { get; init; } = new();
}

public sealed class FileTransferReceipt
{
    [JsonPropertyName("v")] public int Version { get; init; }
    [JsonPropertyName("op_id")] public string OperationId { get; init; } = string.Empty;
    [JsonPropertyName("object_id")] public string ObjectId { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
    [JsonPropertyName("staged_at")] public string StagedAt { get; init; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; init; } = string.Empty;
    [JsonPropertyName("mac")] public string Mac { get; init; } = string.Empty;
}

public sealed class FileDownloadTicketRequest
{
    [JsonPropertyName("revision_id")] public string? RevisionId { get; init; }
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
}

public sealed class FileDownloadTicketResponse
{
    [JsonPropertyName("file_id")] public string FileId { get; init; } = string.Empty;
    [JsonPropertyName("revision_id")] public string RevisionId { get; init; } = string.Empty;
    [JsonPropertyName("object_kind")] public string? ObjectKind { get; init; }
    [JsonPropertyName("object_id")] public string ObjectId { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
    [JsonPropertyName("ticket")] public string Ticket { get; init; } = string.Empty;
    [JsonPropertyName("transfer_url")] public string TransferUrl { get; init; } = string.Empty;
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class FileChangesResponse
{
    [JsonPropertyName("items")] public List<FileChangeEntry> Items { get; init; } = [];
    [JsonPropertyName("next_sequence")] public long NextSequence { get; init; }
    [JsonPropertyName("has_more")] public bool HasMore { get; init; }
    [JsonPropertyName("reset_required")] public bool ResetRequired { get; init; }
}

public sealed class FileChangeEntry
{
    [JsonPropertyName("sequence")] public long Sequence { get; init; }
    [JsonPropertyName("entity_kind")] public string EntityKind { get; init; } = string.Empty;
    [JsonPropertyName("entity_id")] public string EntityId { get; init; } = string.Empty;
    [JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
    [JsonPropertyName("file_id")] public string? FileId { get; init; }
    [JsonPropertyName("scope_id")] public string? ScopeId { get; init; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    [JsonPropertyName("logical_path")] public string? LogicalPath { get; init; }
    [JsonPropertyName("previous_scope_id")] public string? PreviousScopeId { get; init; }
    [JsonPropertyName("previous_project_id")] public string? PreviousProjectId { get; init; }
    [JsonPropertyName("previous_logical_path")] public string? PreviousLogicalPath { get; init; }
    [JsonPropertyName("revision_id")] public string? RevisionId { get; init; }
    [JsonPropertyName("version")] public long Version { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("deleted_operation_id")] public string? DeletedOperationId { get; init; }
    [JsonPropertyName("actor_id")] public string? ActorId { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
}

public sealed class FileChangesAckRequest
{
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("sequence")] public long Sequence { get; init; }
}

public sealed class DirectoryOperationRequest
{
    [JsonPropertyName("operation_id")] public string OperationId { get; init; } = string.Empty;
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
    [JsonPropertyName("scope_id")] public string ScopeId { get; init; } = string.Empty;
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    [JsonPropertyName("logical_path")] public string? LogicalPath { get; init; }
    [JsonPropertyName("expected_version")] public long? ExpectedVersion { get; init; }
    [JsonPropertyName("target_scope_id")] public string? TargetScopeId { get; init; }
    [JsonPropertyName("target_project_id")] public string? TargetProjectId { get; init; }
    [JsonPropertyName("target_logical_path")] public string? TargetLogicalPath { get; init; }
}

public sealed class DirectoryOperationResponse
{
    [JsonPropertyName("directory_id")] public string? DirectoryId { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
    [JsonPropertyName("version")] public long Version { get; init; }
    [JsonPropertyName("change_sequence")] public long? ChangeSequence { get; init; }
}

public sealed class FileDirectoryBatchEntry
{
    [JsonPropertyName("scope_id")] public string ScopeId { get; init; } = string.Empty;
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    [JsonPropertyName("logical_path")] public string LogicalPath { get; init; } = string.Empty;
}

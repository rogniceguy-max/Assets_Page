using System.Net.Http;
using System.Text.Json;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Logical file, revision, transfer-intent and synchronization endpoints.</summary>
public sealed partial class V2ApiClient
{
    private const int MaxFileListResponseBytes = 8 * 1024 * 1024;

    public Task<LogicalFileListResponse> ListFilesAsync(
        string bearerToken,
        string scopeId,
        string? projectId,
        string parentPath,
        string mediaKind = "all",
        bool includeDeleted = false,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentNullException.ThrowIfNull(parentPath);
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var query = new List<string>
        {
            "scope_id=" + Uri.EscapeDataString(scopeId),
            "parent_path=" + Uri.EscapeDataString(parentPath),
            "media_kind=" + Uri.EscapeDataString(mediaKind),
            "include_deleted=" + (includeDeleted ? "true" : "false"),
            "limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(projectId)) query.Add("project_id=" + Uri.EscapeDataString(projectId));
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add("cursor=" + Uri.EscapeDataString(cursor));
        return SendJsonAsync<LogicalFileListResponse>(HttpMethod.Get, "api/v2/files?" + string.Join('&', query),
            null, bearerToken, cancellationToken, MaxFileListResponseBytes);
    }

    public Task<FileRevisionListResponse> ListFileRevisionsAsync(
        string bearerToken, string fileId, CancellationToken cancellationToken = default) =>
        SendJsonAsync<FileRevisionListResponse>(HttpMethod.Get,
            $"api/v2/files/{Uri.EscapeDataString(fileId)}/revisions", null, bearerToken,
            cancellationToken, MaxFileListResponseBytes);

    public Task<FileOperationBeginResponse> BeginFileOperationAsync(
        string bearerToken, FileOperationRequest request, CancellationToken cancellationToken = default) =>
        SendJsonAsync<FileOperationBeginResponse>(HttpMethod.Post, "api/v2/file-operations",
            request, bearerToken, cancellationToken);

    public Task<FileOperationsBatchResponse> BeginFileOperationsBatchAsync(
        string bearerToken, FileOperationsBatchRequest request, CancellationToken cancellationToken = default) =>
        SendJsonAsync<FileOperationsBatchResponse>(HttpMethod.Post, "api/v2/file-operations/batch",
            request, bearerToken, cancellationToken, MaxFileListResponseBytes);

    public Task<DirectoryOperationResponse> CreateDirectoryAsync(
        string bearerToken, DirectoryOperationRequest request, CancellationToken cancellationToken = default) =>
        SendJsonAsync<DirectoryOperationResponse>(HttpMethod.Post, "api/v2/directories",
            request, bearerToken, cancellationToken);

    public Task<DirectoryOperationResponse> MutateDirectoryAsync(
        string bearerToken, string directoryId, DirectoryOperationRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<DirectoryOperationResponse>(HttpMethod.Post,
            $"api/v2/directories/{Uri.EscapeDataString(directoryId)}/operations",
            request, bearerToken, cancellationToken);

    public Task<FileOperationBeginResponse> CommitFileOperationAsync(
        string bearerToken, string operationId, FileTransferReceipt receipt,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<FileOperationBeginResponse>(HttpMethod.Post,
            $"api/v2/file-operations/{Uri.EscapeDataString(operationId)}/commit",
            new FileOperationCommitRequest { Receipt = receipt }, bearerToken, cancellationToken);

    public Task<FileOperationBeginResponse> RestoreFileRevisionAsync(
        string bearerToken, string fileId, string operationId, string revisionId,
        long expectedVersion, string clientId, string source = "wpf",
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<FileOperationBeginResponse>(HttpMethod.Post,
            $"api/v2/files/{Uri.EscapeDataString(fileId)}/restore",
            new RestoreFileRevisionRequest(operationId, revisionId, expectedVersion, clientId, source),
            bearerToken, cancellationToken);

    public Task<FileDownloadTicketResponse> CreateFileDownloadTicketAsync(
        string bearerToken, string fileId, string clientId, string? revisionId = null,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<FileDownloadTicketResponse>(HttpMethod.Post,
            $"api/v2/files/{Uri.EscapeDataString(fileId)}/download",
            new FileDownloadTicketRequest { RevisionId = revisionId, ClientId = clientId },
            bearerToken, cancellationToken);

    public Task<FileChangesResponse> GetFileChangesAsync(
        string bearerToken, string clientId, long afterSequence, int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var query = "client_id=" + Uri.EscapeDataString(clientId) +
            "&after_sequence=" + afterSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return SendJsonAsync<FileChangesResponse>(HttpMethod.Get, "api/v2/sync/changes?" + query,
            null, bearerToken, cancellationToken, MaxFileListResponseBytes);
    }

    public async Task AcknowledgeFileChangesAsync(
        string bearerToken, string clientId, long sequence, CancellationToken cancellationToken = default)
    {
        _ = await SendJsonAsync<JsonElement>(HttpMethod.Post, "api/v2/sync/ack",
            new FileChangesAckRequest { ClientId = clientId, Sequence = sequence },
            bearerToken, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record RestoreFileRevisionRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("operation_id")] string OperationId,
    [property: System.Text.Json.Serialization.JsonPropertyName("revision_id")] string RevisionId,
    [property: System.Text.Json.Serialization.JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: System.Text.Json.Serialization.JsonPropertyName("client_id")] string ClientId,
    [property: System.Text.Json.Serialization.JsonPropertyName("source")] string Source);

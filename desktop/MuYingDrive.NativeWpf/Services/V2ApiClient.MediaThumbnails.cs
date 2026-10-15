using System.Text.Json.Serialization;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Revision-specific thumbnail derivative ticket endpoints.</summary>
public sealed partial class V2ApiClient
{
    public Task<FileDownloadTicketResponse> CreateFileThumbnailTicketAsync(
        string bearerToken, string fileId, string clientId, string? revisionId = null,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<FileDownloadTicketResponse>(HttpMethod.Post,
            $"api/v2/files/{Uri.EscapeDataString(fileId)}/thumbnail",
            new FileThumbnailTicketRequest { RevisionId = revisionId, ClientId = clientId },
            bearerToken, cancellationToken);
}

internal sealed class FileThumbnailTicketRequest
{
    [JsonPropertyName("revision_id")] public string? RevisionId { get; init; }
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
}

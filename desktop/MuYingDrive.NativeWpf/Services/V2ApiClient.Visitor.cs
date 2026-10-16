using System.Net.Http;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Creates and revokes login-free visitor sessions.</summary>
public sealed partial class V2ApiClient
{
    public Task<VisitorSessionResponse> CreateVisitorSessionAsync(CancellationToken cancellationToken = default) =>
        SendJsonAsync<VisitorSessionResponse>(
            HttpMethod.Post, "api/v2/visitor/session", null, null, cancellationToken);

    public async Task EndVisitorSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        _ = await SendJsonAsync<V2LogoutResponse>(
            HttpMethod.Delete, "api/v2/visitor/session", null, sessionToken, cancellationToken)
            .ConfigureAwait(false);
    }
}

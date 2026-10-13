using System.Net.Http;
using System.Net.Http.Headers;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// Visitor channel calls, added to the pinned client so the tour uses exactly the same
/// TLS and error handling as every other request.
/// </summary>
/// <remarks>
/// The visitor deliberately does not open a Windows SMB connection: a second SMB
/// identity for the same NAS collides with the employee's own session (WNet error
/// 1219). Reads go through the backend, which performs them as a low-privilege NAS
/// identity so the real ACL decides what is visible.
/// </remarks>
public sealed partial class V2ApiClient
{
    // A single listing may legitimately carry thousands of entries, far beyond the
    // default JSON response cap.
    private const int MaximumListingBytes = 4 * 1024 * 1024;
    private const int DownloadChunkBytes = 128 * 1024;
    private const long MaximumDownloadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Starts a login-free visitor session. Requires no credentials.</summary>
    public Task<VisitorSessionResponse> CreateVisitorSessionAsync(CancellationToken cancellationToken = default) =>
        SendJsonAsync<VisitorSessionResponse>(
            HttpMethod.Post, "api/v2/visitor/session", null, null, cancellationToken);

    /// <summary>Ends the session on the server so the token cannot be reused.</summary>
    public async Task EndVisitorSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        _ = await SendJsonAsync<V2LogoutResponse>(
            HttpMethod.Delete, "api/v2/visitor/session", null, sessionToken, cancellationToken).ConfigureAwait(false);
    }

    public Task<VisitorEntriesResponse> ListVisitorAsync(
        string sessionToken, string spaceId, string relativePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        var path = Uri.EscapeDataString(relativePath ?? string.Empty);
        var space = Uri.EscapeDataString(spaceId);
        return SendJsonAsync<VisitorEntriesResponse>(
            HttpMethod.Get, $"api/v2/visitor/spaces/{space}/entries?path={path}", null, sessionToken,
            cancellationToken, MaximumListingBytes);
    }

    /// <summary>
    /// Downloads a visitor-visible file to <paramref name="destination"/>, returning the
    /// number of bytes written. The destination is written through so a partly fetched
    /// file never looks complete.
    /// </summary>
    public async Task<long> DownloadVisitorFileAsync(
        string sessionToken, string spaceId, string relativePath, string destination,
        IProgress<long>? receivedBytes = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        var uri = new Uri(_baseUri,
            $"api/v2/visitor/spaces/{Uri.EscapeDataString(spaceId)}/file?path={Uri.EscapeDataString(relativePath ?? string.Empty)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new V2ApiException("下载参观文件超时，请稍后重试。");
        }
        catch (HttpRequestException exception)
        {
            throw new V2ApiException("无法下载参观文件，请检查网络后重试。", inner: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                throw CreateApiError(response.StatusCode, errorBody);
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared is long length && (length < 0 || length > MaximumDownloadBytes))
                throw new V2ApiException("参观文件超出允许大小。");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                DownloadChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            var buffer = new byte[DownloadChunkBytes];
            long written = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                written = checked(written + read);
                if (written > MaximumDownloadBytes)
                    throw new V2ApiException("参观文件超出允许大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                receivedBytes?.Report(written);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (declared is long expected && written != expected)
                throw new V2ApiException("参观文件下载不完整，请重试。");
            return written;
        }
    }
}

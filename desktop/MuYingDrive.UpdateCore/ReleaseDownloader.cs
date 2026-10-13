using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MuYingDrive.UpdateCore;

/// <summary>Raised when a release cannot be fetched or fails verification.</summary>
public sealed class ReleaseDownloadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Fetches and verifies a published release over the pinned HTTPS channel.
/// </summary>
/// <remarks>
/// Used by the first-install bootstrap. The client has its own API client for identity
/// endpoints; this type deliberately covers only the anonymous release reads, and both
/// share <see cref="CertificatePin"/> so the TLS rules cannot drift apart.
/// </remarks>
public sealed class ReleaseDownloader : IDisposable
{
    private const int MaximumManifestBytes = 64 * 1024;
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly byte[] _certificatePin;

    public ReleaseDownloader(Uri baseUri, string leafCertificateSha256)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException(
                "The V2 API base URI must be an absolute HTTPS URI without query or fragment.", nameof(baseUri));
        }

        _baseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _certificatePin = CertificatePin.ParseSha256Pin(leafCertificateSha256);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                (_, certificate, chain, errors) => CertificatePin.IsTrusted(certificate, chain, errors, _certificatePin)
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Fetches the published manifest and verifies its ECDSA signature.</summary>
    public async Task<VerifiedUpdateManifest> GetStableManifestAsync(CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(new Uri(_baseUri, "api/v2/updates/stable/manifest"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseDownloadException("连接更新服务器超时，请检查网络后重试。");
        }
        catch (HttpRequestException exception)
        {
            throw new ReleaseDownloadException(UpdateFailureText.DescribeConnectionFailure(exception), exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ReleaseDownloadException($"更新服务器返回了 {(int)response.StatusCode}，暂时无法获取更新信息。");
            if (response.Content.Headers.ContentLength is long length && length > MaximumManifestBytes)
                throw new ReleaseDownloadException("更新清单超出允许大小。");

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length is 0 or > MaximumManifestBytes)
                throw new ReleaseDownloadException("更新清单超出允许大小。");
            return UpdateManifestVerifier.Verify(body);
        }
    }

    /// <summary>
    /// Downloads the package described by a verified manifest, resuming a partial file
    /// when one is present, and returns the path of a fully verified package.
    /// </summary>
    public async Task<string> DownloadPackageAsync(
        VerifiedUpdateManifest verified,
        string partialPath,
        IProgress<long>? receivedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verified);
        var manifest = verified.Value;
        var etag = new EntityTagHeaderValue($"\"{manifest.Sha256.ToLowerInvariant()}\"");

        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        var currentLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (currentLength > manifest.PackageSize)
        {
            File.Delete(partialPath);
            currentLength = 0;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(_baseUri, $"api/v2/updates/packages/{manifest.PackageId.ToLowerInvariant()}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (currentLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(currentLength, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(etag);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseDownloadException("下载客户端安装包超时，请检查网络后重试。");
        }
        catch (HttpRequestException exception)
        {
            throw new ReleaseDownloadException(
                UpdateFailureText.DescribeConnectionFailure(exception, "无法下载客户端安装包"), exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ReleaseDownloadException($"下载客户端安装包失败（服务器返回 {(int)response.StatusCode}）。");

            var responseEtag = response.Headers.ETag;
            if (responseEtag is null || responseEtag.IsWeak ||
                !string.Equals(responseEtag.Tag, etag.Tag, StringComparison.Ordinal))
            {
                throw new ReleaseDownloadException("安装包标识与签名清单不一致，已停止安装。");
            }

            long writeOffset;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != currentLength || range.Length != manifest.PackageSize || currentLength == 0)
                    throw new ReleaseDownloadException("服务器返回的续传范围无效，已停止安装。");
                writeOffset = currentLength;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                writeOffset = 0;
            }
            else
            {
                throw new ReleaseDownloadException("服务器返回的安装包响应无效，已停止安装。");
            }

            var expectedBytes = manifest.PackageSize - writeOffset;
            if (expectedBytes < 0 ||
                (response.Content.Headers.ContentLength is long contentLength && contentLength != expectedBytes))
            {
                throw new ReleaseDownloadException("安装包长度与签名清单不一致，已停止安装。");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(partialPath,
                writeOffset == 0 ? FileMode.Create : FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            output.Position = writeOffset;

            var buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                var read = await ReadChunkAsync(input, buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                written = checked(written + read);
                if (written > expectedBytes)
                    throw new ReleaseDownloadException("安装包响应超过签名清单声明的长度，已停止安装。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                receivedBytes?.Report(writeOffset + written);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (written != expectedBytes)
                throw new ReleaseDownloadException("客户端安装包未下载完整，请重试。");
        }

        // The manifest signature already covered this hash; re-checking the bytes on disk
        // closes the gap between "what was signed" and "what actually arrived".
        var actual = await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partialPath);
            throw new ReleaseDownloadException("客户端安装包校验失败，已删除损坏的文件。");
        }

        var packagePath = partialPath + ".zip";
        File.Move(partialPath, packagePath, overwrite: true);
        return packagePath;
    }

    public void Dispose() => _http.Dispose();

    private static async Task<int> ReadChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadIdleTimeout);
        try
        {
            return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseDownloadException("安装包下载长时间没有数据，请重试。");
        }
        catch (IOException exception)
        {
            // A response that ends early surfaces as HttpIOException, an IOException whose
            // Message is raw CLR English. Left alone it reaches the setup window verbatim,
            // where it reads like a crash rather than a dropped link.
            throw new ReleaseDownloadException(UpdateFailureText.DownloadInterrupted, exception);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        // The signed manifest carries a lowercase hash; ToHexStringLower is .NET 9+.
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

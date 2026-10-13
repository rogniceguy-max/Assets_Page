using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Connection settings for the authenticated V2 HTTPS API.</summary>
public sealed record V2ApiClientOptions(Uri BaseUri, string LeafCertificateSha256);

/// <summary>An API failure with a safe message suitable for display in the UI.</summary>
public sealed class V2ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public string? ErrorCode { get; }

    internal V2ApiException(string message, HttpStatusCode? statusCode = null, string? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}

/// <summary>
/// HTTPS client for V2 identity endpoints. Hostname and certificate validity checks
/// remain mandatory; the configured SHA-256 pin can anchor the exact self-signed leaf.
/// </summary>
public sealed partial class V2ApiClient : IDisposable
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan UpdateReadIdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly byte[] _certificatePin;

    public V2ApiClient(V2ApiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.BaseUri.IsAbsoluteUri || options.BaseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.BaseUri.Query) || !string.IsNullOrEmpty(options.BaseUri.Fragment))
            throw new ArgumentException("The V2 API base URI must be an absolute HTTPS URI without query or fragment.", nameof(options));

        _baseUri = new Uri(options.BaseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _certificatePin = ParseSha256Pin(options.LeafCertificateSha256);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = ValidateServerCertificate
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<V2AuthenticationResponse> RegisterAsync(
        string username, string password, string groupType, CancellationToken cancellationToken = default)
    {
        var request = new V2RegistrationRequest(username, password, groupType);
        var response = await SendJsonAsync<V2AuthenticationResponse>(HttpMethod.Post, "api/v2/register", request, null, cancellationToken)
            .ConfigureAwait(false);
        ValidateAuthenticationResponse(response);
        return response;
    }

    public async Task<V2AuthenticationResponse> LoginAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        var request = new V2CredentialsRequest(username, password);
        var response = await SendJsonAsync<V2AuthenticationResponse>(HttpMethod.Post, "api/v2/login", request, null, cancellationToken)
            .ConfigureAwait(false);
        ValidateAuthenticationResponse(response);
        return response;
    }

    public async Task<V2SessionResponse> GetSessionAsync(string bearerToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        var response = await SendJsonAsync<V2SessionResponse>(HttpMethod.Get, "api/v2/session", null, bearerToken, cancellationToken)
            .ConfigureAwait(false);
        if (!response.Authenticated)
            throw new V2ApiException("登录状态已失效，请重新登录。");
        ValidateUser(response.User);
        return response;
    }

    public async Task LogoutAsync(string bearerToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        _ = await SendJsonAsync<V2LogoutResponse>(HttpMethod.Post, "api/v2/logout", new { }, bearerToken, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<byte[]> GetStableUpdateManifestAsync(CancellationToken cancellationToken = default) =>
        SendBytesAsync(HttpMethod.Get, "api/v2/updates/stable/manifest", null, cancellationToken);

    public async Task DownloadUpdatePackageAsync(string packageId, string sha256, long expectedSize,
        string partialPath, IProgress<long>? receivedBytes = null, CancellationToken cancellationToken = default)
    {
        if (packageId.Length != 32 || !packageId.All(Uri.IsHexDigit) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The signed update manifest contains an invalid package identifier.");

        var etag = new EntityTagHeaderValue($"\"{sha256.ToLowerInvariant()}\"");
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        var currentLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (currentLength > expectedSize)
        {
            File.Delete(partialPath);
            currentLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, $"api/v2/updates/packages/{packageId.ToLowerInvariant()}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (currentLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(currentLength, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(etag);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new V2ApiException("更新包下载超时；下次检查时会尝试续传。");
        }
        catch (HttpRequestException exception)
        {
            throw new V2ApiException("无法安全下载更新包；请检查网络后重试。", inner: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                throw CreateApiError(response.StatusCode, errorBody);
            }

            var responseEtag = response.Headers.ETag;
            if (responseEtag is null || responseEtag.IsWeak || !string.Equals(responseEtag.Tag, etag.Tag, StringComparison.Ordinal))
                throw new V2ApiException("更新包版本标识与签名清单不一致。");

            long writeOffset;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != currentLength || range.Length != expectedSize || currentLength == 0)
                    throw new V2ApiException("服务器返回的更新续传范围无效。");
                writeOffset = currentLength;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                writeOffset = 0;
            }
            else
            {
                throw new V2ApiException("服务器返回的更新包响应无效。");
            }

            var expectedResponseBytes = expectedSize - writeOffset;
            if (expectedResponseBytes < 0 || response.Content.Headers.ContentLength is long contentLength && contentLength != expectedResponseBytes)
                throw new V2ApiException("更新包响应长度与签名清单不一致。");

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
                var read = await ReadUpdateChunkAsync(input, buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                written = checked(written + read);
                if (written > expectedResponseBytes)
                    throw new V2ApiException("更新包响应超过签名清单声明的长度。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                receivedBytes?.Report(writeOffset + written);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (written != expectedResponseBytes)
                throw new EndOfStreamException("更新包下载尚未完成；下次检查时会从已保存的位置继续。");
        }
    }

    public void Dispose() => _http.Dispose();

    private static async Task<int> ReadUpdateChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(UpdateReadIdleTimeout);
        try
        {
            return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new V2ApiException("更新包下载长时间没有数据；已保留已收数据，稍后可继续续传。");
        }
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string relativePath, object? payload, string? bearerToken, CancellationToken cancellationToken, int maximumResponseBytes = MaxResponseBytes)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (payload is not null)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new V2ApiException("连接服务器超时，请检查网络后重试。");
        }
        catch (HttpRequestException ex)
        {
            // Do not include exception details: handlers and platform errors may expose endpoint data.
            throw new V2ApiException("无法安全连接服务器，请检查网络或联系管理员。", inner: ex);
        }

        using (response)
        {
            var body = await ReadBoundedAsync(response.Content, cancellationToken, maximumResponseBytes).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateApiError(response.StatusCode, body);

            try
            {
                return JsonSerializer.Deserialize<T>(body, JsonOptions)
                    ?? throw new V2ApiException("服务器返回的数据格式无效。");
            }
            catch (JsonException ex)
            {
                throw new V2ApiException("服务器返回的数据格式无效。", response.StatusCode, inner: ex);
            }
        }
    }

    private async Task<byte[]> SendBytesAsync(HttpMethod method, string relativePath, string? bearerToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new V2ApiException("更新检查超时；沐影盘仍可正常使用。");
        }
        catch (HttpRequestException exception)
        {
            throw new V2ApiException("无法安全检查更新；沐影盘仍可正常使用。", inner: exception);
        }
        using (response)
        {
            var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateApiError(response.StatusCode, body);
            return body;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken, int maximum = MaxResponseBytes)
    {
        if (content.Headers.ContentLength > maximum)
            throw new V2ApiException("服务器响应超出允许大小。");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            if (buffer.Length + count > maximum)
                throw new V2ApiException("服务器响应超出允许大小。");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static V2ApiException CreateApiError(HttpStatusCode statusCode, byte[] body)
    {
        string? code = null;
        string? message = null;
        try
        {
            var error = JsonSerializer.Deserialize<V2ApiErrorResponse>(body, JsonOptions);
            code = Sanitize(error?.Code, 64);
            message = Sanitize(error?.Error, 240);
        }
        catch (JsonException)
        {
            // Use a local fallback for non-JSON and malformed server responses.
        }

        message ??= statusCode switch
        {
            HttpStatusCode.Unauthorized => "用户名、密码或登录状态无效。",
            HttpStatusCode.Conflict => "该姓名已注册或存在账号冲突。",
            HttpStatusCode.BadRequest => "提交的信息无效，请检查后重试。",
            HttpStatusCode.ServiceUnavailable => "服务器暂时不可用，请稍后重试。",
            _ => "服务器暂时无法完成请求，请稍后重试。"
        };
        return new V2ApiException(message, statusCode, code);
    }

    private bool ValidateServerCertificate(HttpRequestMessage _, X509Certificate2? certificate, X509Chain? chain, System.Net.Security.SslPolicyErrors errors)
    {
        if (certificate is null ||
            (errors & (System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable |
                       System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;

        try
        {
            var now = DateTime.UtcNow;
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
                return false;

            var chainErrors = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
            if ((errors & ~chainErrors) != System.Net.Security.SslPolicyErrors.None)
                return false;
            if ((errors & chainErrors) != 0 &&
                (chain is null || chain.ChainStatus.Any(status =>
                    (status.Status & ~X509ChainStatusFlags.UntrustedRoot) != X509ChainStatusFlags.NoError)))
                return false;

            var actualPin = certificate.GetCertHash(HashAlgorithmName.SHA256);
            return CryptographicOperations.FixedTimeEquals(actualPin, _certificatePin);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] ParseSha256Pin(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var normalized = new string(fingerprint.Where(Uri.IsHexDigit).ToArray());
        if (normalized.Length != 64 || fingerprint.Any(c => !Uri.IsHexDigit(c) && c != ':' && c != '-' && !char.IsWhiteSpace(c)))
            throw new ArgumentException("The certificate fingerprint must contain exactly 32 SHA-256 bytes in hexadecimal form.", nameof(fingerprint));
        try
        {
            return Convert.FromHexString(normalized);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The certificate fingerprint is invalid.", nameof(fingerprint), ex);
        }
    }

    private static string? Sanitize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var clean = new string(value.Where(c => !char.IsControl(c)).Take(maximumLength).ToArray()).Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static void ValidateAuthenticationResponse(V2AuthenticationResponse response)
    {
        if (!response.Success || string.IsNullOrWhiteSpace(response.SessionToken))
            throw new V2ApiException("服务器返回的登录信息无效。");
        ValidateUser(response.User);
    }

    private static void ValidateUser(V2UserSession? user)
    {
        if (user is null || user.Id <= 0 || string.IsNullOrWhiteSpace(user.UserName) ||
            string.IsNullOrWhiteSpace(user.Department) || string.IsNullOrWhiteSpace(user.GroupType) || user.Spaces is null)
            throw new V2ApiException("服务器返回的用户信息无效。");
    }
}


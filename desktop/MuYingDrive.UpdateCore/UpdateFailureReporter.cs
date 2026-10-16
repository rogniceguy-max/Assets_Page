using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MuYingDrive.UpdateCore;

/// <summary>Only stable, non-identifying update startup failure fields are sent to the NAS.</summary>
public sealed record UpdateFailureReport(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("update_id")] string UpdateId,
    [property: JsonPropertyName("from_version")] string FromVersion,
    [property: JsonPropertyName("target_version")] string TargetVersion,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("error_code")] string ErrorCode,
    [property: JsonPropertyName("occurred_at_utc")] DateTimeOffset OccurredAtUtc)
{
    public static UpdateFailureReport Create(string updateId, string fromVersion, string targetVersion,
        string stage, string errorCode) => new(1, updateId.ToLowerInvariant(), fromVersion, targetVersion,
        stage, errorCode, DateTimeOffset.UtcNow);
}

public sealed record UpdateFailureContext(string UpdateId, string FromVersion, string TargetVersion)
{
    public UpdateFailureReport CreateReport(string stage, string errorCode) =>
        UpdateFailureReport.Create(UpdateId, FromVersion, TargetVersion, stage, errorCode);
}

public static class UpdateFailureReporter
{
    private const int MaximumPendingReports = 50;
    private const int MaximumReportBytes = 2048;
    private static readonly TimeSpan MaximumReportAge = TimeSpan.FromDays(30);
    private static readonly Regex VersionPattern = new("^(0|[1-9][0-9]{0,9})\\.(0|[1-9][0-9]{0,9})\\.(0|[1-9][0-9]{0,9})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> Stages = ["health_check", "launch", "rollback", "recovery"];
    private static readonly HashSet<string> ErrorCodes =
    [
        "HEALTH_CHECK_PROCESS_START_FAILED", "HEALTH_CHECK_PROCESS_EXITED", "HEALTH_CHECK_MARKER_MISSING",
        "HEALTH_CHECK_MARKER_MISMATCH", "HEALTH_CHECK_MARKER_READ_FAILED", "HEALTH_CHECK_TIMEOUT",
        "HEALTH_CHECK_EXIT_TIMEOUT", "HEALTH_CHECK_PROCESS_STOP_FAILED", "ROLLBACK_FAILED",
        "INTERRUPTED_RECOVERY_REQUIRED", "CLIENT_LAUNCH_FAILED"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };
    private static readonly SemaphoreSlim OutboxGate = new(1, 1);

    private sealed record ClientConfig(Uri? ApiBaseUri, string? CertificateSha256);

    public static void Queue(UpdateFailureReport report)
    {
        if (!IsValid(report)) return;
        try { Enqueue(report); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    public static async Task SendPendingAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = LoadEndpointOptions();
        if (endpoint?.ApiBaseUri is null || endpoint.CertificateSha256 is null) return;
        await SendPendingAsync(endpoint.ApiBaseUri, endpoint.CertificateSha256, cancellationToken).ConfigureAwait(false);
    }

    public static async Task SendPendingAsync(Uri apiBaseUri, string certificateSha256,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeEndpoint(apiBaseUri, certificateSha256, out var baseUri, out var pin)) return;

        await OutboxGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = GetOutboxDirectory();
            if (!Directory.Exists(directory)) return;
            UpdatePathPolicy.EnsureNoReparsePointComponents(directory);

            using var client = CreateClient(pin);
            foreach (var path in GetPendingReportPaths(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateFailureReport? report;
                try
                {
                    UpdatePathPolicy.EnsureNoReparsePointComponents(path);
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length is <= 0 or > MaximumReportBytes ||
                        DateTimeOffset.UtcNow - info.LastWriteTimeUtc > MaximumReportAge)
                    {
                        if (info.Exists) File.Delete(path);
                        continue;
                    }
                    report = JsonSerializer.Deserialize<UpdateFailureReport>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
                    if (report is null || !IsValid(report))
                    {
                        File.Delete(path);
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    continue;
                }

                try
                {
                    var endpoint = new Uri(baseUri, "api/v2/diagnostics/update-failures");
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
                        File.Delete(path);
                    else
                        break;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or HttpRequestException)
        {
            // Reporting is best-effort and must never change the update or application startup result.
        }
        finally
        {
            OutboxGate.Release();
        }
    }

    public static bool IsValidVersion(string? value) => value is not null && VersionPattern.IsMatch(value);

    private static bool IsValid(UpdateFailureReport report) =>
        report.SchemaVersion == 1 && report.UpdateId is { Length: 32 } updateId && updateId.All(Uri.IsHexDigit) &&
        IsValidVersion(report.FromVersion) && IsValidVersion(report.TargetVersion) &&
        report.Stage is not null && Stages.Contains(report.Stage) && report.ErrorCode is not null && ErrorCodes.Contains(report.ErrorCode) &&
        report.OccurredAtUtc.Offset == TimeSpan.Zero && report.OccurredAtUtc >= DateTimeOffset.UtcNow.AddDays(-30) &&
        report.OccurredAtUtc <= DateTimeOffset.UtcNow.AddHours(24);

    private static void Enqueue(UpdateFailureReport report)
    {
        var directory = GetOutboxDirectory();
        UpdatePathPolicy.EnsureNoReparsePointComponents(directory);
        Directory.CreateDirectory(directory);
        UpdatePathPolicy.EnsureNoReparsePointComponents(directory);

        var now = DateTimeOffset.UtcNow;
        var existing = GetPendingReportPaths(directory).ToList();
        foreach (var path in existing)
        {
            try
            {
                UpdatePathPolicy.EnsureNoReparsePointComponents(path);
                if (now - File.GetLastWriteTimeUtc(path) > MaximumReportAge)
                    File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        existing = GetPendingReportPaths(directory).ToList();
        foreach (var path in existing.Take(Math.Max(0, existing.Count - MaximumPendingReports + 1)))
        {
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        var name = Guid.NewGuid().ToString("N") + ".json";
        var destination = Path.Combine(directory, name);
        var temporary = destination + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (bytes.Length > MaximumReportBytes)
            throw new InvalidDataException("The update failure report is too large.");
        UpdatePathPolicy.EnsureNoReparsePointComponents(temporary);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination, overwrite: false);
    }

    private static IEnumerable<string> GetPendingReportPaths(string directory) =>
        Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(File.GetLastWriteTimeUtc);

    private static string GetOutboxDirectory() => Path.Combine(UpdatePathPolicy.UpdateRoot, "reports", "outbox");

    private static ClientConfig? LoadEndpointOptions()
    {
        var candidates = new[]
        {
            UpdatePathPolicy.ClientSettingsPath,
            Path.Combine(UpdatePathPolicy.InstallRoot, "nas-client.json"),
            Path.Combine(AppContext.BaseDirectory, "nas-client.json")
        };
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                UpdatePathPolicy.EnsureNoReparsePointComponents(path);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length is <= 0 or > 64 * 1024) continue;
                var config = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllBytes(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    MaxDepth = 4
                });
                if (config is { ApiBaseUri: not null, CertificateSha256: not null } &&
                    TryNormalizeEndpoint(config.ApiBaseUri, config.CertificateSha256, out _, out _))
                    return config;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }

        var api = Environment.GetEnvironmentVariable("MUYING_V2_API_BASE_URI");
        var pin = Environment.GetEnvironmentVariable("MUYING_V2_CERT_SHA256");
        if (Uri.TryCreate(api, UriKind.Absolute, out var baseUri) && pin is not null &&
            TryNormalizeEndpoint(baseUri, pin, out _, out _))
            return new ClientConfig(baseUri, pin);
        return null;
    }

    private static bool TryNormalizeEndpoint(Uri? apiBaseUri, string? certificateSha256, out Uri baseUri, out byte[] pin)
    {
        baseUri = new Uri("https://invalid.invalid/");
        pin = [];
        if (apiBaseUri is null || !apiBaseUri.IsAbsoluteUri || apiBaseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(apiBaseUri.Query) || !string.IsNullOrEmpty(apiBaseUri.Fragment) || string.IsNullOrWhiteSpace(certificateSha256))
            return false;
        var normalized = new string(certificateSha256.Where(Uri.IsHexDigit).ToArray());
        if (normalized.Length != 64 || certificateSha256.Any(c => !Uri.IsHexDigit(c) && c != ':' && c != '-' && !char.IsWhiteSpace(c)))
            return false;
        try { pin = Convert.FromHexString(normalized); }
        catch (FormatException) { return false; }
        baseUri = new Uri(apiBaseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }

    private static HttpClient CreateClient(byte[] pin)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                CertificatePin.IsTrusted(certificate, chain, errors, pin)
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(5) };
    }
}

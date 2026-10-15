using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// The pinned HTTP client the project-workbook library is handed.
/// </summary>
/// <remarks>
/// A partial of <see cref="V2ApiClient"/> rather than a class of its own, for the same reason
/// the feedback and visitor endpoints are: it needs the certificate pin and the validation
/// callback that already live here. Building a separate client elsewhere would mean a second
/// copy of the pinning rule, which is exactly the drift this codebase has been bitten by
/// before — and a copy that got it wrong would fail open.
///
/// The library takes a raw <see cref="HttpClient"/> and forbids reflection into this one, so
/// this is the seam that exists rather than the one that would be tidiest.
/// </remarks>
public sealed partial class V2ApiClient
{
    /// <summary>
    /// A client for the workbook endpoints: the same certificate pinning, and no overall
    /// timeout.
    /// </summary>
    /// <remarks>
    /// The infinite timeout is deliberate. This client's default is 30 seconds, and the
    /// workbook library sets its own deadlines per request — 90 seconds for a JSON call, 180
    /// for an upload or download. A 30-second ceiling on the shared client would abort every
    /// one of those before the library's own limit was reached, and the failure would look
    /// like a network problem.
    ///
    /// Callers own the result and must dispose it.
    /// </remarks>
    public HttpClient CreateProjectWorkbookClient() => new(
        new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = ValidateServerCertificate
        },
        disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>The absolute root the workbook endpoints live under.</summary>
    public Uri ProjectWorkbookRoot => new(_baseUri, "api/v2/project-workbooks/");

    /// <summary>
    /// Creates a project space, throwing a message fit to show the user on failure.
    /// </summary>
    /// <remarks>
    /// Not in the workbook library: that package models reading and editing workbooks, and
    /// this is the host's own route (PROJECT_SPACE_V1.md). Its own client is created per
    /// call and disposed — creating a project is rare, and a long-lived client here would
    /// have to be owned and disposed by whoever held the command.
    /// </remarks>
    public async Task CreateProjectSpaceAsync(
        string bearerToken, string name, CancellationToken cancellationToken = default)
    {
        using var client = CreateProjectWorkbookClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/v2/project-spaces"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        var json = JsonSerializer.Serialize(new { name });
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;

        // The server's own sentence when there is one — it knows which rule was broken,
        // and it is the only place that does. Otherwise a status-based fallback, because a
        // bare status code tells the user nothing they can act on.
        var detail = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(detail ?? response.StatusCode switch
        {
            HttpStatusCode.Conflict => "已存在同名项目，请换一个名称。",
            HttpStatusCode.UnprocessableEntity => "项目名称无效，请检查后重试。",
            HttpStatusCode.Unauthorized => "登录状态已失效，请重新登录。",
            HttpStatusCode.ServiceUnavailable => "项目服务暂时不可用，请稍后重试。",
            _ => "创建项目失败，请稍后重试。"
        });
    }

    /// <summary>
    /// Deletes a project space by id.
    /// </summary>
    public async Task DeleteProjectSpaceAsync(
        string bearerToken, string projectId, CancellationToken cancellationToken = default)
    {
        using var client = CreateProjectWorkbookClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_baseUri, $"api/v2/project-spaces/{Uri.EscapeDataString(projectId)}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;

        var detail = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(detail ?? response.StatusCode switch
        {
            HttpStatusCode.NotFound => "项目不存在或已被删除。",
            HttpStatusCode.Forbidden => "当前用户无权删除该项目。",
            HttpStatusCode.Unauthorized => "登录状态已失效，请重新登录。",
            HttpStatusCode.ServiceUnavailable => "项目服务暂时不可用，请稍后重试。",
            _ => "删除项目失败，请稍后重试。"
        });
    }

    private static async Task<string?> ReadErrorMessageAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            foreach (var name in new[] { "message", "error", "detail" })
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) break;
                if (!document.RootElement.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                    return text;
                if (value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("message", out var nested) &&
                    nested.ValueKind == JsonValueKind.String && nested.GetString() is { Length: > 0 } nestedText)
                    return nestedText;
            }
        }
        catch (JsonException)
        {
            // Not JSON; the status-based fallback is the answer.
        }
        return null;
    }
}

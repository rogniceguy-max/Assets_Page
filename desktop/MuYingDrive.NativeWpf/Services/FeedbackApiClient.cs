using System.Net.Http;
using System.Net.Http.Headers;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

public sealed partial class V2ApiClient
{
    public Task<FeedbackCapabilities> FeedbackCapabilitiesAsync(string token, CancellationToken ct) =>
        SendJsonAsync<FeedbackCapabilities>(HttpMethod.Get, "api/v2/feedback/capabilities", null, token, ct);

    public Task<FeedbackPage> FeedbackListAsync(string token, string kind, string query, int page, string scope, CancellationToken ct) =>
        SendJsonAsync<FeedbackPage>(HttpMethod.Get,
            $"api/v2/feedback?kind={Uri.EscapeDataString(kind)}&q={Uri.EscapeDataString(query)}&page={page}&scope={Uri.EscapeDataString(scope)}",
            null, token, ct, 512 * 1024);

    public Task<FeedbackItem> FeedbackDetailAsync(string token, string id, CancellationToken ct) =>
        SendJsonAsync<FeedbackItem>(HttpMethod.Get, $"api/v2/feedback/{Uri.EscapeDataString(id)}", null, token, ct);

    public Task<FeedbackCreated> CreateFeedbackAsync(string token, FeedbackSubmission submission, CancellationToken ct) =>
        SendJsonAsync<FeedbackCreated>(HttpMethod.Post, "api/v2/feedback", submission, token, ct);

    public Task<FeedbackItem> UpdateFeedbackAsync(string token, string id, string status, string visibility, CancellationToken ct) =>
        SendJsonAsync<FeedbackItem>(HttpMethod.Patch, $"api/v2/feedback/{Uri.EscapeDataString(id)}", new { status, visibility }, token, ct);

    public async Task<byte[]> FeedbackImageAsync(string token, string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, $"api/v2/feedback/attachments/{Uri.EscapeDataString(id)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateApiError(response.StatusCode, await ReadBoundedAsync(response.Content, ct).ConfigureAwait(false));
            if (response.Content.Headers.ContentType?.MediaType != "image/png")
                throw new V2ApiException("截图格式无效。");
            return await ReadBoundedAsync(response.Content, ct, 6 * 1024 * 1024).ConfigureAwait(false);
        }
        catch (HttpRequestException) { throw new V2ApiException("截图暂时无法加载，请重试。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new V2ApiException("截图加载超时，请重试。"); }
    }
}

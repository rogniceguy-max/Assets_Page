using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MuYingDrive.ProjectWorkbooks;

// Host owns the client/TLS handler. Keep authentication and certificate validation, disable mutation
// retries and cross-origin redirects in the host handler. This service never retries a request.
public sealed class HttpProjectWorkbookService : IProjectWorkbookService
{
    private readonly HttpClient _client;
    private readonly Func<string> _getBearerToken;
    private readonly Uri _root;
    private readonly WorkbookTransportOptions _limits;
    private readonly Func<string>? _getAccountId;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private sealed record ApiError(string Code, string Message);
    private sealed record ErrorBody(ApiError Error);
    private sealed record ItemsEnvelope<T>(List<T> Items);
    public long MaximumUploadBytes => _limits.MaxUploadBytes;
    public string OperationScope
    {
        get
        {
            var actor = _getAccountId?.Invoke();
            if (string.IsNullOrWhiteSpace(actor) || actor.Any(char.IsControl))
                throw new InvalidDataException("宿主必须配置当前登录账号的稳定标识，才能安全保存和恢复导入操作。");
            LimitedText(actor, 512);
            // Only this digest goes to disk. Never persist a token or expose the underlying account ID.
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_root.AbsoluteUri + "\n" + actor)));
        }
    }

    public HttpProjectWorkbookService(HttpClient client, Uri apiRoot, Func<string> getBearerToken,
        WorkbookTransportOptions? options = null, Func<string>? getAccountId = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _getBearerToken = getBearerToken ?? throw new ArgumentNullException(nameof(getBearerToken));
        _limits = options ?? new WorkbookTransportOptions(); _limits.Validate();
        _getAccountId = getAccountId;
        if (!apiRoot.IsAbsoluteUri || !string.IsNullOrEmpty(apiRoot.Query) || !string.IsNullOrEmpty(apiRoot.Fragment))
            throw new ArgumentException("接口根地址必须是无查询参数的绝对地址。", nameof(apiRoot));
        _root = new Uri(apiRoot.AbsoluteUri.TrimEnd('/') + "/");
        if (_root.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("接口必须使用 HTTPS。", nameof(apiRoot));
    }

    private static string Id(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl) || value is "." or ".." || value.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidDataException("资源标识无效。");
        LimitedText(value, 512); // Reject unpaired UTF-16 surrogates before URI percent encoding.
        return Uri.EscapeDataString(value);
    }
    private static string FileId(string value)
    {
        if (value is "create" or "import" or "image-assets" or "projects") throw new InvalidDataException("工作簿标识与保留路径冲突。");
        return Id(value);
    }
    private static void Version(long version)
    {
        if (version < 1) throw new InvalidDataException("记录版本无效。");
    }
    private static void Template(string kind)
    {
        if (kind is not ("scene_beats" or "asset_review")) throw new InvalidDataException("仅支持两种模板。");
    }
    private static void WorkbookName(string name)
    {
        if (!string.Equals(Path.GetExtension(name), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("项目只接收 .xlsx 文件。");
    }
    private static void ImageType(string mediaType)
    {
        if (mediaType is not ("image/png" or "image/jpeg" or "image/webp")) throw new InvalidDataException("不支持该图片类型。");
    }
    private static string? LimitedText(string? value, int max, bool nonEmpty = false)
    {
        if (nonEmpty && string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("请填写处理说明。");
        if (value == null) return null;
        int count = 0;
        for (int i = 0; i < value.Length; i++, count++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new InvalidDataException("文本包含无效字符。");
            }
            else if (char.IsLowSurrogate(value[i])) throw new InvalidDataException("文本包含无效字符。");
        }
        if (count > max) throw new InvalidDataException($"文本最多允许 {max} 个字符。");
        return value;
    }
    private static HttpContent JsonBody<T>(T value, int limit = 64 * 1024)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > limit) throw new InvalidDataException("提交内容超过大小限制。");
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
    private HttpRequestMessage Request(HttpMethod method, string route, HttpContent? body)
    {
        var token = _getBearerToken();
        if (string.IsNullOrWhiteSpace(token)) { body?.Dispose(); throw new WorkbookApiException(401, "SESSION_REQUIRED", "请先登录。"); }
        var request = new HttpRequestMessage(method, new Uri(_root, route)) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }
    private static async Task CopyBoundedAsync(HttpContent content, Stream destination, long limit, CancellationToken ct)
    {
        var declared = content.Headers.ContentLength;
        if (declared > limit) throw new InvalidDataException("接口响应超过大小限制。");
        using var source = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            total = checked(total + count);
            if (total > limit) throw new InvalidDataException("接口响应超过大小限制。");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (declared.HasValue && total != declared.Value) throw new InvalidDataException("接口响应不完整，请重试读取。");
        ct.ThrowIfCancellationRequested();
    }
    private static async Task<byte[]> BytesAsync(HttpContent content, int limit, CancellationToken ct)
    {
        using var result = new MemoryStream();
        await CopyBoundedAsync(content, result, limit, ct);
        return result.ToArray();
    }
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        ErrorBody? error = null;
        try { error = JsonSerializer.Deserialize<ErrorBody>(await BytesAsync(response.Content, 64 * 1024, ct), Json); }
        catch (JsonException) { }
        var status = (int)response.StatusCode;
        throw new WorkbookApiException(status, error?.Error?.Code ?? "HTTP_ERROR", error?.Error?.Message ?? $"请求失败（{status}）。");
    }
    private async Task<T> ReadAsync<T>(HttpMethod method, string route, HttpContent? body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_limits.RequestTimeout);
        using var request = Request(method, route, body);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await EnsureSuccessAsync(response, deadline.Token);
        var bytes = await BytesAsync(response.Content, _limits.MaxJsonResponseBytes, deadline.Token);
        var value = JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("接口返回为空。");
        deadline.Token.ThrowIfCancellationRequested(); return value;
    }
    private async Task<IReadOnlyList<T>> ListAsync<T>(string route, CancellationToken ct) =>
        (await ReadAsync<ItemsEnvelope<T>>(HttpMethod.Get, route, null, ct)).Items
            ?? throw new InvalidDataException("接口缺少列表内容。");

    private async Task<T> UploadAsync<T>(string route, string fileName, string mediaType, Stream source,
        IReadOnlyDictionary<string, string>? extraHeaders, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_limits.UploadTimeout);
        // Includes any bounded non-seekable spool, sending, and the complete response in one deadline.
        using var upload = await WorkbookUploadSource.PrepareAsync(source, _limits.MaxUploadBytes, deadline.Token);
        using var content = new WorkbookStreamContent(upload.Stream, upload.Length, deadline.Token);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        content.Headers.ContentLength = upload.Length;
        using var request = Request(HttpMethod.Post, route, content);
        var basename = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(basename) || basename.Any(char.IsControl)) throw new InvalidDataException("文件名无效。");
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(basename));
        if (extraHeaders != null) foreach (var header in extraHeaders) request.Headers.Add(header.Key, Id(header.Value));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await EnsureSuccessAsync(response, deadline.Token);
        var value = JsonSerializer.Deserialize<T>(await BytesAsync(response.Content, _limits.MaxJsonResponseBytes, deadline.Token), Json)
            ?? throw new InvalidDataException("接口返回为空。");
        deadline.Token.ThrowIfCancellationRequested(); return value;
    }
    private async Task DownloadToAsync(string route, string destination, CancellationToken ct)
    {
        var target = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("保存路径无效。");
        var temporary = Path.Combine(directory, ".pwb-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_limits.DownloadTimeout);
        try
        {
            using var request = Request(HttpMethod.Get, route, null);
            request.Headers.Accept.Clear(); request.Headers.Accept.ParseAdd("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await EnsureSuccessAsync(response, deadline.Token);
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mediaType is not ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or "application/octet-stream"))
                throw new InvalidDataException("接口没有返回工作簿文件，原有文件未被覆盖。");
            if (response.Content.Headers.ContentLength == 0) throw new InvalidDataException("接口返回空文件。");
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await CopyBoundedAsync(response.Content, file, _limits.MaxDownloadBytes, deadline.Token);
                if (file.Length < 4) throw new InvalidDataException("接口返回的工作簿不完整。");
                await file.FlushAsync(deadline.Token);
                file.Position = 0;
                var signature = new byte[4];
                await file.ReadExactlyAsync(signature.AsMemory(), deadline.Token);
                if (signature[0] != 0x50 || signature[1] != 0x4b || signature[2] != 0x03 || signature[3] != 0x04)
                    throw new InvalidDataException("接口没有返回 XLSX 容器，原有文件未被覆盖。");
                // Full OOXML validation is still the authenticated host adapter's responsibility.
            }
            deadline.Token.ThrowIfCancellationRequested();
            // Same-directory staging: an error never truncates the existing user file.
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target); // A concurrently created target is not overwritten.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct = default) => ListAsync<ProjectInfo>("projects", ct);
    public Task<IReadOnlyList<ProjectFile>> ListProjectFilesAsync(string projectId, CancellationToken ct = default) => ListAsync<ProjectFile>("?project_id=" + Id(projectId), ct);
    public Task<ProjectFile> CreateInProjectAsync(string projectId, string templateKind, string operationId, CancellationToken ct = default)
    {
        _ = OperationScope;
        Template(templateKind); _ = Id(projectId); _ = Id(operationId);
        return ReadAsync<ProjectFile>(HttpMethod.Post, "create", JsonBody(new { template_kind = templateKind, project_id = projectId, operation_id = operationId }), ct);
    }
    public Task<ProjectFile> ImportRevisionAsync(string projectId, string fileName, Stream source, string operationId, string? revisionOf = null, CancellationToken ct = default)
    {
        _ = OperationScope;
        WorkbookName(fileName); _ = Id(projectId); _ = Id(operationId);
        var headers = new Dictionary<string, string> { ["X-Project-Id"] = projectId, ["X-Operation-Id"] = operationId };
        if (revisionOf != null) { _ = FileId(revisionOf); headers["X-Revision-Of"] = revisionOf; }
        return UploadAsync<ProjectFile>("import", fileName, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", source, headers, ct);
    }
    public Task<IReadOnlyList<ProjectFile>> ListRevisionsAsync(string fileId, CancellationToken ct = default) => ListAsync<ProjectFile>(FileId(fileId) + "/revisions", ct);
    public Task DownloadOriginalToAsync(string fileId, string destination, CancellationToken ct = default) => DownloadToAsync(FileId(fileId) + "/original", destination, ct);
    public Task ExportCurrentAsync(string fileId, long expectedVersion, string destination, CancellationToken ct = default)
    {
        Version(expectedVersion);
        return DownloadToAsync(FileId(fileId) + "/export?expected_version=" + expectedVersion.ToString(CultureInfo.InvariantCulture), destination, ct);
    }
    public Task<ImageSearchPage> SearchImagesAsync(string projectId, string? q = null, string? category = null, string? cursor = null, int pageSize = 40, CancellationToken ct = default)
    {
        if (pageSize is < 1 or > 100) throw new InvalidDataException("素材每页需为 1–100 张。");
        LimitedText(q, 200); LimitedText(category, 200); LimitedText(cursor, 4096);
        var route = "image-assets?project_id=" + Id(projectId) + "&page_size=" + pageSize.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(q)) route += "&q=" + Uri.EscapeDataString(q);
        if (!string.IsNullOrEmpty(category)) route += "&category=" + Uri.EscapeDataString(category);
        if (!string.IsNullOrEmpty(cursor)) route += "&cursor=" + Uri.EscapeDataString(cursor);
        return ReadAsync<ImageSearchPage>(HttpMethod.Get, route, null, ct);
    }
    public Task<ImageAsset> UploadProjectImageAsync(string projectId, string fileName, string mediaType, Stream source, CancellationToken ct = default)
    {
        ImageType(mediaType); _ = Id(projectId);
        return UploadAsync<ImageAsset>("image-assets", fileName, mediaType, source, new Dictionary<string, string> { ["X-Project-Id"] = projectId }, ct);
    }
    public Task<SceneRow> UpdateSceneAsync(string fileId, string rowId, UpdateSceneRequest request, CancellationToken ct = default)
    {
        Version(request.ExpectedVersion);
        if (request.LabelLevel is not (null or "红" or "黄" or "绿")) throw new InvalidDataException("等级仅允许红、黄、绿或空。");
        LimitedText(request.BreakdownPlan, 20000);
        return ReadAsync<SceneRow>(HttpMethod.Put, RowRoute(fileId, rowId, "scene-rows"), JsonBody(request, 256 * 1024), ct);
    }
    public Task<SceneRow> UpdateSceneOriginsAsync(string fileId, string rowId, UpdateSceneOriginsRequest request, CancellationToken ct = default)
    {
        Version(request.ExpectedVersion);
        if (request.Origins == null || request.Origins.Count > 100 || request.Origins.Distinct().Count() != request.Origins.Count)
            throw new InvalidDataException("来源需为最多 100 个唯一节拍行。");
        foreach (var origin in request.Origins)
        {
            _ = FileId(origin.FileId); _ = Id(origin.RowId);
            if (origin.FileId == fileId && origin.RowId == rowId) throw new InvalidDataException("不能关联当前行作为自己的来源。");
        }
        return ReadAsync<SceneRow>(HttpMethod.Put, RowRoute(fileId, rowId, "scene-rows") + "/origins", JsonBody(request), ct);
    }
    public Task<IReadOnlyList<SelectionHistoryEntry>> GetSelectionHistoryAsync(string fileId, string rowId, CancellationToken ct = default) =>
        ListAsync<SelectionHistoryEntry>(RowRoute(fileId, rowId, "asset-rows") + "/selection-history", ct);

    public Task<WorkbookDocument> OpenAsync(string fileId, CancellationToken ct = default) => ReadAsync<WorkbookDocument>(HttpMethod.Get, FileId(fileId), null, ct);
    public async Task<byte[]> GetImageContentAsync(string assetId, bool thumbnail, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_limits.RequestTimeout);
        using var request = Request(HttpMethod.Get, $"image-assets/{Id(assetId)}/content?thumbnail={thumbnail.ToString().ToLowerInvariant()}", null);
        request.Headers.Accept.Clear(); request.Headers.Accept.ParseAdd("image/*");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await EnsureSuccessAsync(response, deadline.Token);
        return await BytesAsync(response.Content, _limits.MaxPreviewBytes, deadline.Token);
    }
    public Task<AssetRow> UpdateImagesAsync(string fileId, string rowId, UpdateImagesRequest request, CancellationToken ct = default)
    {
        Version(request.ExpectedVersion); LimitedText(request.SelectionReason, 4000);
        if (request.SelectedImageId != null) _ = Id(request.SelectedImageId);
        if (request.CandidateImageIds == null || request.CandidateImageIds.Count > 100 || request.CandidateImageIds.Distinct(StringComparer.Ordinal).Count() != request.CandidateImageIds.Count)
            throw new InvalidDataException("备选图片需为最多 100 个唯一图片。");
        foreach (var id in request.CandidateImageIds) _ = Id(id);
        return ReadAsync<AssetRow>(HttpMethod.Put, RowRoute(fileId, rowId, "asset-rows") + "/images", JsonBody(request), ct);
    }
    private static string RowRoute(string fileId, string rowId, string rowKind)
    {
        if (rowKind is not ("asset-rows" or "scene-rows")) throw new InvalidDataException("记录类型无效。");
        return $"{FileId(fileId)}/{rowKind}/{Id(rowId)}";
    }
    private static void ReviewTarget(string rowKind, string? imageId)
    {
        if (rowKind is not ("asset-rows" or "scene-rows")) throw new InvalidDataException("记录类型无效。");
        if (rowKind == "scene-rows" && imageId != null) throw new InvalidDataException("节拍讨论不能绑定图片。");
        if (imageId != null) _ = Id(imageId);
    }
    public Task<IReadOnlyList<ReviewSummary>> GetReviewSummariesForRowAsync(string fileId, string rowId, string rowKind, CancellationToken ct = default) =>
        ListAsync<ReviewSummary>(RowRoute(fileId, rowId, rowKind) + "/review-summaries", ct);
    public Task<ReviewThread> GetReviewThreadForRowAsync(string fileId, string rowId, string rowKind, string? imageId, CancellationToken ct = default)
    {
        ReviewTarget(rowKind, imageId);
        return ReadAsync<ReviewThread>(HttpMethod.Get, RowRoute(fileId, rowId, rowKind) + "/reviews" + (imageId == null ? "" : "?image_id=" + Id(imageId)), null, ct);
    }
    public Task<ReviewThread> VoteAsync(string fileId, string rowId, ReviewVoteRequest request, CancellationToken ct = default)
    {
        _ = Id(request.ImageId);
        if (request.Verdict is not (null or "good" or "bad")) throw new InvalidDataException("评价仅支持好、不好或撤回。");
        return ReadAsync<ReviewThread>(HttpMethod.Put, RowRoute(fileId, rowId, "asset-rows") + "/reviews/vote", JsonBody(request), ct);
    }
    public Task<ReviewThread> AddCommentForRowAsync(string fileId, string rowId, string rowKind, AddReviewCommentRequest request, CancellationToken ct = default)
    {
        ReviewTarget(rowKind, request.ImageId); request = request with { Text = ReviewText.Validate(request.Text) };
        return ReadAsync<ReviewThread>(HttpMethod.Post, RowRoute(fileId, rowId, rowKind) + "/reviews/comments", JsonBody(request), ct);
    }
    public Task<ReviewThread> EditCommentForRowAsync(string fileId, string rowId, string rowKind, string commentId, EditReviewCommentRequest request, CancellationToken ct = default)
    {
        Version(request.ExpectedVersion); request = request with { Text = ReviewText.Validate(request.Text) };
        return ReadAsync<ReviewThread>(HttpMethod.Put, RowRoute(fileId, rowId, rowKind) + "/reviews/comments/" + Id(commentId), JsonBody(request), ct);
    }
    public Task<ReviewThread> DeleteCommentForRowAsync(string fileId, string rowId, string rowKind, string commentId, long expectedVersion, CancellationToken ct = default)
    {
        Version(expectedVersion);
        return ReadAsync<ReviewThread>(HttpMethod.Delete, RowRoute(fileId, rowId, rowKind) + "/reviews/comments/" + Id(commentId)
            + "?expected_version=" + expectedVersion.ToString(CultureInfo.InvariantCulture), null, ct);
    }
    public Task<ReviewThread> ResolveCommentAsync(string fileId, string rowId, string rowKind, string commentId, ResolveCommentRequest request, CancellationToken ct = default)
    {
        Version(request.ExpectedVersion); LimitedText(request.ResolutionNote, 4000, request.Resolved);
        return ReadAsync<ReviewThread>(HttpMethod.Put, RowRoute(fileId, rowId, rowKind) + "/reviews/comments/" + Id(commentId) + "/resolution", JsonBody(request), ct);
    }

    // Legacy v2 entry points for an incremental host migration. New UI never uses unscoped methods.
    public Task<IReadOnlyList<ProjectFile>> ListAsync(CancellationToken ct = default) => ListAsync<ProjectFile>("", ct);
    public Task<ProjectFile> CreateAsync(string templateKind, CancellationToken ct = default)
    { Template(templateKind); return ReadAsync<ProjectFile>(HttpMethod.Post, "create", JsonBody(new { template_kind = templateKind }), ct); }
    public Task<ProjectFile> ImportAsync(string fileName, Stream source, CancellationToken ct = default)
    { WorkbookName(fileName); return UploadAsync<ProjectFile>("import", fileName, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", source, null, ct); }
    public Task<IReadOnlyList<ImageAsset>> ListImagesAsync(CancellationToken ct = default) => ListAsync<ImageAsset>("image-assets", ct);
    public Task<ImageAsset> UploadImageAsync(string fileName, string mediaType, Stream source, CancellationToken ct = default)
    { ImageType(mediaType); return UploadAsync<ImageAsset>("image-assets", fileName, mediaType, source, null, ct); }
    public async Task<byte[]> DownloadOriginalAsync(string fileId, CancellationToken ct = default)
    {
        // Compatibility only: legacy byte[] API remains bounded by the old JSON-sized cap.
        // Use DownloadOriginalToAsync for large originals and atomic replacement.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_limits.DownloadTimeout);
        using var request = Request(HttpMethod.Get, FileId(fileId) + "/original", null);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await EnsureSuccessAsync(response, deadline.Token);
        return await BytesAsync(response.Content, _limits.MaxJsonResponseBytes, deadline.Token);
    }
    public Task<IReadOnlyList<ReviewSummary>> GetReviewSummariesAsync(string fileId, string rowId, CancellationToken ct = default) => GetReviewSummariesForRowAsync(fileId, rowId, "asset-rows", ct);
    public Task<ReviewThread> GetReviewThreadAsync(string fileId, string rowId, string? imageId, CancellationToken ct = default) => GetReviewThreadForRowAsync(fileId, rowId, "asset-rows", imageId, ct);
    public Task<ReviewThread> AddCommentAsync(string fileId, string rowId, AddReviewCommentRequest request, CancellationToken ct = default) => AddCommentForRowAsync(fileId, rowId, "asset-rows", request, ct);
    public Task<ReviewThread> EditCommentAsync(string fileId, string rowId, string commentId, EditReviewCommentRequest request, CancellationToken ct = default) => EditCommentForRowAsync(fileId, rowId, "asset-rows", commentId, request, ct);
    public Task<ReviewThread> DeleteCommentAsync(string fileId, string rowId, string commentId, long expectedVersion, CancellationToken ct = default) => DeleteCommentForRowAsync(fileId, rowId, "asset-rows", commentId, expectedVersion, ct);
}

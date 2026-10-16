using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
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
    private readonly IProjectFileTransfer? _imageTransfer;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private sealed record ApiError(string Code, string Message);
    private sealed record ErrorBody(ApiError? Error = null, string? Message = null);
    private sealed record ItemsEnvelope<T>(List<T> Items);
    public event Action<ProjectTransferTaskSnapshot>? TransferUpdated
    {
        add { if (_imageTransfer is not null) _imageTransfer.TaskUpdated += value; }
        remove { if (_imageTransfer is not null) _imageTransfer.TaskUpdated -= value; }
    }
    public ProjectTransferTaskSnapshot? GetTransferTaskSnapshot(string operationId) =>
        _imageTransfer?.GetTaskSnapshot(operationId);
    public IReadOnlyList<ProjectTransferTaskSnapshot> GetTransferTaskSnapshots() =>
        _imageTransfer?.GetTaskSnapshots() ?? Array.Empty<ProjectTransferTaskSnapshot>();
    public string OperationScope
    {
        get
        {
            var actor = _getAccountId?.Invoke();
            if (string.IsNullOrWhiteSpace(actor) || actor.Any(char.IsControl))
                throw new InvalidDataException("宿主必须配置当前登录账号的稳定标识，才能安全保存和恢复创建操作。");
            LimitedText(actor, 512);
            // Only this digest goes to disk. Never persist a token or expose the underlying account ID.
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_root.AbsoluteUri + "\n" + actor)));
        }
    }

    public HttpProjectWorkbookService(HttpClient client, Uri apiRoot, Func<string> getBearerToken,
        WorkbookTransportOptions? options = null, Func<string>? getAccountId = null,
        IProjectFileTransfer? imageTransfer = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _getBearerToken = getBearerToken ?? throw new ArgumentNullException(nameof(getBearerToken));
        _limits = options ?? new WorkbookTransportOptions(); _limits.Validate();
        _getAccountId = getAccountId;
        _imageTransfer = imageTransfer;
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
        if (value is "create" or "image-assets" or "projects") throw new InvalidDataException("工作簿标识与保留路径冲突。");
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
    private static void ImageType(string mediaType)
    {
        if (mediaType is not ("image/png" or "image/jpeg" or "image/webp")) throw new InvalidDataException("不支持该图片类型。");
    }
    private static void VideoType(string mediaType)
    {
        if (mediaType is not ("video/mp4" or "video/quicktime" or "video/x-matroska" or "video/webm"
            or "video/x-msvideo" or "video/x-flv" or "video/x-ms-wmv"))
            throw new InvalidDataException("不支持该视频类型。");
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
    private static void AssetComponent(string? value, string label)
    {
        LimitedText(value, 200);
        if (value == null) return;
        if (value is "." or ".." || value.IndexOfAny(['/', '\\']) >= 0
            || value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 200)
            throw new InvalidDataException($"{label}不能包含路径分隔符或控制字符，且最多 200 字节。");
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
        var (code, message) = await ReadErrorDetailsAsync(response, ct);
        var status = (int)response.StatusCode;
        throw new WorkbookApiException(status, code ?? "HTTP_ERROR", message ?? $"请求失败（{status}）。");
    }

    private static async Task<(string? Code, string? Message)> ReadErrorDetailsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var bytes = await BytesAsync(response.Content, 64 * 1024, ct);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            string? code = null;
            string? message = null;

            if (root.TryGetProperty("code", out var codeElem) && codeElem.ValueKind == JsonValueKind.String)
            {
                code = codeElem.GetString();
            }

            if (root.TryGetProperty("error", out var errorElem))
            {
                if (errorElem.ValueKind == JsonValueKind.String)
                {
                    message = errorElem.GetString();
                }
                else if (errorElem.ValueKind == JsonValueKind.Object)
                {
                    if (errorElem.TryGetProperty("code", out var nestedCode) && nestedCode.ValueKind == JsonValueKind.String)
                    {
                        code ??= nestedCode.GetString();
                    }
                    if (errorElem.TryGetProperty("message", out var nestedMsg) && nestedMsg.ValueKind == JsonValueKind.String)
                    {
                        message = nestedMsg.GetString();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(message) && root.TryGetProperty("message", out var msgElem) && msgElem.ValueKind == JsonValueKind.String)
            {
                message = msgElem.GetString();
            }

            if (string.IsNullOrWhiteSpace(message) && root.TryGetProperty("detail", out var detailElem) && detailElem.ValueKind == JsonValueKind.String)
            {
                message = detailElem.GetString();
            }

            return (code, message);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var (_, message) = await ReadErrorDetailsAsync(response, ct);
        return message;
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

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct = default) => ListAsync<ProjectInfo>("projects", ct);
    public Task<IReadOnlyList<ProjectFile>> ListProjectFilesAsync(string projectId, CancellationToken ct = default) => ListAsync<ProjectFile>("?project_id=" + Id(projectId), ct);
    public async IAsyncEnumerable<ProjectChangeEvent> SubscribeProjectEventsAsync(string projectId,
        long? afterSequence = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var route = "events?project_id=" + Id(projectId);
        if (afterSequence.HasValue)
        {
            if (afterSequence.Value < 0) throw new InvalidDataException("实时更新游标无效。");
            route += "&after=" + afterSequence.Value.ToString(CultureInfo.InvariantCulture);
        }
        using var request = Request(HttpMethod.Get, route, null);
        request.Headers.Accept.Clear(); request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                if (eventName == "ready" && data.Length > 0)
                {
                    using var document = JsonDocument.Parse(data.ToString());
                    var sequence = document.RootElement.GetProperty("sequence").GetInt64();
                    yield return new ProjectChangeEvent(sequence, projectId, "", "", "", [], EventKind: "ready");
                }
                else if (eventName == "change" && data.Length > 0)
                {
                    var change = JsonSerializer.Deserialize<ProjectChangeEvent>(data.ToString(), Json)
                        ?? throw new InvalidDataException("实时更新消息为空。");
                    yield return change with { EventKind = "change" };
                }
                eventName = null;
                data.Clear();
                continue;
            }
            if (line[0] == ':') continue;
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
    }
    public Task<ProjectFile> CreateInProjectAsync(string projectId, string templateKind, string operationId, string? name = null, CancellationToken ct = default)
    {
        _ = OperationScope;
        Template(templateKind); _ = Id(projectId); _ = Id(operationId);
        object body = string.IsNullOrWhiteSpace(name)
            ? new { template_kind = templateKind, project_id = projectId, operation_id = operationId }
            : new { template_kind = templateKind, project_id = projectId, operation_id = operationId, name = name.Trim() };
        return ReadAsync<ProjectFile>(HttpMethod.Post, "create", JsonBody(body), ct);
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
    public async Task<ImageAsset> UploadProjectImageAsync(string projectId, string fileName, string mediaType, Stream source, string? folderPath = null, CancellationToken ct = default, string? operationId = null)
    {
        ImageType(mediaType); _ = Id(projectId);
        if (_imageTransfer is null)
            throw new WorkbookApiException(409, "TRANSFER_REQUIRED", "项目图片必须通过本地传输代理上传。");
        ArgumentNullException.ThrowIfNull(source);
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName.Any(char.IsControl) || safeName.Contains('/') || safeName.Contains('\\'))
            throw new InvalidDataException("文件名无效。");
        var tempSuffix = Guid.TryParse(operationId, out var operationGuid) ? operationGuid.ToString("N") : Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(Path.GetTempPath(), "muying-image-" + tempSuffix + Path.GetExtension(safeName));
        var keepForRetry = false;
        try
        {
            if (!File.Exists(temporary))
            {
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                long copied = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (count == 0) break;
                    copied += count;
                    if (copied > _limits.MaxUploadBytes) throw new InvalidDataException("图片超过当前允许的上传大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            var logicalPath = string.IsNullOrWhiteSpace(folderPath)
                ? safeName : folderPath.Trim('/', '\\') + "/" + safeName;
            ProjectFileTransferResult uploaded;
            try
            {
                uploaded = await _imageTransfer.UploadAsync(projectId, safeName, mediaType, temporary, logicalPath, ct, operationId)
                    .ConfigureAwait(false);
            }
            catch
            {
                keepForRetry = operationId is not null;
                throw;
            }
            return new ImageAsset(uploaded.FileId, Path.GetFileNameWithoutExtension(safeName), mediaType, 0, 0)
            {
                FileId = uploaded.FileId,
                CurrentRevisionId = uploaded.RevisionId,
                LogicalPath = uploaded.LogicalPath,
                ProjectId = projectId,
                TransferOperationId = uploaded.OperationId
            };
        }
        finally { if (!keepForRetry) try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }
    public async Task<ProjectFileTransferResult> UploadProjectFileAsync(string projectId, string fileName,
        string mediaType, string localPath, string logicalPath, CancellationToken ct = default, string? operationId = null)
    {
        VideoType(mediaType);
        _ = Id(projectId);
        if (_imageTransfer is null)
            throw new WorkbookApiException(409, "TRANSFER_REQUIRED", "项目文件必须通过本地传输代理上传。");
        return await _imageTransfer.UploadAsync(projectId, Path.GetFileName(fileName), mediaType,
            localPath, logicalPath, ct, operationId).ConfigureAwait(false);
    }
    public async Task<bool> CancelProjectTransferAsync(string operationId)
    {
        if (_imageTransfer is null) return false;
        var canceled = await _imageTransfer.CancelAsync(operationId).ConfigureAwait(false);
        if (!canceled || !Guid.TryParseExact(operationId, "D", out var operationGuid)) return canceled;
        var prefix = "muying-image-" + operationGuid.ToString("N");
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), prefix + ".*"))
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return true;
    }

    public async Task<string> DownloadProjectFileAsync(string fileId, string? revisionId, string fileName,
        CancellationToken ct = default, string? projectId = null)
    {
        _ = Id(fileId);
        if (_imageTransfer is null)
            throw new WorkbookApiException(409, "TRANSFER_REQUIRED", "项目文件必须通过本地传输代理下载。");
        return await _imageTransfer.DownloadToLocalAsync(fileId, revisionId, fileName, ct,
            projectId is null ? null : $"project:{projectId}:scene-video", projectId).ConfigureAwait(false);
    }
    public async Task DeleteProjectFileAsync(string fileId, long expectedVersion, CancellationToken ct = default)
    {
        _ = Id(fileId); Version(expectedVersion);
        if (_imageTransfer is null)
            throw new WorkbookApiException(409, "TRANSFER_REQUIRED", "项目文件必须通过本地传输代理管理。");
        await _imageTransfer.DeleteAsync(fileId, expectedVersion, ct).ConfigureAwait(false);
    }
    public Task<ImageAsset> GetProjectImageAsync(string imageId, CancellationToken ct = default) =>
        ReadAsync<ImageAsset>(HttpMethod.Get, "image-assets/" + Id(imageId), null, ct);
    public Task<ImageAsset> RenameImageAsync(string imageId, long expectedVersion, string displayName, CancellationToken ct = default)
    {
        _ = Id(imageId); Version(expectedVersion); LimitedText(displayName, 200, nonEmpty: true);
        if (displayName.IndexOfAny(['/', '\\']) >= 0 || displayName.Any(char.IsControl))
            throw new InvalidDataException("图片显示名称不能包含路径分隔符或控制字符。");
        return ReadAsync<ImageAsset>(HttpMethod.Put, "image-assets/" + Id(imageId) + "/name",
            JsonBody(new { expected_version = expectedVersion, display_name = displayName }), ct);
    }
    public Task<AddSheetResponse> AddSheetAsync(string fileId, string sheetName, CancellationToken ct = default)
    {
        LimitedText(sheetName, 31, nonEmpty: true);
        return ReadAsync<AddSheetResponse>(HttpMethod.Post, FileId(fileId) + "/sheets", JsonBody(new { sheet_name = sheetName }), ct);
    }
    public Task<ProjectFile> RenameWorkbookAsync(string fileId, long expectedNameVersion, string displayName, CancellationToken ct = default)
    {
        Version(expectedNameVersion); LimitedText(displayName, 128, nonEmpty: true);
        return ReadAsync<ProjectFile>(HttpMethod.Put, FileId(fileId) + "/name",
            JsonBody(new { expected_name_version = expectedNameVersion, display_name = displayName }), ct);
    }
    public async Task DeleteWorkbookAsync(string fileId, CancellationToken ct = default)
    {
        await ReadAsync<JsonElement>(HttpMethod.Delete, FileId(fileId), null, ct);
    }
    public Task<SceneRow> CreateSceneRowAsync(string fileId, string sheetId, CancellationToken ct = default) =>
        ReadAsync<SceneRow>(HttpMethod.Post, FileId(fileId) + "/scene-rows", JsonBody(new { sheet_id = Id(sheetId) }), ct);
    public Task<AssetRow> CreateAssetRowAsync(string fileId, string sheetId, CancellationToken ct = default) =>
        ReadAsync<AssetRow>(HttpMethod.Post, FileId(fileId) + "/asset-rows", JsonBody(new { sheet_id = Id(sheetId) }), ct);
    public async Task DeleteRowAsync(string fileId, string rowId, string rowKind, long expectedVersion, CancellationToken ct = default)
    {
        Version(expectedVersion);
        await ReadAsync<JsonElement>(HttpMethod.Delete, RowRoute(fileId, rowId, rowKind),
            JsonBody(new { expected_version = expectedVersion }), ct);
    }
    public Task<SceneRow> UpdateSceneRowAsync(string fileId, string rowId, UpdateSceneRowRequest request, CancellationToken ct = default)
    {
        if (request.LabelLevel is not (null or "红" or "黄" or "绿")
            || (request.ClearLabelLevel && request.LabelLevel != null))
            throw new InvalidDataException("等级仅允许红、黄、绿或空。");

        var dict = new Dictionary<string, object?>();
        if (request.ExpectedVersions != null) dict["expected_versions"] = request.ExpectedVersions;
        else if (request.ExpectedVersion.HasValue) dict["expected_version"] = request.ExpectedVersion.Value;
        if (request.SceneNumber != null) dict["scene_number"] = request.SceneNumber;
        if (request.BeatNumber != null) dict["beat_number"] = request.BeatNumber;
        if (request.StoryContent != null) dict["story_content"] = request.StoryContent;
        if (request.ClearLabelLevel) dict["label_level"] = null;
        else if (request.LabelLevel != null) dict["label_level"] = request.LabelLevel;
        if (request.BreakdownPlan != null) dict["breakdown_plan"] = request.BreakdownPlan;
        if (request.Note != null) dict["note"] = request.Note;
        if (request.VideoAction != null) dict["video_action"] = request.VideoAction;
        if (request.Video != null) dict["video"] = request.Video;
        if (request.VideoId != null) dict["video_id"] = request.VideoId;
        if (request.VideoName != null) dict["video_name"] = request.VideoName;

        return ReadAsync<SceneRow>(HttpMethod.Put, RowRoute(fileId, rowId, "scene-rows"), JsonBody(dict, 256 * 1024), ct);
    }
    public Task<SceneRow> UpdateStoryContentAsync(string fileId, string rowId, UpdateStoryContentRequest request, CancellationToken ct = default)
    {
        Version(request.ExpectedVersion); LimitedText(request.StoryContent, 20000);
        return ReadAsync<SceneRow>(HttpMethod.Put, RowRoute(fileId, rowId, "scene-rows") + "/story-content",
            JsonBody(request, 256 * 1024), ct);
    }
    public Task<AssetRow> UpdateAssetRowAsync(string fileId, string rowId, UpdateAssetRowRequest request, CancellationToken ct = default)
    {
        Version(request.ExpectedVersion);
        AssetComponent(request.Category, "类别"); AssetComponent(request.Name, "名称");
        return ReadAsync<AssetRow>(HttpMethod.Put, RowRoute(fileId, rowId, "asset-rows"), JsonBody(request), ct);
    }
    public Task<WorkbookDocument> OpenAsync(string fileId, CancellationToken ct = default) => ReadAsync<WorkbookDocument>(HttpMethod.Get, FileId(fileId), null, ct);
    public async Task<byte[]> GetImageContentAsync(string assetId, bool thumbnail, CancellationToken ct = default)
    {
        if (_imageTransfer is null)
            throw new WorkbookApiException(409, "TRANSFER_REQUIRED", "项目图片必须通过本地传输代理下载。");
        var metadata = await GetProjectImageAsync(assetId, ct).ConfigureAwait(false);
        var fileId = metadata.FileId ?? metadata.Id;
        return await _imageTransfer.DownloadImageAsync(fileId, metadata.CurrentRevisionId, thumbnail, ct).ConfigureAwait(false);
    }
    public Task<AssetRow> UpdateImagesAsync(string fileId, string rowId, UpdateImagesRequest request, CancellationToken ct = default)
    {
        Version(request.ExpectedVersion);
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
}

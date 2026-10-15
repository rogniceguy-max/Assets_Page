using System.IO;

namespace MuYingDrive.ProjectWorkbooks;

public sealed record ProjectInfo(string Id, string Name)
{
    public override string ToString() => Name;
}
public sealed record ProjectFile(string Id, string Name, string TemplateKind, long Version)
{
    public long NameVersion { get; init; } = 1;
    public string? ProjectId { get; init; }
    public string? RevisionOf { get; init; }
    public string? RevisionLabel { get; init; }
}
public sealed record ImageAsset(string Id, string Name, string MediaType, int Width, int Height)
{
    public long Version { get; init; } = 1;
    public string? ProjectId { get; init; }
    public string? Category { get; init; }
    public string? Variant { get; init; }
    public string? RevisionLabel { get; init; }
}
public sealed record ImageSearchPage(List<ImageAsset> Items, string? NextCursor);
public sealed record AssetRow(string Id, long Version, string? Category, string? Name, string? Appearance,
    ImageAsset? SelectedImage, List<ImageAsset> CandidateImages)
{
    public string? Variant { get; init; }
    public string? AssetFolderPath { get; init; }
    public Dictionary<string, long> FieldVersions { get; init; } = [];
    public bool CanEdit { get; init; }
}
public sealed record VideoItem(string? Id, string Name, string? Path, string? Duration = null, string? ThumbUrl = null, string? DiscardedAt = null);
public sealed record LegacyNote(string Label, string? Text, string? SourceCell);
public sealed record SceneOrigin(string FileId, string RowId);
public sealed record SceneRow(string Id, long Version, string? SceneNumber, string? BeatNumber,
    string? StoryContent, string? LabelLevel, string? BreakdownPlan)
{
    public Dictionary<string, long> FieldVersions { get; init; } = [];
    public List<LegacyNote> LegacyNotes { get; init; } = [];
    public string? SceneContext { get; init; }
    public string? CastContext { get; init; }
    public List<SceneOrigin> Origins { get; init; } = [];
    public bool CanEdit { get; init; }
    public string? Note { get; init; }
    public VideoItem? Video { get; init; }
    public List<VideoItem> Videos { get; init; } = [];
    public VideoItem? FinalVideo { get; init; }
    public List<VideoItem> DiscardedVideos { get; init; } = [];
}
public sealed record GenericColumn(string Key, string Label);
public sealed record GenericRow(string Id, Dictionary<string, string?> Cells);
public sealed record WorkbookSheet(string Id, string Name, string Kind, List<AssetRow> AssetRows,
    List<SceneRow> SceneRows, List<GenericColumn> Columns, List<GenericRow> GenericRows)
{
    public long NameVersion { get; init; } = 1;
}
public sealed record WorkbookDocument(ProjectFile File, List<WorkbookSheet> Sheets, string Fidelity, List<string> Notices);
public sealed record UpdateImagesRequest(long ExpectedVersion, string? SelectedImageId, List<string> CandidateImageIds,
    string? SelectionReason = null);
public sealed record UpdateSceneRequest(long ExpectedVersion, string? LabelLevel, string? BreakdownPlan)
{
    public Dictionary<string, long>? ExpectedVersions { get; init; }
}
public sealed record UpdateSceneRowRequest(
    long? ExpectedVersion = null,
    Dictionary<string, long>? ExpectedVersions = null,
    string? SceneNumber = null,
    string? BeatNumber = null,
    string? StoryContent = null,
    string? LabelLevel = null,
    string? BreakdownPlan = null,
    string? Note = null,
    string? VideoAction = null,
    VideoItem? Video = null,
    string? VideoId = null,
    string? VideoName = null
);
public sealed record UpdateStoryContentRequest(long ExpectedVersion, string? StoryContent);
public sealed record UpdateAssetRowRequest(long ExpectedVersion, string? Category, string? Name);
public sealed record UpdateSceneOriginsRequest(long ExpectedVersion, List<SceneOrigin> Origins);
public sealed record SelectionHistoryEntry(string Id, string? PreviousImageId, string? ImageId,
    string UserName, DateTimeOffset CreatedAt, string? Reason);
public sealed record ReviewSummary(string? ImageId, int Good, int Bad, int CommentCount, string? MyVerdict, string? LatestComment = null, string? LatestUser = null);
public sealed record ReviewComment(string Id, string UserId, string UserName, string Text, long Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool CanEdit, bool CanDelete)
{
    public string CreatedAtText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "（本地时间）";
    public bool Resolved { get; init; }
    public string? ResolutionNote { get; init; }
    public bool CanResolve { get; init; }
}
public sealed record ReviewThread(string? ImageId, int Good, int Bad, int CommentCount,
    string? MyVerdict, List<ReviewComment> Comments);
public sealed record ReviewVoteRequest(string ImageId, string? Verdict);
public sealed record AddReviewCommentRequest(string? ImageId, string Text);
public sealed record EditReviewCommentRequest(long ExpectedVersion, string Text);
public sealed record ResolveCommentRequest(long ExpectedVersion, bool Resolved, string? ResolutionNote);
public sealed record AddSheetResponse(bool Ok, string SheetName, string? FileName = null, string? Id = null);
public sealed record ProjectChangeEvent(long Sequence, string ProjectId, string WorkbookId, string EntityKind,
    string EntityId, List<string> ChangedFields, string? CreatedAt = null, string? EventKind = null);
public sealed record LinkedAsset(string Category, string Name, string? Appearance, ImageAsset? Image);

public interface IProjectWorkbookService
{
    long MaximumUploadBytes { get; }
    string OperationScope { get; }
    Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProjectFile>> ListProjectFilesAsync(string projectId, CancellationToken ct = default);
    IAsyncEnumerable<ProjectChangeEvent> SubscribeProjectEventsAsync(string projectId, long? afterSequence = null, CancellationToken ct = default);
    Task<ProjectFile> CreateInProjectAsync(string projectId, string templateKind, string operationId, string? name = null, CancellationToken ct = default);
    Task<ProjectFile> ImportRevisionAsync(string projectId, string fileName, Stream source, string operationId,
        string? revisionOf = null, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectFile>> ListRevisionsAsync(string fileId, CancellationToken ct = default);
    Task DownloadOriginalToAsync(string fileId, string destination, CancellationToken ct = default);
    Task ExportCurrentAsync(string fileId, long expectedVersion, string destination, CancellationToken ct = default);
    Task<ImageSearchPage> SearchImagesAsync(string projectId, string? q = null, string? category = null,
        string? cursor = null, int pageSize = 40, CancellationToken ct = default);
    Task<ImageAsset> GetProjectImageAsync(string imageId, CancellationToken ct = default);
    Task<ImageAsset> UploadProjectImageAsync(string projectId, string fileName, string mediaType, Stream source, CancellationToken ct = default);
    Task<ImageAsset> RenameImageAsync(string imageId, long expectedVersion, string displayName, CancellationToken ct = default);
    Task<AddSheetResponse> AddSheetAsync(string fileId, string sheetName, CancellationToken ct = default);
    Task<ProjectFile> RenameWorkbookAsync(string fileId, long expectedNameVersion, string displayName, CancellationToken ct = default);
    Task DeleteWorkbookAsync(string fileId, CancellationToken ct = default);
    Task RenameSheetAsync(string fileId, string sheetId, long expectedNameVersion, string displayName, CancellationToken ct = default);
    Task<SceneRow> CreateSceneRowAsync(string fileId, string sheetId, CancellationToken ct = default);
    Task<AssetRow> CreateAssetRowAsync(string fileId, string sheetId, CancellationToken ct = default);
    Task DeleteRowAsync(string fileId, string rowId, string rowKind, long expectedVersion, CancellationToken ct = default);
    Task<SceneRow> UpdateSceneAsync(string fileId, string rowId, UpdateSceneRequest request, CancellationToken ct = default);
    Task<SceneRow> UpdateSceneRowAsync(string fileId, string rowId, UpdateSceneRowRequest request, CancellationToken ct = default);
    Task<SceneRow> UpdateStoryContentAsync(string fileId, string rowId, UpdateStoryContentRequest request, CancellationToken ct = default);
    Task<AssetRow> UpdateAssetRowAsync(string fileId, string rowId, UpdateAssetRowRequest request, CancellationToken ct = default);
    Task<SceneRow> UpdateSceneOriginsAsync(string fileId, string rowId, UpdateSceneOriginsRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SelectionHistoryEntry>> GetSelectionHistoryAsync(string fileId, string rowId, CancellationToken ct = default);
    Task<IReadOnlyList<ReviewSummary>> GetReviewSummariesForRowAsync(string fileId, string rowId, string rowKind, CancellationToken ct = default);
    Task<ReviewThread> GetReviewThreadForRowAsync(string fileId, string rowId, string rowKind, string? imageId, CancellationToken ct = default);
    Task<ReviewThread> AddCommentForRowAsync(string fileId, string rowId, string rowKind, AddReviewCommentRequest request, CancellationToken ct = default);
    Task<ReviewThread> EditCommentForRowAsync(string fileId, string rowId, string rowKind, string commentId, EditReviewCommentRequest request, CancellationToken ct = default);
    Task<ReviewThread> DeleteCommentForRowAsync(string fileId, string rowId, string rowKind, string commentId, long expectedVersion, CancellationToken ct = default);
    Task<ReviewThread> ResolveCommentAsync(string fileId, string rowId, string rowKind, string commentId, ResolveCommentRequest request, CancellationToken ct = default);

    // Legacy v2 signatures remain for host migration only. The v3 UI always supplies project scope.
    Task<IReadOnlyList<ProjectFile>> ListAsync(CancellationToken ct = default);
    Task<ProjectFile> CreateAsync(string templateKind, CancellationToken ct = default);
    Task<ProjectFile> ImportAsync(string fileName, Stream source, CancellationToken ct = default);
    Task<WorkbookDocument> OpenAsync(string fileId, CancellationToken ct = default);
    Task<byte[]> DownloadOriginalAsync(string fileId, CancellationToken ct = default);
    Task<IReadOnlyList<ImageAsset>> ListImagesAsync(CancellationToken ct = default);
    Task<ImageAsset> UploadImageAsync(string fileName, string mediaType, Stream source, CancellationToken ct = default);
    Task<byte[]> GetImageContentAsync(string assetId, bool thumbnail, CancellationToken ct = default);
    Task<AssetRow> UpdateImagesAsync(string fileId, string rowId, UpdateImagesRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ReviewSummary>> GetReviewSummariesAsync(string fileId, string rowId, CancellationToken ct = default);
    Task<ReviewThread> GetReviewThreadAsync(string fileId, string rowId, string? imageId, CancellationToken ct = default);
    Task<ReviewThread> VoteAsync(string fileId, string rowId, ReviewVoteRequest request, CancellationToken ct = default);
    Task<ReviewThread> AddCommentAsync(string fileId, string rowId, AddReviewCommentRequest request, CancellationToken ct = default);
    Task<ReviewThread> EditCommentAsync(string fileId, string rowId, string commentId, EditReviewCommentRequest request, CancellationToken ct = default);
    Task<ReviewThread> DeleteCommentAsync(string fileId, string rowId, string commentId, long expectedVersion, CancellationToken ct = default);
}

public sealed class WorkbookApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

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
    public override string ToString() => Name;
}
public sealed record ImageAsset(string Id, string Name, string MediaType, int Width, int Height)
{
    public string? FileId { get; init; }
    public string? CurrentRevisionId { get; init; }
    public string? LogicalPath { get; init; }
    public long Version { get; init; } = 1;
    public string? ProjectId { get; init; }
    public string? Category { get; init; }
    public string? Variant { get; init; }
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
public sealed record VideoItem(string? Id, string Name, string? Path, string? Duration = null, string? ThumbUrl = null, string? DiscardedAt = null)
{
    public string? FileId { get; init; }
}
public sealed record SceneRow(string Id, long Version, string? SceneNumber, string? BeatNumber,
    string? StoryContent, string? LabelLevel, string? BreakdownPlan)
{
    public Dictionary<string, long> FieldVersions { get; init; } = [];
    public string? SceneContext { get; init; }
    public string? CastContext { get; init; }
    public bool CanEdit { get; init; }
    public string? Note { get; init; }
    public VideoItem? Video { get; init; }
    public List<VideoItem> Videos { get; init; } = [];
    public List<VideoItem> DiscardedVideos { get; init; } = [];
}
public sealed record WorkbookSheet(string Id, string Name, string Kind, List<AssetRow> AssetRows,
    List<SceneRow> SceneRows);
public sealed record WorkbookDocument(ProjectFile File, List<WorkbookSheet> Sheets);
public sealed record UpdateImagesRequest(long ExpectedVersion, string? SelectedImageId, List<string> CandidateImageIds);
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
    string? VideoName = null,
    bool ClearLabelLevel = false
);
public sealed record UpdateStoryContentRequest(long ExpectedVersion, string? StoryContent);
public sealed record UpdateAssetRowRequest(long ExpectedVersion, string? Category, string? Name);
public sealed record ReviewSummary(string? ImageId, int Good, int Bad, int CommentCount, string? MyVerdict, string? LatestComment = null, string? LatestUser = null);
public sealed record ReviewComment(string Id, string UserId, string UserName, string Text, long Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool CanEdit, bool CanDelete)
{
    public string CreatedAtText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "（本地时间）";
}
public sealed record ReviewThread(string? ImageId, int Good, int Bad, int CommentCount,
    string? MyVerdict, List<ReviewComment> Comments);
public sealed record ReviewVoteRequest(string ImageId, string? Verdict);
public sealed record AddReviewCommentRequest(string? ImageId, string Text);
public sealed record EditReviewCommentRequest(long ExpectedVersion, string Text);
public sealed record AddSheetResponse(bool Ok, string SheetName, string? FileName = null, string? Id = null);
public sealed record ProjectChangeEvent(long Sequence, string ProjectId, string WorkbookId, string EntityKind,
    string EntityId, List<string> ChangedFields, string? CreatedAt = null, string? EventKind = null);
public sealed record LinkedAsset(string Category, string Name, string? Appearance, ImageAsset? Image);

public interface IProjectWorkbookService
{
    string OperationScope { get; }
    Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProjectFile>> ListProjectFilesAsync(string projectId, CancellationToken ct = default);
    IAsyncEnumerable<ProjectChangeEvent> SubscribeProjectEventsAsync(string projectId, long? afterSequence = null, CancellationToken ct = default);
    Task<ProjectFile> CreateInProjectAsync(string projectId, string templateKind, string operationId, string? name = null, CancellationToken ct = default);
    Task<ImageSearchPage> SearchImagesAsync(string projectId, string? q = null, string? category = null,
        string? cursor = null, int pageSize = 40, CancellationToken ct = default);
    Task<ImageAsset> GetProjectImageAsync(string imageId, CancellationToken ct = default);
    Task<ImageAsset> UploadProjectImageAsync(string projectId, string fileName, string mediaType, Stream source, string? folderPath = null, CancellationToken ct = default);
    Task<ProjectFileTransferResult> UploadProjectFileAsync(string projectId, string fileName, string mediaType,
        string localPath, string logicalPath, CancellationToken ct = default);
    Task<string> DownloadProjectFileAsync(string fileId, string? revisionId, string fileName, CancellationToken ct = default);
    Task DeleteProjectFileAsync(string fileId, long expectedVersion, CancellationToken ct = default);
    Task<ImageAsset> RenameImageAsync(string imageId, long expectedVersion, string displayName, CancellationToken ct = default);
    Task<AddSheetResponse> AddSheetAsync(string fileId, string sheetName, CancellationToken ct = default);
    Task<ProjectFile> RenameWorkbookAsync(string fileId, long expectedNameVersion, string displayName, CancellationToken ct = default);
    Task DeleteWorkbookAsync(string fileId, CancellationToken ct = default);
    Task<SceneRow> CreateSceneRowAsync(string fileId, string sheetId, CancellationToken ct = default);
    Task<AssetRow> CreateAssetRowAsync(string fileId, string sheetId, CancellationToken ct = default);
    Task DeleteRowAsync(string fileId, string rowId, string rowKind, long expectedVersion, CancellationToken ct = default);
    Task<SceneRow> UpdateSceneRowAsync(string fileId, string rowId, UpdateSceneRowRequest request, CancellationToken ct = default);
    Task<SceneRow> UpdateStoryContentAsync(string fileId, string rowId, UpdateStoryContentRequest request, CancellationToken ct = default);
    Task<AssetRow> UpdateAssetRowAsync(string fileId, string rowId, UpdateAssetRowRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ReviewSummary>> GetReviewSummariesForRowAsync(string fileId, string rowId, string rowKind, CancellationToken ct = default);
    Task<ReviewThread> GetReviewThreadForRowAsync(string fileId, string rowId, string rowKind, string? imageId, CancellationToken ct = default);
    Task<ReviewThread> AddCommentForRowAsync(string fileId, string rowId, string rowKind, AddReviewCommentRequest request, CancellationToken ct = default);
    Task<ReviewThread> EditCommentForRowAsync(string fileId, string rowId, string rowKind, string commentId, EditReviewCommentRequest request, CancellationToken ct = default);
    Task<ReviewThread> DeleteCommentForRowAsync(string fileId, string rowId, string rowKind, string commentId, long expectedVersion, CancellationToken ct = default);

    Task<WorkbookDocument> OpenAsync(string fileId, CancellationToken ct = default);
    Task<byte[]> GetImageContentAsync(string assetId, bool thumbnail, CancellationToken ct = default);
    Task<AssetRow> UpdateImagesAsync(string fileId, string rowId, UpdateImagesRequest request, CancellationToken ct = default);
    Task<ReviewThread> VoteAsync(string fileId, string rowId, ReviewVoteRequest request, CancellationToken ct = default);
}

/// <summary>Host-provided bridge from project image metadata to the versioned file service.</summary>
public sealed record ProjectFileTransferResult(string FileId, string RevisionId, string LogicalPath, long Size, string MediaType, long Version);

public interface IProjectFileTransfer
{
    Task<ProjectFileTransferResult> UploadAsync(string projectId, string fileName, string mediaType,
        string localPath, string logicalPath, CancellationToken cancellationToken = default);
    Task<string> DownloadToLocalAsync(string fileId, string? revisionId, string fileName,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(string fileId, long expectedVersion, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadImageAsync(string fileId, string? revisionId, bool thumbnail,
        CancellationToken cancellationToken = default);
}

public sealed class WorkbookApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MuYingDrive.ProjectWorkbooks;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class ImageItem(ImageAsset asset, ImageSource? preview, string? error = null) : ObservableModel
{
    public ImageAsset Asset { get; private set; } = asset;
    private ImageSource? _preview = preview;
    public ImageSource? Preview { get => _preview; private set { _preview = value; Changed(); } }
    public string Name => Asset.Name;
    public string Details => string.Join(" · ", new[]
    {
        Asset.Category,
        Asset.Variant,
        Asset.Version > 1 ? $"版本 {Asset.Version}" : null
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    private string? _error = error;
    public string? Error { get => _error; private set { _error = value; Changed(); } }
    internal bool Loading { get; set; }
    public void SetPreview(ImageSource? source, string? error = null) { Preview = source; Error = error; }
    public void UpdateAsset(ImageAsset asset)
    {
        var revisionChanged = !string.Equals(Asset.CurrentRevisionId, asset.CurrentRevisionId, StringComparison.Ordinal);
        Asset = asset;
        if (revisionChanged)
        {
            Preview = null;
            Error = null;
        }
        Changed(nameof(Asset)); Changed(nameof(Name)); Changed(nameof(Details));
    }
    private ReviewSummary? _summary;
    public string SummaryText => _summary is { } summary
        ? $"好 {summary.Good} · 不好 {summary.Bad} · 评论 {summary.CommentCount}" : "好 ? · 不好 ? · 评论 ?";
    public void SetSummary(ReviewSummary? summary) { _summary = summary; Changed(nameof(SummaryText)); }
}

/// <summary>Local-first upload card whose completed state includes the workbook link.</summary>
public sealed class WorkbookUploadCard(string localPath, bool isImage, AssetRowItem? assetRow = null,
    SceneRowItem? sceneRow = null) : ObservableModel
{
    private ImageSource? _preview;
    private string _state = "准备上传";
    private string? _error;
    private string? _operationId;
    private string? _fileId;
    private ProjectTransferTaskSnapshot? _transfer;

    public string LocalPath { get; } = Path.GetFullPath(localPath);
    public string DisplayName { get; } = Path.GetFileName(localPath);
    public string? OperationId { get => _operationId; set { _operationId = value; Changed(); } }
    public bool IsImage { get; } = isImage;
    public bool IsVideo => !IsImage;
    public bool SelectedTarget { get; set; }
    public AssetRowItem? AssetRow { get; } = assetRow;
    public SceneRowItem? SceneRow { get; } = sceneRow;
    public ImageSource? Preview { get => _preview; set { _preview = value; Changed(); Changed(nameof(HasPreview)); } }
    public string State { get => _state; set { _state = value; Changed(); Changed(nameof(IsBusy)); Changed(nameof(CanRetry)); } }
    public string? Error { get => _error; set { _error = value; Changed(); Changed(nameof(HasError)); } }
    public string? FileId { get => _fileId; set { _fileId = value; Changed(); Changed(nameof(HasFileId)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasFileId => !string.IsNullOrWhiteSpace(FileId);
    public bool HasPreview => Preview is not null;
    public bool IsBusy => State is "正在上传" or "正在关联";
    public ProjectTransferTaskSnapshot? Transfer
    {
        get => _transfer;
        private set
        {
            _transfer = value;
            Changed(); Changed(nameof(Progress)); Changed(nameof(ProgressLabel)); Changed(nameof(HasProgress));
            Changed(nameof(CanRetry)); Changed(nameof(CanCancel));
        }
    }
    public double Progress => Transfer is { BytesTotal: > 0 } transfer
        ? Math.Clamp((double)Math.Max(0, transfer.BytesDone) / transfer.BytesTotal.Value * 100, 0, 100)
        : Transfer?.State == "completed" ? 100 : 0;
    public bool HasProgress => Transfer?.BytesTotal is > 0;
    public string ProgressLabel => HasProgress
        ? $"{Transfer!.BytesDone:N0} / {Transfer.BytesTotal:N0} 字节 · {Progress:0}%"
        : Transfer is null ? string.Empty : Transfer.State switch
        {
            "queued" => "排队中",
            "hashing" => "正在校验",
            "verifying" => "正在校验传输",
            "committing" => "正在提交",
            "completed" => "文件传输已提交",
            "paused" => "已暂停",
            "waiting_network" or "offline" => "等待网络",
            _ => string.Empty
        };
    public bool CanRetry => !IsBusy && (Transfer is { } transfer
        ? transfer.Retryable || transfer.State is "paused" or "completed"
        : UploadedImage is not null || UploadedVideo is not null || State.Contains("可重试", StringComparison.Ordinal));
    public bool CanCancel => Transfer?.CanCancel == true;
    public ImageAsset? UploadedImage { get; set; }
    public ProjectFileTransferResult? UploadedVideo { get; set; }

    public void ApplyTransfer(ProjectTransferTaskSnapshot snapshot)
    {
        Transfer = snapshot;
        if (snapshot.FileId is { Length: > 0 }) FileId = snapshot.FileId;
        if (snapshot.State is "failed" or "conflict")
        {
            State = snapshot.Retryable ? "上传失败，可重试" : snapshot.State == "conflict" ? "文件冲突" : "上传失败";
            Error = snapshot.Message ?? "文件传输失败。";
        }
        else if (snapshot.State is "cancelled" or "canceled")
        {
            State = "已取消";
            Error = snapshot.Message;
        }
        else if (snapshot.State != "completed" && State is not "正在关联" and not "已完成")
        {
            State = snapshot.State switch
            {
                "queued" => "排队中",
                "hashing" => "正在校验",
                "transferring" => "正在上传",
                "verifying" => "正在校验",
                "committing" or "staged" => "正在提交",
                "paused" => "已暂停",
                "waiting_network" or "offline" => "等待网络",
                _ => "等待传输状态"
            };
        }
        Changed(nameof(CanRetry)); Changed(nameof(CanCancel));
    }
}

public sealed class AssetRowItem(AssetRow row, ImageItem? selected, IReadOnlyList<ImageItem> candidates) : ObservableModel
{
    private AssetRow _row = row;
    private long _expectedIdentityVersion = row.FieldVersions.GetValueOrDefault("category_name", row.Version);
    public long ExpectedIdentityVersion => _expectedIdentityVersion;
    public AssetRow Row
    {
        get => _row;
        set
        {
            _row = value; _expectedIdentityVersion = value.FieldVersions.GetValueOrDefault("category_name", value.Version);
            CategoryDraft = value.Category; NameDraft = value.Name;
            Changed();
        }
    }
    private string? _categoryDraft = row.Category;
    public string? CategoryDraft { get => _categoryDraft; set { _categoryDraft = value; Changed(); } }
    private string? _nameDraft = row.Name;
    public string? NameDraft { get => _nameDraft; set { _nameDraft = value; Changed(); } }
    private bool _isSaving;
    public bool IsSaving { get => _isSaving; internal set { _isSaving = value; Changed(); } }
    public ImageItem? Selected { get; private set; } = selected;
    public IReadOnlyList<ImageItem> Candidates { get; private set; } = candidates;
    public ObservableCollection<WorkbookUploadCard> TransferCards { get; } = [];
    public bool HasCandidates => Candidates.Count > 0;
    public bool HasNoCandidates => Candidates.Count == 0;
    public void UpdateFrom(AssetRow row)
    {
        Row = row;
        Selected = row.SelectedImage == null ? null : new ImageItem(row.SelectedImage, null);
        Candidates = row.CandidateImages.Select(asset => new ImageItem(asset, null)).ToList();
        Changed(nameof(Selected)); Changed(nameof(Candidates));
        Changed(nameof(HasSelected)); Changed(nameof(HasNoSelected));
        Changed(nameof(HasCandidates)); Changed(nameof(HasNoCandidates));
    }
    public void ApplyRemote(AssetRow row)
    {
        var categoryDraft = CategoryDraft;
        var nameDraft = NameDraft;
        var dirty = !string.Equals(categoryDraft, Row.Category, StringComparison.Ordinal)
            || !string.Equals(nameDraft, Row.Name, StringComparison.Ordinal);
        var identityChanged = !string.Equals(row.Category, Row.Category, StringComparison.Ordinal)
            || !string.Equals(row.Name, Row.Name, StringComparison.Ordinal);
        _row = row;
        if (!dirty || !identityChanged)
        {
            _expectedIdentityVersion = row.FieldVersions.GetValueOrDefault("category_name", row.Version);
            CategoryDraft = dirty ? categoryDraft : row.Category;
            NameDraft = dirty ? nameDraft : row.Name;
        }
        else
        {
            CategoryDraft = categoryDraft; NameDraft = nameDraft;
        }
        Selected = row.SelectedImage == null ? null : new ImageItem(row.SelectedImage, null);
        Candidates = row.CandidateImages.Select(asset => new ImageItem(asset, null)).ToList();
        Changed(nameof(Row)); Changed(nameof(Selected)); Changed(nameof(Candidates));
        Changed(nameof(HasSelected)); Changed(nameof(HasNoSelected));
        Changed(nameof(HasCandidates)); Changed(nameof(HasNoCandidates));
    }
    public bool HasSelected => Selected != null;
    public bool HasNoSelected => Selected == null;
    internal bool SummaryLoading { get; set; }
    internal bool SummaryLoaded { get; set; }
    private ReviewSummary? _generalSummary;
    public string GeneralSummaryText => _generalSummary is { } summary ? $"整条记录讨论 · 评论 {summary.CommentCount}" : "整条记录讨论 · 评论 ?";
    public bool HasReviews => _generalSummary is { } summary && summary.CommentCount > 0;
    public string ReviewCountText => _generalSummary is { } summary && summary.CommentCount > 0
        ? $"{summary.CommentCount} 条讨论"
        : string.Empty;
    public string ReviewSnippetText
    {
        get
        {
            if (_generalSummary is not { } summary || summary.CommentCount <= 0) return string.Empty;
            if (!string.IsNullOrWhiteSpace(summary.LatestComment))
            {
                var userPrefix = string.IsNullOrWhiteSpace(summary.LatestUser) ? "" : $"{summary.LatestUser}: ";
                return $"最新: {userPrefix}{summary.LatestComment}";
            }
            return "点击查看所有讨论";
        }
    }
    public void SetSummaries(IReadOnlyList<ReviewSummary>? summaries)
    {
        _generalSummary = summaries?.FirstOrDefault(s => s.ImageId == null);
        Selected?.SetSummary(summaries?.FirstOrDefault(s => s.ImageId == Selected.Asset.Id));
        foreach (var image in Candidates) image.SetSummary(summaries?.FirstOrDefault(s => s.ImageId == image.Asset.Id));
        Changed(nameof(GeneralSummaryText));
        Changed(nameof(HasReviews));
        Changed(nameof(ReviewCountText));
        Changed(nameof(ReviewSnippetText));
    }
}
public sealed class ProjectFileItem : ObservableModel
{
    private ProjectFile _file;
    public ProjectFile File { get => _file; private set { _file = value; Changed(); } }
    public string Id => File.Id;
    public string Name => File.Name;
    public string TemplateKind => File.TemplateKind;
    public long Version => File.Version;
    public long NameVersion => File.NameVersion;

    private string _nameDraft;
    public string NameDraft { get => _nameDraft; set { _nameDraft = value; Changed(); } }

    private bool _isEditingName;
    public bool IsEditingName { get => _isEditingName; set { _isEditingName = value; Changed(); } }

    public ProjectFileItem(ProjectFile file)
    {
        _file = file;
        _nameDraft = file.Name;
    }

    public void UpdateFile(ProjectFile file)
    {
        File = file;
        if (!IsEditingName) NameDraft = file.Name;
        Changed(nameof(Name));
        Changed(nameof(Version));
        Changed(nameof(NameVersion));
    }

    public void StartRename()
    {
        NameDraft = File.Name;
        IsEditingName = true;
    }

    public void CancelRename()
    {
        NameDraft = File.Name;
        IsEditingName = false;
    }
}

public sealed class SceneRowItem(SceneRow row) : ObservableModel
{
    private SceneRow _row = row;
    private readonly Dictionary<string, SceneVideoItem> _videoItemsById = new(StringComparer.Ordinal);
    public ObservableCollection<WorkbookUploadCard> TransferCards { get; } = [];
    public SceneRow Row
    {
        get => _row;
        set
        {
            _row = value;
            _sceneNumberDraft = value.SceneNumber;
            _beatNumberDraft = value.BeatNumber;
            _storyContentDraft = value.StoryContent;
            _breakdownPlanDraft = value.BreakdownPlan;
            _noteDraft = value.Note;
            _labelLevelDraft = value.LabelLevel ?? "未标注";
            Changed();
            Changed(nameof(StoryPreview));
            Changed(nameof(PlanPreview));
            Changed(nameof(NotePreview));
            Changed(nameof(HasBreakdownPlan));
            Changed(nameof(HasNote));
            Changed(nameof(Video));
            Changed(nameof(Videos));
            Changed(nameof(VideoItems));
            Changed(nameof(HasVideos));
            Changed(nameof(DiscardedVideos));
            Changed(nameof(HasVideo));
            Changed(nameof(DiscardedCount));
            Changed(nameof(HasDiscarded));
            Changed(nameof(VideoDisplayName));
        }
    }

    public long StoryExpectedVersion => Row.FieldVersions.GetValueOrDefault("story_content", Row.Version);

    private string? _sceneNumberDraft = row.SceneNumber;
    public string? SceneNumberDraft { get => _sceneNumberDraft; set { _sceneNumberDraft = value; Changed(); } }

    private string? _beatNumberDraft = row.BeatNumber;
    public string? BeatNumberDraft { get => _beatNumberDraft; set { _beatNumberDraft = value; Changed(); } }

    private string? _storyContentDraft = row.StoryContent;
    public string? StoryContentDraft { get => _storyContentDraft; set { _storyContentDraft = value; Changed(); } }

    private string? _breakdownPlanDraft = row.BreakdownPlan;
    public string? BreakdownPlanDraft
    {
        get => _breakdownPlanDraft;
        set { _breakdownPlanDraft = value; Changed(); Changed(nameof(HasBreakdownPlan)); }
    }
    public bool HasBreakdownPlan => !string.IsNullOrWhiteSpace(BreakdownPlanDraft);

    private string? _noteDraft = row.Note;
    public string? NoteDraft
    {
        get => _noteDraft;
        set { _noteDraft = value; Changed(); Changed(nameof(HasNote)); }
    }
    public bool HasNote => !string.IsNullOrWhiteSpace(NoteDraft);

    private string _labelLevelDraft = row.LabelLevel ?? "未标注";
    public string LabelLevelDraft
    {
        get => _labelLevelDraft;
        set
        {
            // An older workbook may contain a value outside the editor's allowed choices.
            // WPF reports no matching SelectedItem as null; keep that legacy value untouched
            // until the user explicitly chooses one of the valid options.
            if (value is null) return;
            _labelLevelDraft = value;
            Changed();
        }
    }

    public VideoItem? Video => Row.Video;
    public IReadOnlyList<VideoItem> Videos =>
        Row.Videos.Count > 0 ? Row.Videos : (Row.Video != null ? [Row.Video] : []);

    public IReadOnlyList<SceneVideoItem> VideoItems
    {
        get
        {
            var rawList = Row.Videos.Count > 0 ? Row.Videos : (Row.Video != null ? [Row.Video] : []);
            return rawList.Select(video =>
            {
                var key = video.FileId ?? video.Id ?? video.Path ?? video.Name;
                if (!_videoItemsById.TryGetValue(key, out var item))
                    _videoItemsById[key] = item = new SceneVideoItem(video);
                else item.UpdateVideo(video);
                return item;
            }).ToList();
        }
    }

    public bool HasVideos => VideoItems.Count > 0;
    public IReadOnlyList<VideoItem> DiscardedVideos => Row.DiscardedVideos;

    public bool HasVideo => Video != null;
    public int DiscardedCount => Row.DiscardedVideos.Count;
    public bool HasDiscarded => DiscardedCount > 0;

    public string VideoDisplayName => Video?.Name ?? "未关联视频";

    private bool _isSaving;
    public bool IsSaving { get => _isSaving; internal set { _isSaving = value; Changed(); } }

    public string StoryPreview => Row.StoryContent ?? "";
    public string PlanPreview => Row.BreakdownPlan ?? "";
    public string NotePreview => Row.Note ?? "";

    public void ApplyRemote(SceneRow row)
    {
        _row = row;
        _sceneNumberDraft = row.SceneNumber;
        _beatNumberDraft = row.BeatNumber;
        _storyContentDraft = row.StoryContent;
        _breakdownPlanDraft = row.BreakdownPlan;
        _noteDraft = row.Note;
        _labelLevelDraft = row.LabelLevel ?? "未标注";
        Changed(nameof(Row));
        Changed(nameof(SceneNumberDraft));
        Changed(nameof(BeatNumberDraft));
        Changed(nameof(StoryContentDraft));
        Changed(nameof(BreakdownPlanDraft));
        Changed(nameof(NoteDraft));
        Changed(nameof(LabelLevelDraft));
        Changed(nameof(StoryPreview));
        Changed(nameof(PlanPreview));
        Changed(nameof(NotePreview));
        Changed(nameof(Video));
        Changed(nameof(Videos));
        Changed(nameof(VideoItems));
        Changed(nameof(HasVideos));
        Changed(nameof(DiscardedVideos));
        Changed(nameof(HasVideo));
        Changed(nameof(DiscardedCount));
        Changed(nameof(HasDiscarded));
        Changed(nameof(VideoDisplayName));
    }

    private string _discussionText = "讨论 ?";
    public string DiscussionText { get => _discussionText; set { _discussionText = value; Changed(); } }
    internal bool SummaryLoaded { get; set; }
    internal bool SummaryLoading { get; set; }
    private ReviewSummary? _reviewSummary;
    public void SetSummary(ReviewSummary? summary)
    {
        _reviewSummary = summary;
        var count = summary?.CommentCount;
        DiscussionText = count == null ? "讨论 ?" : $"讨论 {count}";
        Changed(nameof(DiscussionText));
        Changed(nameof(HasReviews));
        Changed(nameof(ReviewCountText));
        Changed(nameof(ReviewSnippetText));
        Changed(nameof(NotePreview));
    }
    public bool HasReviews => _reviewSummary is { } summary && summary.CommentCount > 0;
    public string ReviewCountText => _reviewSummary is { } summary && summary.CommentCount > 0
        ? $"{summary.CommentCount} 条批注"
        : string.Empty;
    public string ReviewSnippetText
    {
        get
        {
            if (_reviewSummary is not { } summary || summary.CommentCount <= 0)
            {
                return !string.IsNullOrWhiteSpace(NoteDraft) ? NoteDraft : string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(summary.LatestComment))
            {
                var userPrefix = string.IsNullOrWhiteSpace(summary.LatestUser) ? "" : $"{summary.LatestUser}: ";
                return $"最新: {userPrefix}{summary.LatestComment}";
            }
            return "点击查看批注";
        }
    }
    public ObservableCollection<LinkedAsset> LinkedAssets { get; } = [];
    public bool HasLinkedAssets => false;

    public void ResolveLinkedAssets(IEnumerable<AssetRowItem> assets)
    {
        LinkedAssets.Clear();
    }
}
public sealed class SheetItem(WorkbookSheet sheet)
{
    public WorkbookSheet Sheet { get; } = sheet;
    public string Name => Sheet.Name;
    public override string ToString() => Name;
    public bool IsAsset => Sheet.Kind == "asset_review";
    public bool IsScene => Sheet.Kind == "scene_beats";
    public ObservableCollection<AssetRowItem> Assets { get; } = [];
    public ObservableCollection<SceneRowItem> Scenes { get; } = new(sheet.SceneRows.Select(row => new SceneRowItem(row)));
}

public sealed class ProjectWorkbooksViewModel : ObservableModel, IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _videoLinkGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _assetLinkGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Path, long Length, DateTime LastWriteUtc, ProjectFileTransferResult Result)> _unlinkedVideoTransfers = new(StringComparer.Ordinal);
    private string _inlineStatusMessage = string.Empty;
    private bool _disposed;

    public ProjectWorkbooksViewModel(IProjectWorkbookService service, bool isReadOnly = false)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        IsReadOnly = isReadOnly;
        Service.TransferUpdated += HandleTransferUpdated;
    }

    public IProjectWorkbookService Service { get; }
    public bool IsReadOnly { get; }
    public ObservableCollection<ProjectFileItem> Files { get; } = [];
    public ObservableCollection<ProjectInfo> Projects { get; } = [];
    private ProjectInfo? _selectedProject;
    public ProjectInfo? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (ReferenceEquals(_selectedProject, value)) return;
            _selectedProject = value; Changed();
            if (value != null) _ = EnsureLiveUpdatesAsync(value.Id);
        }
    }
    public ObservableCollection<SheetItem> Sheets { get; } = [];
    private bool _busy;
    public bool Busy { get => _busy; private set { _busy = value; Changed(); Changed(nameof(Ready)); } }
    public string InlineStatusMessage { get => _inlineStatusMessage; private set { _inlineStatusMessage = value; Changed(); Changed(nameof(HasInlineStatus)); } }
    public bool HasInlineStatus => !string.IsNullOrWhiteSpace(InlineStatusMessage);
    public void ReportInlineStatus(string? message) => InlineStatusMessage = message ?? string.Empty;

    public void PersistPendingTransfer(WorkbookUploadCard card)
    {
        if (Document is not { } document || SelectedProject is not { } project ||
            (card.AssetRow is null && card.SceneRow is null))
            throw new InvalidOperationException("无法确定待上传素材所属的项目、工作簿或业务行。");
        if (!Guid.TryParseExact(card.OperationId, "D", out _))
            throw new InvalidDataException("上传操作 ID 无效。");
        var path = Path.GetFullPath(card.LocalPath);
        var old = PendingWorkbookTransferOperation.LoadAll(Service.OperationScope)
            .FirstOrDefault(operation => operation.OperationId == card.OperationId);
        var info = File.Exists(path) ? new FileInfo(path) : null;
        if (old is not null && info is not null && old.UploadedImage is null && old.UploadedVideo is null &&
            (old.SourceLength != info.Length || old.SourceLastWriteUtcTicks != info.LastWriteTimeUtc.Ticks))
            throw new IOException("本机文件自上次尝试后发生变化；为避免将错误内容关联到业务行，请作为新文件重新拖入。");
        if (info is null && (old is null || old.UploadedImage is null && old.UploadedVideo is null))
            throw new FileNotFoundException("找不到待上传的本地文件。", path);
        var operation = new PendingWorkbookTransferOperation(
            Service.OperationScope,
            card.OperationId!,
            card.IsImage ? "asset_image" : "scene_video",
            project.Id,
            document.File.Id,
            card.AssetRow?.Row.Id ?? card.SceneRow!.Row.Id,
            path,
            card.DisplayName,
            card.SelectedTarget,
            info?.Length ?? old!.SourceLength,
            info?.LastWriteTimeUtc.Ticks ?? old!.SourceLastWriteUtcTicks,
            card.State,
            card.Error,
            card.UploadedImage,
            card.UploadedVideo ?? old?.UploadedVideo);
        operation.Save();
    }

    public void RemovePendingTransfer(string operationId)
    {
        foreach (var operation in PendingWorkbookTransferOperation.LoadAll(Service.OperationScope)
                     .Where(operation => operation.OperationId == operationId))
            operation.Remove();
    }

    public void RestorePendingUploadCards()
    {
        if (Document is not { } document || SelectedProject is not { } project) return;
        foreach (var operation in PendingWorkbookTransferOperation.LoadAll(Service.OperationScope)
                     .Where(operation => operation.ProjectId == project.Id && operation.WorkbookId == document.File.Id))
        {
            var imageRow = Sheets.SelectMany(sheet => sheet.Assets).FirstOrDefault(item => item.Row.Id == operation.RowId);
            var sceneRow = Sheets.SelectMany(sheet => sheet.Scenes).FirstOrDefault(item => item.Row.Id == operation.RowId);
            if (operation.Kind == "asset_image" && imageRow is not null &&
                !imageRow.TransferCards.Any(card => card.OperationId == operation.OperationId))
            {
                var card = new WorkbookUploadCard(operation.LocalPath, true, imageRow)
                {
                    OperationId = operation.OperationId,
                    SelectedTarget = operation.SelectedTarget,
                    UploadedImage = operation.UploadedImage,
                    FileId = operation.UploadedImage?.FileId,
                    State = operation.State.Contains("失败", StringComparison.Ordinal) ? operation.State : "已恢复，可重试",
                    Error = operation.Error ?? "已恢复待关联操作。重试会继续原操作并确认资产行关联。"
                };
                ApplyRestoredTransfer(card, operation.OperationId);
                imageRow.TransferCards.Add(card);
            }
            else if (operation.Kind == "scene_video" && sceneRow is not null &&
                     !sceneRow.TransferCards.Any(card => card.OperationId == operation.OperationId))
            {
                var card = new WorkbookUploadCard(operation.LocalPath, false, sceneRow: sceneRow)
                {
                    OperationId = operation.OperationId,
                    UploadedVideo = operation.UploadedVideo,
                    FileId = operation.UploadedVideo?.FileId,
                    State = operation.State.Contains("失败", StringComparison.Ordinal) ? operation.State : "已恢复，可重试",
                    Error = operation.Error ?? "已恢复待关联操作。重试会继续原操作并确认节拍关联。"
                };
                ApplyRestoredTransfer(card, operation.OperationId);
                sceneRow.TransferCards.Add(card);
            }
        }
    }

    private void ApplyRestoredTransfer(WorkbookUploadCard card, string operationId)
    {
        var task = Service.GetTransferTaskSnapshot(operationId);
        if (task is null) return;
        if (!TransferTaskProjectionIsActive(task.State)) card.Error = task.Message;
        else card.Error = null;
        card.ApplyTransfer(task);
    }

    private static bool TransferTaskProjectionIsActive(string state) => state is
        "queued" or "waiting_stable" or "hashing" or "transferring" or "verifying" or "staged" or
        "committing" or "paused" or "waiting_network" or "offline";

    private void HandleTransferUpdated(ProjectTransferTaskSnapshot snapshot)
    {
        void Apply()
        {
            foreach (var card in Sheets.SelectMany(sheet => sheet.Assets).SelectMany(row => row.TransferCards)
                         .Concat(Sheets.SelectMany(sheet => sheet.Scenes).SelectMany(row => row.TransferCards))
                         .Where(card => card.OperationId == snapshot.OperationId))
            {
                card.ApplyTransfer(snapshot);
                if (snapshot.State is "failed" or "conflict" && card.Error is not null)
                {
                    var pending = PendingWorkbookTransferOperation.LoadAll(Service.OperationScope)
                        .FirstOrDefault(operation => operation.OperationId == card.OperationId);
                    if (pending is not null) (pending with { State = card.State, Error = card.Error }).Save();
                }
            }
            ApplyVideoTransferSnapshot(snapshot);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(new Action(Apply));
    }

    private void ApplyVideoTransferSnapshot(ProjectTransferTaskSnapshot snapshot)
    {
        if (snapshot.Source != "project_workbook" || !snapshot.Kind.Contains("download", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(snapshot.ProjectId)
            || snapshot.ScopeId != $"project:{snapshot.ProjectId}:scene-video") return;
        var currentProjectId = Document?.File.ProjectId ?? SelectedProject?.Id;
        if (currentProjectId != snapshot.ProjectId) return;
        foreach (var item in Sheets.SelectMany(sheet => sheet.Scenes).SelectMany(row => row.VideoItems)
                     .Where(item => item.FileId == snapshot.FileId))
            item.ApplyTransfer(snapshot);
    }

    private void RestoreVideoDownloadStatuses()
    {
        var currentProjectId = Document?.File.ProjectId ?? SelectedProject?.Id;
        if (string.IsNullOrWhiteSpace(currentProjectId)) return;
        foreach (var snapshot in Service.GetTransferTaskSnapshots()
                     .Where(task => task.Source == "project_workbook"
                         && task.Kind.Contains("download", StringComparison.OrdinalIgnoreCase)
                         && task.ProjectId == currentProjectId
                         && task.ScopeId == $"project:{currentProjectId}:scene-video")
                     .OrderBy(task => task.UpdatedAt))
            ApplyVideoTransferSnapshot(snapshot);
    }

    public async Task<bool> CancelUploadCardAsync(WorkbookUploadCard card)
    {
        if (string.IsNullOrWhiteSpace(card.OperationId) || !card.CanCancel) return false;
        var canceled = await Service.CancelProjectTransferAsync(card.OperationId);
        if (canceled)
        {
            card.State = "已取消";
            card.Error = null;
            RemovePendingTransfer(card.OperationId);
        }
        return canceled;
    }

    public async Task<bool> CancelVideoDownloadAsync(SceneVideoItem item)
    {
        if (!item.CanCancel || string.IsNullOrWhiteSpace(item.OperationId)) return false;
        return await Service.CancelProjectTransferAsync(item.OperationId);
    }
    public bool Ready => !Busy;
    private string? _error;
    public string? Error { get => _error; private set { _error = value; Changed(); Changed(nameof(CanReload)); } }
    public bool CanReload => !string.IsNullOrEmpty(Error);
    private WorkbookDocument? _document;
    public WorkbookDocument? Document { get => _document; private set { _document = value; Changed(); Changed(nameof(IsHome)); Changed(nameof(IsOpen)); } }
    public bool IsHome => Document == null;
    public bool IsOpen => Document != null;
    private SheetItem? _selectedSheet;
    public SheetItem? SelectedSheet { get => _selectedSheet; set { _selectedSheet = value; Changed(); Changed(nameof(VisibleScenes)); Changed(nameof(VisibleAssets)); } }
    private string _search = "";
    public string Search { get => _search; set { _search = value; Changed(); Changed(nameof(VisibleScenes)); Changed(nameof(VisibleAssets)); } }
    private string _levelFilter = "全部等级";
    public string LevelFilter { get => _levelFilter; set { _levelFilter = value; Changed(); Changed(nameof(VisibleScenes)); } }
    public IReadOnlyList<string> LevelFilters { get; } = ["全部等级", "红", "黄", "绿", "未标注"];
    public IReadOnlyList<string> LabelLevelOptions { get; } = ["红", "黄", "绿", "未标注"];
    public IEnumerable<SceneRowItem> VisibleScenes => (SelectedSheet?.Scenes.AsEnumerable() ?? []).Where(item =>
        (LevelFilter == "全部等级" || (LevelFilter == "未标注" ? string.IsNullOrWhiteSpace(item.Row.LabelLevel) : item.Row.LabelLevel == LevelFilter)) &&
        (string.IsNullOrWhiteSpace(Search) || string.Join(" ", item.Row.SceneNumber, item.Row.BeatNumber, item.Row.StoryContent,
            item.Row.BreakdownPlan, item.Row.Note, item.Row.Video?.Name, item.Row.SceneContext, item.Row.CastContext).Contains(Search, StringComparison.OrdinalIgnoreCase)));
    public IEnumerable<AssetRowItem> VisibleAssets => (SelectedSheet?.Assets.AsEnumerable() ?? []).Where(item =>
        string.IsNullOrWhiteSpace(Search) ||
        string.Join(" ", item.Row.Category, item.Row.Name, item.Row.Variant, item.Row.Appearance).Contains(Search, StringComparison.OrdinalIgnoreCase));
    private readonly SemaphoreSlim _thumbnailGate = new(4);
    private readonly Dictionary<string, ImageSource> _thumbnailCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _liveUpdatesCts;
    private Task? _liveUpdatesTask;
    private TaskCompletionSource<bool>? _liveUpdatesReady;
    private string? _liveUpdatesProjectId;
    private long? _lastEventSequence;
    public bool ApplyingRemoteUpdate { get; private set; }
    public event Action<SceneRow, SceneRow>? SceneRowChanging;
    public PendingWorkbookOperation? PendingOperation { get; private set; }
    public string PendingText => PendingOperation == null ? "" : $"待确认：{PendingOperation.Description}。可继续重试这次操作。";
    public bool HasPendingOperation => PendingOperation != null;
    public void SetPending(PendingWorkbookOperation? operation)
    {
        if (operation == null && PendingOperation != null) PendingWorkbookOperation.Remove(PendingOperation.Scope, PendingOperation.OperationId);
        if (operation != null) PendingWorkbookOperation.Save(operation);
        PendingOperation = operation ?? PendingWorkbookOperation.Load(Service.OperationScope);
        Changed(nameof(PendingText)); Changed(nameof(HasPendingOperation));
    }
    public async Task InitializeAsync()
    {
        var projects = await Service.ListProjectsAsync();
        Projects.Clear(); foreach (var project in projects) Projects.Add(project);
        PendingOperation = PendingWorkbookOperation.Load(Service.OperationScope); Changed(nameof(PendingText)); Changed(nameof(HasPendingOperation));
        SelectedProject = Projects.FirstOrDefault(p => p.Id == PendingOperation?.ProjectId) ?? Projects.FirstOrDefault();
        await RefreshFilesAsync();
    }

    public async Task ExecuteAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; Error = null;
        try { await action(); }
        catch (WorkbookApiException ex) when (ex.StatusCode == 409)
        { Error = "内容已变更，本次未覆盖。请重新加载并核对后再操作。"; }
        catch (OperationCanceledException) { Error = "请求已取消或超时。请重新加载确认结果后再操作。"; }
        catch (Exception ex) { Error = ex is WorkbookApiException or InvalidDataException or InvalidOperationException ? ex.Message : "操作未完成，请检查连接后重新加载。"; }
        finally { Busy = false; }
    }
    public async Task ExecuteInlineAsync(Func<Task> action)
    {
        Error = null;
        try { await action(); }
        catch (WorkbookApiException ex) when (ex.StatusCode == 409)
        { Error = "内容已变更，本次未覆盖。请重新加载并核对后再操作。"; }
        catch (OperationCanceledException) { Error = "请求已取消或超时。请重新加载确认结果后再操作。"; }
        catch (Exception ex) { Error = ex is WorkbookApiException or InvalidDataException or InvalidOperationException ? ex.Message : "操作未完成，请检查连接后重新加载。"; }
    }
    public async Task RefreshFilesAsync()
    {
        if (SelectedProject == null) { Files.Clear(); return; }
        var files = await Service.ListProjectFilesAsync(SelectedProject.Id);
        Files.Clear(); foreach (var file in files) Files.Add(new ProjectFileItem(file));
    }
    /// <summary>
    /// Asked before the open workbook is replaced or closed, and answers whether that is
    /// allowed. The view sets it to whatever is holding unsaved input.
    /// </summary>
    /// <remarks>
    /// It lives here rather than on the open button because the open button is not the only
    /// way in: the host's project tree opens workbooks through OpenWorkbookAsync, and a guard
    /// that only covered the in-page buttons would drop a draft without asking whenever
    /// someone clicked a workbook in the sidebar instead.
    /// </remarks>
    public Func<bool>? ConfirmLeaving { get; set; }
    public Action? ProjectFilesChanged { get; set; }
    public event Action<ImageAsset>? ProjectImageChanged;
    public event Action<string>? ProjectRowsChanged;

    public Task ObserveProjectAsync(string projectId) => EnsureLiveUpdatesAsync(projectId);

    public Task OpenAsync(ProjectFileItem item) => OpenAsync(item.File);
    public async Task OpenAsync(ProjectFile file)
    {
        if (ConfirmLeaving?.Invoke() == false) return;
        if (!string.IsNullOrWhiteSpace(file.ProjectId)) await EnsureLiveUpdatesAsync(file.ProjectId);
        var sequenceBeforeLoad = _lastEventSequence;
        var document = AsReadOnly(await Service.OpenAsync(file.Id));
        var sheets = new List<SheetItem>();
        foreach (var source in document.Sheets)
        {
            if (source.Kind is not ("asset_review" or "scene_beats"))
                throw new InvalidDataException("存在尚不支持的工作表类型，请联系管理员检查数据库记录。");
            var sheet = new SheetItem(source);
            foreach (var row in source.AssetRows) sheet.Assets.Add(MakeRow(row));
            sheets.Add(sheet);
        }
        Document = document;
        Sheets.Clear(); foreach (var sheet in sheets) Sheets.Add(sheet);
        SelectedSheet = Sheets.FirstOrDefault(s => s.IsScene || s.IsAsset) ?? Sheets.FirstOrDefault();
        RestorePendingUploadCards();
        RestoreVideoDownloadStatuses();
        if (Document.File.ProjectId == file.ProjectId && _lastEventSequence != sequenceBeforeLoad)
            await RefreshCurrentDocumentAsync(CancellationToken.None);
        if (SelectedSheet is { IsAsset: true } assetSheet)
        {
            foreach (var asset in assetSheet.Assets.Take(20))
            {
                if (asset.Selected != null) _ = EnsureThumbnailAsync(asset.Selected);
                foreach (var candidate in asset.Candidates) _ = EnsureThumbnailAsync(candidate);
            }
        }
        if (file.TemplateKind == "scene_beats")
        {
            _ = LoadProjectAssetsAsync();
        }
    }

    private async Task EnsureLiveUpdatesAsync(string projectId)
    {
        if (string.Equals(_liveUpdatesProjectId, projectId, StringComparison.Ordinal)
            && _liveUpdatesReady is { } currentReady)
        {
            try { await currentReady.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            return;
        }
        _liveUpdatesCts?.Cancel();
        _liveUpdatesProjectId = projectId;
        _lastEventSequence = null;
        _liveUpdatesCts = new CancellationTokenSource();
        _liveUpdatesReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _liveUpdatesTask = RunLiveUpdatesAsync(projectId, _liveUpdatesCts.Token);
        try { await _liveUpdatesReady.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }

    private async Task RunLiveUpdatesAsync(string projectId, CancellationToken ct)
    {
        var retry = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in Service.SubscribeProjectEventsAsync(projectId, _lastEventSequence, ct))
                {
                    if (change.EventKind == "ready")
                    {
                        if (Document?.File.ProjectId == projectId)
                            await OnUiAsync(() => RefreshCurrentDocumentAsync(ct));
                        _lastEventSequence = change.Sequence;
                        _liveUpdatesReady?.TrySetResult(true);
                        continue;
                    }
                    retry = TimeSpan.FromSeconds(1);
                    await OnUiAsync(() => HandleProjectChangeAsync(projectId, change, ct));
                    _lastEventSequence = change.Sequence;
                }
                throw new EndOfStreamException("实时更新连接已关闭。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch
            {
                _liveUpdatesReady?.TrySetResult(false);
                try { await Task.Delay(retry, ct); } catch (OperationCanceledException) { break; }
                retry = TimeSpan.FromSeconds(Math.Min(15, retry.TotalSeconds * 2));
            }
        }
    }

    private Task OnUiAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) return action();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await action(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        }));
        return completion.Task;
    }

    private async Task HandleProjectChangeAsync(string projectId, ProjectChangeEvent change, CancellationToken ct)
    {
        if (!string.Equals(SelectedProject?.Id, projectId, StringComparison.Ordinal)) return;
        await RefreshProjectFilesFromEventAsync(projectId, ct);
        if (change.EntityKind == "image")
        {
            try
            {
                var image = await Service.GetProjectImageAsync(change.EntityId, ct);
                InvalidateOlderThumbnailRevisions(image);
                ProjectImageChanged?.Invoke(image);
            }
            catch { /* A stale or removed image must not break the project change feed. */ }
            if (Document?.File.ProjectId == projectId)
            {
                if (Document.File.TemplateKind == "scene_beats") await LoadProjectAssetsAsync(ct);
                else await RefreshCurrentDocumentAsync(ct);
            }
            return;
        }
        if (Document?.File.Id == change.WorkbookId)
            await RefreshCurrentDocumentAsync(ct);
        else if (Document?.File.ProjectId == projectId && Document.File.TemplateKind == "scene_beats")
            _ = LoadProjectAssetsAsync(ct);
        if (change.EntityKind == "row" && change.ChangedFields.Contains("image_selection", StringComparer.Ordinal))
            ProjectRowsChanged?.Invoke(projectId);
    }

    private async Task RefreshProjectFilesFromEventAsync(string projectId, CancellationToken ct)
    {
        var files = await Service.ListProjectFilesAsync(projectId, ct);
        var editingDrafts = Files.Where(f => f.IsEditingName).ToDictionary(f => f.Id, f => f.NameDraft);
        Files.Clear();
        foreach (var file in files)
        {
            var item = new ProjectFileItem(file);
            if (editingDrafts.TryGetValue(file.Id, out var draft))
            {
                item.IsEditingName = true;
                item.NameDraft = draft;
            }
            Files.Add(item);
        }
        ProjectFilesChanged?.Invoke();
    }

    private async Task RefreshCurrentDocumentAsync(CancellationToken ct)
    {
        if (Document is not { } current) return;
        var document = AsReadOnly(await Service.OpenAsync(current.File.Id, ct));
        var sceneItems = Sheets.SelectMany(sheet => sheet.Scenes).ToDictionary(item => item.Row.Id, StringComparer.Ordinal);
        var assetItems = Sheets.SelectMany(sheet => sheet.Assets).ToDictionary(item => item.Row.Id, StringComparer.Ordinal);
        var selectedSheetId = SelectedSheet?.Sheet.Id;
        var refreshedSheets = new List<SheetItem>();
        foreach (var source in document.Sheets)
        {
            var sheet = new SheetItem(source);
            sheet.Scenes.Clear(); sheet.Assets.Clear();
            foreach (var row in source.SceneRows)
            {
                if (sceneItems.TryGetValue(row.Id, out var existing))
                {
                    SceneRowChanging?.Invoke(existing.Row, row);
                    existing.ApplyRemote(row);
                    sheet.Scenes.Add(existing);
                }
                else sheet.Scenes.Add(new SceneRowItem(row));
            }
            foreach (var row in source.AssetRows)
            {
                if (assetItems.TryGetValue(row.Id, out var existing))
                {
                    existing.ApplyRemote(row);
                    sheet.Assets.Add(existing);
                }
                else
                {
                    var item = MakeRow(row); sheet.Assets.Add(item);
                }
            }
            refreshedSheets.Add(sheet);
        }
        ApplyingRemoteUpdate = true;
        try
        {
            Document = document;
            Sheets.Clear(); foreach (var sheet in refreshedSheets) Sheets.Add(sheet);
            SelectedSheet = refreshedSheets.FirstOrDefault(sheet => sheet.Sheet.Id == selectedSheetId)
                ?? refreshedSheets.FirstOrDefault(sheet => sheet.IsScene || sheet.IsAsset)
                ?? refreshedSheets.FirstOrDefault();
            RestorePendingUploadCards();
            RestoreVideoDownloadStatuses();
            SceneChanged();
            AssetChanged();
        }
        finally { ApplyingRemoteUpdate = false; }
    }

    public Task RefreshOpenDocumentAsync() => RefreshCurrentDocumentAsync(CancellationToken.None);

    public void StopLiveUpdates()
    {
        _liveUpdatesCts?.Cancel();
        _liveUpdatesCts?.Dispose();
        _liveUpdatesCts = null;
        _liveUpdatesReady = null;
        _liveUpdatesProjectId = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Service.TransferUpdated -= HandleTransferUpdated;
        StopLiveUpdates();
    }

    public ObservableCollection<AssetRowItem> ProjectAssets { get; } = [];

    public async Task LoadProjectAssetsAsync(CancellationToken ct = default)
    {
        if (Document == null || SelectedProject == null) return;
        var assetFile = Files.FirstOrDefault(f => f.TemplateKind == "asset_review");
        if (assetFile == null) return;
        try
        {
            var doc = AsReadOnly(await Service.OpenAsync(assetFile.Id, ct));
            var assetRows = new List<AssetRowItem>();
            foreach (var sheet in doc.Sheets)
            {
                foreach (var row in sheet.AssetRows)
                {
                    var item = MakeRow(row);
                    assetRows.Add(item);
                }
            }
            ProjectAssets.Clear();
            foreach (var r in assetRows) ProjectAssets.Add(r);
            foreach (var sheet in Sheets)
            {
                if (!sheet.IsScene) continue;
                foreach (var scene in sheet.Scenes)
                {
                    scene.ResolveLinkedAssets(ProjectAssets);
                }
            }
            SceneChanged();
        }
        catch { }
    }

    public async Task AddEpisodeAsync(string sheetName)
    {
        if (IsReadOnly) return;
        if (Document == null) return;
        await Service.AddSheetAsync(Document.File.Id, sheetName);
        await OpenAsync(Document.File);
        var created = Sheets.FirstOrDefault(s => s.Name == sheetName);
        if (created != null) SelectedSheet = created;
    }

    public async Task AddSceneRowAsync()
    {
        if (IsReadOnly) return;
        var sheet = SelectedSheet;
        if (Document == null || sheet == null || !sheet.IsScene) return;
        var row = await Service.CreateSceneRowAsync(Document.File.Id, sheet.Sheet.Id);
        var item = new SceneRowItem(row);
        sheet.Scenes.Add(item);
        item.ResolveLinkedAssets(ProjectAssets);
        SceneChanged();
    }

    public async Task AddAssetRowAsync()
    {
        if (IsReadOnly) return;
        var sheet = SelectedSheet;
        if (Document == null || sheet == null || !sheet.IsAsset) return;
        var row = await Service.CreateAssetRowAsync(Document.File.Id, sheet.Sheet.Id);
        var item = MakeRow(row);
        sheet.Assets.Add(item);
        if (ProjectAssets.All(asset => asset.Row.Id != row.Id)) ProjectAssets.Add(item);
        foreach (var scene in Sheets.SelectMany(candidate => candidate.Scenes))
            scene.ResolveLinkedAssets(ProjectAssets);
        SceneChanged();
        AssetChanged();
    }

    public async Task DeleteSceneRowAsync(SceneRowItem item)
    {
        if (IsReadOnly) return;
        if (Document == null || !item.Row.CanEdit) return;
        await Service.DeleteRowAsync(Document.File.Id, item.Row.Id, "scene-rows", item.Row.Version);
        foreach (var sheet in Sheets) sheet.Scenes.Remove(item);
        SceneChanged();
    }

    public async Task DeleteAssetRowAsync(AssetRowItem item)
    {
        if (IsReadOnly) return;
        if (Document == null || !item.Row.CanEdit) return;
        await Service.DeleteRowAsync(Document.File.Id, item.Row.Id, "asset-rows", item.Row.Version);
        foreach (var sheet in Sheets) sheet.Assets.Remove(item);
        for (var index = ProjectAssets.Count - 1; index >= 0; index--)
            if (ProjectAssets[index].Row.Id == item.Row.Id) ProjectAssets.RemoveAt(index);
        foreach (var scene in Sheets.SelectMany(sheet => sheet.Scenes))
            scene.ResolveLinkedAssets(ProjectAssets);
        SceneChanged();
        AssetChanged();
    }

    public void Back() => TryBack();

    public bool TryBack()
    {
        if (ConfirmLeaving?.Invoke() == false) return false;
        StopLiveUpdates();
        Document = null; SelectedSheet = null; Sheets.Clear();
        return true;
    }
    public async Task<ImageItem> MakeImageAsync(ImageAsset image)
    {
        var item = new ImageItem(image, null); await EnsureThumbnailAsync(item); return item;
    }
    public async Task EnsureThumbnailAsync(ImageItem image, CancellationToken ct = default)
    {
        if (image.Preview != null || image.Loading) return;
        image.Loading = true;
        var entered = false;
        try
        {
            await _thumbnailGate.WaitAsync(ct); entered = true;
            var cacheKey = ThumbnailCacheKey(image.Asset);
            if (!_thumbnailCache.TryGetValue(cacheKey, out var preview))
            {
                preview = Decode(await Service.GetImageContentAsync(image.Asset.Id, true, ct), thumbnail: true);
                if (_thumbnailCache.Count >= 128) _thumbnailCache.Remove(_thumbnailCache.Keys.First());
                _thumbnailCache[cacheKey] = preview;
            }
            ct.ThrowIfCancellationRequested(); image.SetPreview(preview);
        }
        catch (OperationCanceledException) { }
        catch { image.SetPreview(null, "缩略图暂不可用；点击图片仍可重试大图。"); }
        finally { image.Loading = false; if (entered) _thumbnailGate.Release(); }
    }

    private static string ThumbnailCacheKey(ImageAsset asset) =>
        asset.Id + "\0" + (asset.CurrentRevisionId ?? "current");

    private void InvalidateOlderThumbnailRevisions(ImageAsset asset)
    {
        var prefix = asset.Id + "\0";
        var currentKey = ThumbnailCacheKey(asset);
        foreach (var staleKey in _thumbnailCache.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && key != currentKey)
                     .ToArray())
            _thumbnailCache.Remove(staleKey);
    }

    public static BitmapSource Decode(byte[] bytes, bool thumbnail = false)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (thumbnail) image.DecodePixelWidth = 320;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    private static AssetRowItem MakeRow(AssetRow row)
    {
        return new AssetRowItem(row, row.SelectedImage == null ? null : new ImageItem(row.SelectedImage, null),
            row.CandidateImages.Select(asset => new ImageItem(asset, null)).ToList());
    }

    private WorkbookDocument AsReadOnly(WorkbookDocument document)
    {
        if (!IsReadOnly) return document;
        return document with
        {
            Sheets = document.Sheets.Select(sheet => sheet with
            {
                SceneRows = sheet.SceneRows.Select(row => row with { CanEdit = false }).ToList(),
                AssetRows = sheet.AssetRows.Select(row => row with { CanEdit = false }).ToList()
            }).ToList()
        };
    }
    public async Task RefreshReviewSummariesAsync(string fileId, AssetRowItem item)
    {
        if (item.SummaryLoading) return; item.SummaryLoading = true;
        try { item.SetSummaries(await Service.GetReviewSummariesForRowAsync(fileId, item.Row.Id, "asset-rows")); item.SummaryLoaded = true; }
        catch { item.SetSummaries(null); } // Unknown is explicitly displayed as ?, never as zero.
        finally { item.SummaryLoading = false; }
    }
    public async Task RefreshSceneSummaryAsync(string fileId, SceneRowItem item)
    {
        if (item.SummaryLoading) return; item.SummaryLoading = true;
        try { var summaries = await Service.GetReviewSummariesForRowAsync(fileId, item.Row.Id, "scene-rows");
            var summary = summaries.FirstOrDefault(s => s.ImageId == null);
            item.SetSummary(summary);
            item.SummaryLoaded = true; }
        catch { item.SetSummary(null); }
        finally { item.SummaryLoading = false; }
    }
    public void SceneChanged() => Changed(nameof(VisibleScenes));
    public void AssetChanged() => Changed(nameof(VisibleAssets));
    public async Task UpdateImagesAsync(AssetRowItem item, string? selectedId, IEnumerable<string> candidateIds)
    {
        if (IsReadOnly) throw new InvalidDataException("访客只能查看项目工作簿。");
        if (Document == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行图片的权限。");
        var ids = candidateIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > 100) throw new InvalidDataException("每行最多保留 100 张备选图片。");
        var gate = _assetLinkGates.GetOrAdd(item.Row.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await UpdateImagesWhileLockedAsync(item, selectedId, ids);
        }
        finally { gate.Release(); }
    }

    public async Task LinkUploadedImageAsync(AssetRowItem item, ImageAsset image, bool selectedTarget)
    {
        if (IsReadOnly) throw new InvalidDataException("访客只能查看项目工作簿。");
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行图片的权限。");
        var gate = _assetLinkGates.GetOrAdd(item.Row.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var candidateIds = item.Row.CandidateImages.Select(asset => asset.Id).ToList();
            if (!candidateIds.Contains(image.Id, StringComparer.Ordinal) && candidateIds.Count < 100)
                candidateIds.Add(image.Id);
            await UpdateImagesWhileLockedAsync(item,
                selectedTarget ? image.Id : item.Row.SelectedImage?.Id, candidateIds);
        }
        finally { gate.Release(); }
    }

    private async Task UpdateImagesWhileLockedAsync(AssetRowItem item, string? selectedId, List<string> ids)
    {
        var document = Document ?? throw new InvalidOperationException("工作簿已关闭；关联操作保留待恢复状态。");
        var sheet = Sheets.FirstOrDefault(candidate => candidate.Assets.Contains(item))
            ?? throw new InvalidOperationException("资产行已离开当前工作簿；请重新打开原工作簿后重试关联。");
        var workbookId = document.File.Id;
        var updated = await Service.UpdateImagesAsync(workbookId, item.Row.Id,
            new UpdateImagesRequest(item.Row.FieldVersions.GetValueOrDefault("image_selection", 1), selectedId, ids));
        if (Document?.File.Id != workbookId || !sheet.Assets.Contains(item))
            throw new InvalidOperationException("工作簿在图片关联期间发生变化；请重新打开原工作簿核对关联结果。");
        item.UpdateFrom(updated);
        AssetChanged();
    }
    public async Task UpdateStoryContentAsync(SceneRowItem item)
    {
        if (IsReadOnly) throw new InvalidDataException("访客只能查看项目工作簿。");
        if (Document == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行的权限。");
        if (item.IsSaving || string.Equals(item.StoryContentDraft, item.Row.StoryContent, StringComparison.Ordinal)) return;
        var fileId = Document.File.Id;
        var current = item.Row;
        item.IsSaving = true;
        try
        {
            item.Row = await Service.UpdateStoryContentAsync(fileId, current.Id,
                new UpdateStoryContentRequest(item.StoryExpectedVersion, item.StoryContentDraft));
            SceneChanged();
        }
        finally { item.IsSaving = false; }
    }
    public async Task UpdateAssetFieldsAsync(AssetRowItem item)
    {
        if (IsReadOnly) throw new InvalidDataException("访客只能查看项目工作簿。");
        if (Document == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行的权限。");
        if (item.IsSaving || (string.Equals(item.CategoryDraft, item.Row.Category, StringComparison.Ordinal)
            && string.Equals(item.NameDraft, item.Row.Name, StringComparison.Ordinal))) return;
        var fileId = Document.File.Id;
        item.IsSaving = true;
        try
        {
            var updated = await Service.UpdateAssetRowAsync(fileId, item.Row.Id,
                new UpdateAssetRowRequest(item.ExpectedIdentityVersion, item.CategoryDraft, item.NameDraft));
            item.UpdateFrom(updated);
            var assetFile = Files.FirstOrDefault(file => file.TemplateKind == "asset_review");
            if (assetFile?.Id == fileId)
            {
                foreach (var projectAsset in ProjectAssets.Where(asset => asset.Row.Id == updated.Id && !ReferenceEquals(asset, item)))
                {
                    projectAsset.UpdateFrom(updated);
                }
            }
            foreach (var scene in Sheets.SelectMany(candidate => candidate.Scenes))
                scene.ResolveLinkedAssets(ProjectAssets);
            SceneChanged();
            AssetChanged();
        }
        finally { item.IsSaving = false; }
    }

    public async Task RenameWorkbookAsync(ProjectFileItem item, string newName)
    {
        if (IsReadOnly) throw new InvalidDataException("访客只能查看项目工作簿。");
        var trimmed = newName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, item.Name, StringComparison.Ordinal))
        {
            item.CancelRename();
            return;
        }

        await ExecuteAsync(async () =>
        {
            var updated = await Service.RenameWorkbookAsync(item.Id, item.NameVersion, trimmed);
            item.UpdateFile(updated);
            if (Document?.File.Id == item.Id)
            {
                Document = Document with { File = updated };
            }
        });
    }

    public async Task DeleteWorkbookAsync(ProjectFileItem item)
    {
        if (IsReadOnly) throw new InvalidDataException("访客只能查看项目工作簿。");
        await ExecuteAsync(async () =>
        {
            await Service.DeleteWorkbookAsync(item.Id);
            Files.Remove(item);
            if (Document?.File.Id == item.Id)
            {
                Document = null;
            }
        });
    }

    public async Task UpdateSceneFieldsAsync(SceneRowItem item)
    {
        if (IsReadOnly) throw new InvalidDataException("访客只能查看项目工作簿。");
        if (Document == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行的权限。");
        var sceneChanged = !string.Equals(item.SceneNumberDraft, item.Row.SceneNumber, StringComparison.Ordinal);
        var beatChanged = !string.Equals(item.BeatNumberDraft, item.Row.BeatNumber, StringComparison.Ordinal);
        var storyChanged = !string.Equals(item.StoryContentDraft, item.Row.StoryContent, StringComparison.Ordinal);
        var planChanged = !string.Equals(item.BreakdownPlanDraft, item.Row.BreakdownPlan, StringComparison.Ordinal);
        var noteChanged = !string.Equals(item.NoteDraft, item.Row.Note, StringComparison.Ordinal);
        var level = item.LabelLevelDraft == "未标注" ? null : item.LabelLevelDraft;
        var levelChanged = !string.Equals(level, item.Row.LabelLevel, StringComparison.Ordinal);

        if (levelChanged && item.LabelLevelDraft is not ("红" or "黄" or "绿" or "未标注"))
            throw new InvalidDataException("制作难度仅允许红、黄、绿或未标注。");

        if (!sceneChanged && !beatChanged && !storyChanged && !planChanged && !noteChanged && !levelChanged)
            return;

        if (item.IsSaving) return;
        item.IsSaving = true;
        try
        {
            var req = new UpdateSceneRowRequest(
                ExpectedVersion: item.Row.Version,
                SceneNumber: item.SceneNumberDraft,
                BeatNumber: item.BeatNumberDraft,
                StoryContent: item.StoryContentDraft,
                BreakdownPlan: item.BreakdownPlanDraft,
                Note: item.NoteDraft,
                LabelLevel: levelChanged ? level : null,
                ClearLabelLevel: levelChanged && level == null
            );
            var updated = await Service.UpdateSceneRowAsync(Document.File.Id, item.Row.Id, req);
            item.ApplyRemote(updated);
            item.ResolveLinkedAssets(ProjectAssets);
            SceneChanged();
        }
        finally
        {
            item.IsSaving = false;
        }
    }

    public async Task UpdateSceneVideoAsync(SceneRowItem item, string action, VideoItem? video = null, string? videoId = null, string? videoName = null)
    {
        if (IsReadOnly) throw new InvalidDataException("访客只能查看项目工作簿。");
        var document = Document;
        if (document == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行的权限。");
        await ExecuteAsync(() => UpdateSceneVideoCoreAsync(document.File.Id, item, action, video, videoId, videoName));
    }

    private async Task UpdateSceneVideoCoreAsync(string fileId, SceneRowItem item, string action,
        VideoItem? video = null, string? videoId = null, string? videoName = null)
    {
        var req = new UpdateSceneRowRequest(
            ExpectedVersion: item.Row.Version,
            VideoAction: action,
            Video: video,
            VideoId: videoId,
            VideoName: videoName
        );
        var updated = await Service.UpdateSceneRowAsync(fileId, item.Row.Id, req);
        item.ApplyRemote(updated);
        SceneChanged();
    }

    public async Task AttachVideoFileAsync(SceneRowItem item, string sourceLocalPath, string? operationId = null,
        WorkbookUploadCard? uploadCard = null)
    {
        var gate = _videoLinkGates.GetOrAdd(item.Row.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await AttachVideoFileCoreAsync(item, sourceLocalPath, operationId, uploadCard); }
        finally { gate.Release(); }
    }

    private async Task AttachVideoFileCoreAsync(SceneRowItem item, string sourceLocalPath, string? operationId,
        WorkbookUploadCard? uploadCard)
    {
        if (IsReadOnly) throw new InvalidDataException("访客只能查看项目工作簿。");
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行的权限。");
        var document = Document ?? throw new InvalidOperationException("请先打开节拍表。");
        var project = SelectedProject ?? throw new InvalidOperationException("请先选择项目。");
        var workbookId = document.File.Id;
        var sheet = Sheets.FirstOrDefault(candidate => candidate.Scenes.Contains(item))
            ?? throw new InvalidOperationException("找不到该节拍行所属的工作表，请重新打开工作簿后重试。");
        var sourcePath = Path.GetFullPath(sourceLocalPath);
        var sourceInfo = File.Exists(sourcePath) ? new FileInfo(sourcePath) : null;
        var recoveryKey = workbookId + "\0" + item.Row.Id + "\0" + sourcePath;
        var fileName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName)) throw new InvalidDataException("视频文件名无效。");
        RequirePathSegment(fileName, "视频文件名");
        var episode = RequirePathSegment(sheet.Name, "工作表名称");
        var rawBeatNumber = item.Row.BeatNumber;
        if (string.IsNullOrWhiteSpace(rawBeatNumber) || !rawBeatNumber.Any(char.IsDigit))
            throw new InvalidDataException("请先为节拍行填写有效编号，再关联视频。");
        var beatNumber = NormalizeBeatNumber(rawBeatNumber);
        var beatFolder = $"第{beatNumber}beat";
        var rowsInBeatFolder = sheet.Scenes.Where(candidate =>
            NormalizeBeatNumber(candidate.Row.BeatNumber) == beatNumber).ToArray();
        if (rowsInBeatFolder.Length != 1)
            throw new InvalidDataException($"节拍 {beatNumber} 必须恰好对应一行；当前找到 {rowsInBeatFolder.Length} 行，请先整理节拍表。");

        ProjectFileTransferResult? uploaded = uploadCard?.UploadedVideo;
        if (uploaded is not null)
        {
            if (uploadCard?.FileId != uploaded.FileId)
                throw new InvalidDataException("已上传视频的文件标识不一致，拒绝建立关联。");
        }
        else if (_unlinkedVideoTransfers.TryGetValue(recoveryKey, out var pending))
        {
            if (sourceInfo is null || pending.Length != sourceInfo.Length || pending.LastWriteUtc != sourceInfo.LastWriteTimeUtc)
                throw new IOException("本机视频自上次上传后发生变化；为避免错误关联，请使用新文件名重新拖入。");
            uploaded = pending.Result;
            if (uploadCard is not null) uploadCard.FileId = uploaded.FileId;
        }
        else
        {
            if (sourceInfo is null) throw new FileNotFoundException("找不到所选视频文件。", sourcePath);
            var activeVideos = rowsInBeatFolder.SelectMany(candidate => ActiveVideos(candidate.Row)).ToArray();
            if (activeVideos.Any(video => video.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("该节拍目录已关联同名视频，未覆盖现有内容。请先处理现有关联或重命名新文件。");
            if (rowsInBeatFolder.SelectMany(candidate => candidate.Row.DiscardedVideos)
                .Any(video => video.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("同名视频已在废片库中，未覆盖或重新关联。请先恢复原视频或重命名新文件。");
        }

        if (uploaded is not null && rowsInBeatFolder.SelectMany(candidate => ActiveVideos(candidate.Row))
                .Any(video => video.FileId == uploaded.FileId || video.Id == uploaded.FileId))
        {
            _unlinkedVideoTransfers.TryRemove(recoveryKey, out _);
            if (uploadCard is not null) uploadCard.State = "已完成";
            if (operationId is not null) RemovePendingTransfer(operationId);
            return; // The process may have stopped after the server committed the link.
        }
        if (uploaded is not null && rowsInBeatFolder.SelectMany(candidate => candidate.Row.DiscardedVideos)
                .Any(video => video.FileId == uploaded.FileId || video.Id == uploaded.FileId))
            throw new IOException("该视频已在废片库中；为避免自动恢复或覆盖废片，请先在界面中明确恢复。");
        if (uploaded is not null && rowsInBeatFolder.SelectMany(candidate => ActiveVideos(candidate.Row))
                .Any(video => video.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                    && video.FileId != uploaded.FileId && video.Id != uploaded.FileId))
            throw new IOException("节拍目录中已出现同名视频；已上传文件保留在项目文件库中，但未建立重复名称关联。");
        if (uploaded is not null && rowsInBeatFolder.SelectMany(candidate => candidate.Row.DiscardedVideos)
                .Any(video => video.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                    && video.FileId != uploaded.FileId && video.Id != uploaded.FileId))
            throw new IOException("同名视频已在废片库中；已上传文件保留在项目文件库中，但未自动恢复或建立关联。");

        var logicalPath = $"{episode}/{beatFolder}/{fileName}";
        Error = null;
        try
        {
            if (uploaded is null)
            {
                if (uploadCard is not null)
                {
                    uploadCard.State = "正在上传";
                    PersistPendingTransfer(uploadCard);
                }
                var mediaType = Path.GetExtension(fileName).ToLowerInvariant() switch
                {
                    ".mov" => "video/quicktime",
                    ".mkv" => "video/x-matroska",
                    ".webm" => "video/webm",
                    ".avi" => "video/x-msvideo",
                    _ => "video/mp4"
                };
                uploaded = await Service.UploadProjectFileAsync(project.Id, fileName, mediaType, sourcePath, logicalPath,
                    operationId: operationId);
                if (sourceInfo is not null)
                    _unlinkedVideoTransfers[recoveryKey] =
                        (sourcePath, sourceInfo.Length, sourceInfo.LastWriteTimeUtc, uploaded);
                if (uploadCard is not null) uploadCard.FileId = uploaded.FileId;
                if (uploadCard is not null)
                {
                    uploadCard.UploadedVideo = uploaded;
                    PersistPendingTransfer(uploadCard);
                }
            }

            if (uploadCard is not null) uploadCard.State = "正在关联";

            if (Document?.File.Id != workbookId || SelectedProject?.Id != project.Id)
                throw new InvalidOperationException("项目或工作簿在上传期间发生变化；文件已进入项目文件库，但尚未关联，请重新加载核对。");

            var videoItem = new VideoItem(uploaded.FileId, fileName, uploaded.LogicalPath) { FileId = uploaded.FileId };
            await UpdateSceneVideoCoreAsync(workbookId, item, "add", videoItem);
            _unlinkedVideoTransfers.TryRemove(recoveryKey, out _);
            if (uploadCard is not null) uploadCard.State = "已完成";
            if (operationId is not null) RemovePendingTransfer(operationId);
            ReportInlineStatus($"视频「{fileName}」已上传并关联到 {episode}/{beatFolder}。");
        }
        catch (Exception exception)
        {
            var serverRejected = exception is WorkbookApiException api && api.StatusCode == 409;
            Error = serverRejected
                ? "节拍表内容已被其他人修改，本次未关联。请重新加载并核对。"
                : exception is WorkbookApiException or InvalidDataException or InvalidOperationException or IOException
                    ? exception.Message
                    : "视频关联未完成，请检查连接后重试。";
            if (uploadCard is not null)
            {
                uploadCard.State = uploaded is null ? "上传失败，可重试" : "关联失败，可重试";
                uploadCard.Error = Error;
                try { PersistPendingTransfer(uploadCard); }
                catch (Exception persistError) when (persistError is IOException or InvalidOperationException or InvalidDataException)
                { ReportInlineStatus($"{Error}；待恢复信息保存失败：{persistError.Message}"); }
            }
            if (uploaded is not null)
                throw new IOException($"视频已上传，但节拍关联未完成；文件 ID：{uploaded.FileId}。重试仅会重试关联，不会删除或覆盖该文件。{Environment.NewLine}{exception.Message}", exception);
            throw;
        }
    }

    public Task<string> DownloadVideoAsync(VideoItem video, CancellationToken cancellationToken = default)
    {
        var fileId = video.FileId;
        if (string.IsNullOrWhiteSpace(fileId))
            throw new InvalidOperationException("此视频尚未关联版本化文件 ID，无法从文件服务打开。");
        var projectId = Document?.File.ProjectId ?? SelectedProject?.Id;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("无法确定视频所属项目，已停止下载。");
        return Service.DownloadProjectFileAsync(fileId, null, video.Name, cancellationToken,
            projectId);
    }

    private static string RequirePathSegment(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Any(char.IsControl))
            throw new InvalidDataException($"{description}不能作为项目目录名使用。");
        return value;
    }

    private static IEnumerable<VideoItem> ActiveVideos(SceneRow row)
    {
        var videos = row.Videos.ToList();
        if (row.Video is { } current && !videos.Any(video =>
                (!string.IsNullOrWhiteSpace(current.Id) && video.Id == current.Id)
                || video.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase)))
            videos.Insert(0, current);
        return videos;
    }

    private static string NormalizeBeatNumber(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(input, @"[^\d]", "").Trim();
        if (string.IsNullOrEmpty(cleaned)) return string.Empty;
        var normalized = cleaned.TrimStart('0');
        return normalized.Length == 0 ? "0" : normalized;
    }
}

public sealed class SceneVideoItem(VideoItem video) : ObservableModel
{
    private VideoItem _video = video;
    private ProjectTransferTaskSnapshot? _transfer;
    private string _downloadStatus = string.Empty;
    private string? _downloadError;
    private string? _localPath;
    private bool _downloadInProgress;
    private bool _retryAvailable;

    public VideoItem Video => _video;
    public string Id => Video.Id ?? string.Empty;
    public string Name => Video.Name;
    public string? Path => Video.Path;
    public string? FileId => Video.FileId;
    public string? OperationId => _transfer?.OperationId;
    public string DownloadStatus { get => _downloadStatus; private set { _downloadStatus = value; Changed(); Changed(nameof(HasDownloadStatus)); } }
    public string? DownloadError { get => _downloadError; private set { _downloadError = value; Changed(); Changed(nameof(HasDownloadError)); } }
    public bool HasDownloadStatus => !string.IsNullOrWhiteSpace(DownloadStatus);
    public bool HasDownloadError => !string.IsNullOrWhiteSpace(DownloadError);
    public bool DownloadInProgress { get => _downloadInProgress; private set { _downloadInProgress = value; Changed(); } }
    public string? LocalPath { get => _localPath; private set { _localPath = value; Changed(); } }
    public double Progress => _transfer is { BytesTotal: > 0 } task
        ? Math.Clamp((double)Math.Max(0, task.BytesDone) / task.BytesTotal.Value * 100, 0, 100)
        : _transfer?.State == "completed" ? 100 : 0;
    public bool HasProgress => _transfer?.BytesTotal is > 0;
    public bool CanCancel => _transfer?.CanCancel == true;
    public bool CanRetry => _retryAvailable || _transfer?.Retryable == true;
    public string ProgressLabel => HasProgress
        ? $"{_transfer!.BytesDone:N0} / {_transfer.BytesTotal:N0} 字节 · {Progress:0}%"
        : DownloadStatus;

    public void UpdateVideo(VideoItem value)
    {
        if (_video == value) return;
        _video = value;
        Changed(nameof(Video)); Changed(nameof(Id)); Changed(nameof(Name)); Changed(nameof(Path)); Changed(nameof(FileId));
    }

    public void BeginDownload()
    {
        _transfer = null;
        _retryAvailable = false;
        DownloadInProgress = true;
        DownloadStatus = "正在准备下载";
        DownloadError = null;
        Changed(nameof(Progress)); Changed(nameof(HasProgress)); Changed(nameof(CanCancel)); Changed(nameof(CanRetry));
    }

    public void SetDownloadResult(string status, string? error = null, bool canRetry = false)
    {
        DownloadInProgress = false;
        _retryAvailable = canRetry;
        DownloadStatus = status;
        DownloadError = error;
        Changed(nameof(ProgressLabel)); Changed(nameof(CanRetry));
    }

    public void ApplyTransfer(ProjectTransferTaskSnapshot snapshot)
    {
        if (snapshot.FileId != Video.FileId) return;
        _transfer = snapshot;
        _retryAvailable = false;
        DownloadStatus = snapshot.State switch
        {
            "queued" => "排队中",
            "waiting_stable" => "等待文件稳定",
            "hashing" => "校验中",
            "transferring" => "下载中",
            "verifying" => "校验下载",
            "staged" or "committing" => "提交中",
            "paused" => "已暂停",
            "waiting_network" or "offline" => "等待网络",
            "completed" => "下载完成，正在打开",
            "conflict" => "本地路径冲突",
            "failed" => "下载失败",
            "cancelled" or "canceled" => "已取消",
            _ => "等待下载状态"
        };
        DownloadInProgress = snapshot.State is "queued" or "waiting_stable" or "hashing" or "transferring"
            or "verifying" or "staged" or "committing" or "paused" or "waiting_network" or "offline";
        DownloadError = snapshot.State is "failed" or "conflict" ? snapshot.Message : null;
        Changed(nameof(Progress)); Changed(nameof(ProgressLabel)); Changed(nameof(HasProgress));
        Changed(nameof(CanCancel)); Changed(nameof(CanRetry));
    }

    public void SetLocalPath(string path)
    {
        LocalPath = System.IO.Path.GetFullPath(path);
    }
}

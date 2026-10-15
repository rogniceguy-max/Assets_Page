using System.Collections.ObjectModel;
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
    public string Details => string.Join(" · ", new[] { Asset.Category, Asset.Variant, Asset.RevisionLabel }.Where(s => !string.IsNullOrWhiteSpace(s)));
    private string? _error = error;
    public string? Error { get => _error; private set { _error = value; Changed(); } }
    internal bool Loading { get; set; }
    public void SetPreview(ImageSource? source, string? error = null) { Preview = source; Error = error; }
    public void UpdateAsset(ImageAsset asset)
    {
        Asset = asset;
        Changed(nameof(Asset)); Changed(nameof(Name)); Changed(nameof(Details));
    }
    private ReviewSummary? _summary;
    public string SummaryText => _summary is { } summary
        ? $"好 {summary.Good} · 不好 {summary.Bad} · 评论 {summary.CommentCount}" : "好 ? · 不好 ? · 评论 ?";
    public void SetSummary(ReviewSummary? summary) { _summary = summary; Changed(nameof(SummaryText)); }
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
            Changed(nameof(FinalVideo));
            Changed(nameof(DiscardedVideos));
            Changed(nameof(HasVideo));
            Changed(nameof(IsFinal));
            Changed(nameof(DiscardedCount));
            Changed(nameof(HasDiscarded));
            Changed(nameof(VideoDisplayName));
            Changed(nameof(FinalBadgeText));
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
    public string LabelLevelDraft { get => _labelLevelDraft; set { _labelLevelDraft = value; Changed(); } }

    public VideoItem? Video => Row.Video;
    public IReadOnlyList<VideoItem> Videos =>
        Row.Videos.Count > 0 ? Row.Videos : (Row.Video != null ? [Row.Video] : []);

    public IReadOnlyList<SceneVideoItem> VideoItems
    {
        get
        {
            var rawList = Row.Videos.Count > 0 ? Row.Videos : (Row.Video != null ? [Row.Video] : []);
            return rawList.Select(v => new SceneVideoItem(v)).ToList();
        }
    }

    public bool HasVideos => VideoItems.Count > 0;
    public VideoItem? FinalVideo => Row.FinalVideo;
    public IReadOnlyList<VideoItem> DiscardedVideos => Row.DiscardedVideos;

    public bool HasVideo => Video != null;
    public bool IsFinal => FinalVideo != null && Video != null &&
        (FinalVideo.Id == Video.Id || FinalVideo.Name == Video.Name);
    public int DiscardedCount => Row.DiscardedVideos.Count;
    public bool HasDiscarded => DiscardedCount > 0;

    public string VideoDisplayName => Video?.Name ?? "未关联视频";
    public string FinalBadgeText => IsFinal ? "成片" : "素材";

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
        Changed(nameof(FinalVideo));
        Changed(nameof(DiscardedVideos));
        Changed(nameof(HasVideo));
        Changed(nameof(IsFinal));
        Changed(nameof(DiscardedCount));
        Changed(nameof(HasDiscarded));
        Changed(nameof(VideoDisplayName));
        Changed(nameof(FinalBadgeText));
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
public sealed record GenericCellItem(string Label, string? Value);
public sealed record GenericRowItem(IReadOnlyList<GenericCellItem> Cells);
public sealed class SheetItem(WorkbookSheet sheet)
{
    public WorkbookSheet Sheet { get; } = sheet;
    public string Name => Sheet.Name;
    public override string ToString() => Name;
    public bool IsAsset => Sheet.Kind == "asset_review";
    public bool IsScene => Sheet.Kind == "scene_beats";
    public bool IsGeneric => Sheet.Kind == "generic";
    public ObservableCollection<AssetRowItem> Assets { get; } = [];
    public ObservableCollection<SceneRowItem> Scenes { get; } = new(sheet.SceneRows.Select(row => new SceneRowItem(row)));
    public IReadOnlyList<GenericRowItem> GenericRows { get; } = sheet.GenericRows.Select(row =>
        new GenericRowItem(sheet.Columns.Select(col => new GenericCellItem(col.Label, row.Cells.GetValueOrDefault(col.Key))).ToList())).ToList();
}

public sealed class ProjectWorkbooksViewModel(IProjectWorkbookService service) : ObservableModel
{
    public IProjectWorkbookService Service { get; } = service;
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
    public bool Ready => !Busy;
    private string? _error;
    public string? Error { get => _error; private set { _error = value; Changed(); Changed(nameof(CanReload)); } }
    public bool CanReload => !string.IsNullOrEmpty(Error);
    private WorkbookDocument? _document;
    public WorkbookDocument? Document { get => _document; private set { _document = value; Changed(); Changed(nameof(IsHome)); Changed(nameof(IsOpen)); Changed(nameof(Notice)); } }
    public bool IsHome => Document == null;
    public bool IsOpen => Document != null;
    public string Notice => Document == null ? "" : string.Join("\n", (Document.Fidelity == "values_only"
        ? new[] { "当前显示全部单元格内容；原 Excel 样式与公式重算不在此视图中提供，可下载原文件。" } : Array.Empty<string>()).Concat(Document.Notices).Distinct());
    private SheetItem? _selectedSheet;
    public SheetItem? SelectedSheet { get => _selectedSheet; set { _selectedSheet = value; Changed(); Changed(nameof(VisibleScenes)); Changed(nameof(VisibleAssets)); } }
    private string _search = "";
    public string Search { get => _search; set { _search = value; Changed(); Changed(nameof(VisibleScenes)); Changed(nameof(VisibleAssets)); } }
    private string _levelFilter = "全部等级";
    public string LevelFilter { get => _levelFilter; set { _levelFilter = value; Changed(); Changed(nameof(VisibleScenes)); } }
    public IReadOnlyList<string> LevelFilters { get; } = ["全部等级", "红", "黄", "绿", "未标注"];
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

    public Task OpenAsync(ProjectFileItem item) => OpenAsync(item.File);
    public async Task OpenAsync(ProjectFile file)
    {
        if (ConfirmLeaving?.Invoke() == false) return;
        if (!string.IsNullOrWhiteSpace(file.ProjectId)) await EnsureLiveUpdatesAsync(file.ProjectId);
        var sequenceBeforeLoad = _lastEventSequence;
        var document = await Service.OpenAsync(file.Id);
        var sheets = new List<SheetItem>();
        foreach (var source in document.Sheets)
        {
            if (source.Kind is not ("asset_review" or "scene_beats" or "generic"))
                throw new InvalidDataException("存在尚不支持的工作表类型，请下载原文件查看。");
            var sheet = new SheetItem(source);
            foreach (var row in source.AssetRows) sheet.Assets.Add(MakeRow(row));
            sheets.Add(sheet);
        }
        Document = document;
        Sheets.Clear(); foreach (var sheet in sheets) Sheets.Add(sheet);
        SelectedSheet = Sheets.FirstOrDefault(s => s.IsScene || s.IsAsset) ?? Sheets.FirstOrDefault();
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
            try { ProjectImageChanged?.Invoke(await Service.GetProjectImageAsync(change.EntityId, ct)); }
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
        var document = await Service.OpenAsync(current.File.Id, ct);
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

    public ObservableCollection<AssetRowItem> ProjectAssets { get; } = [];
    public string? ShareRoot { get; set; }

    public async Task LoadProjectAssetsAsync(CancellationToken ct = default)
    {
        if (Document == null || SelectedProject == null) return;
        var assetFile = Files.FirstOrDefault(f => f.TemplateKind == "asset_review");
        if (assetFile == null) return;
        try
        {
            var doc = await Service.OpenAsync(assetFile.Id, ct);
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
        if (Document == null) return;
        await Service.AddSheetAsync(Document.File.Id, sheetName);
        await OpenAsync(Document.File);
        var created = Sheets.FirstOrDefault(s => s.Name == sheetName);
        if (created != null) SelectedSheet = created;
    }

    public async Task AddSceneRowAsync()
    {
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
        if (Document == null || !item.Row.CanEdit) return;
        await Service.DeleteRowAsync(Document.File.Id, item.Row.Id, "scene-rows", item.Row.Version);
        foreach (var sheet in Sheets) sheet.Scenes.Remove(item);
        SceneChanged();
    }

    public async Task DeleteAssetRowAsync(AssetRowItem item)
    {
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

    public void OpenAssetFolder(AssetRowItem item)
    {
        if (SelectedProject == null) return;
        var shareRoot = ShareRoot ?? "\\\\192.168.1.153\\Assets";
        var relative = item.Row.AssetFolderPath;
        if (string.IsNullOrWhiteSpace(relative))
        {
            var category = string.IsNullOrWhiteSpace(item.Row.Category) ? "角色" : item.Row.Category;
            var name = string.IsNullOrWhiteSpace(item.Row.Name) ? "未命名" : item.Row.Name;
            relative = Path.Combine(category, name);
        }
        var folderPath = Path.Combine(shareRoot, "Project", SelectedProject.Name, relative);
        try
        {
            if (!Directory.Exists(folderPath))
            {
                Error = "素材目录尚未创建。请先填写并保存资产类别和名称。";
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Error = $"打开资产文件夹失败：{ex.Message}";
        }
    }
    public void Back()
    {
        if (ConfirmLeaving?.Invoke() == false) return;
        StopLiveUpdates();
        Document = null; SelectedSheet = null; Sheets.Clear();
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
            if (!_thumbnailCache.TryGetValue(image.Asset.Id, out var preview))
            {
                preview = Decode(await Service.GetImageContentAsync(image.Asset.Id, true, ct), thumbnail: true);
                if (_thumbnailCache.Count >= 128) _thumbnailCache.Remove(_thumbnailCache.Keys.First());
                _thumbnailCache[image.Asset.Id] = preview;
            }
            ct.ThrowIfCancellationRequested(); image.SetPreview(preview);
        }
        catch (OperationCanceledException) { }
        catch { image.SetPreview(null, "缩略图暂不可用；点击图片仍可重试大图。"); }
        finally { image.Loading = false; if (entered) _thumbnailGate.Release(); }
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
    public async Task RefreshReviewSummariesAsync(string fileId, AssetRowItem item)
    {
        if (item.SummaryLoading) return; item.SummaryLoading = true;
        try { item.SetSummaries(await Service.GetReviewSummariesAsync(fileId, item.Row.Id)); item.SummaryLoaded = true; }
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
    public async Task UpdateImagesAsync(AssetRowItem item, string? selectedId, IEnumerable<string> candidateIds, string? reason = null)
    {
        if (Document == null || SelectedSheet == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行图片的权限。");
        var ids = candidateIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > 100) throw new InvalidDataException("每行最多保留 100 张备选图片。");
        var sheet = SelectedSheet;
        var updated = await Service.UpdateImagesAsync(Document.File.Id, item.Row.Id,
            new UpdateImagesRequest(item.Row.FieldVersions.GetValueOrDefault("image_selection", 1), selectedId, ids, reason));
        // Replace only after server success. A conflict never mutates local references and never retries.
        var index = sheet.Assets.IndexOf(item);
        if (index >= 0) sheet.Assets[index] = MakeRow(updated);
        AssetChanged();
    }
    public async Task UpdateStoryContentAsync(SceneRowItem item)
    {
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
        if (Document == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行的权限。");
        var sceneChanged = !string.Equals(item.SceneNumberDraft, item.Row.SceneNumber, StringComparison.Ordinal);
        var beatChanged = !string.Equals(item.BeatNumberDraft, item.Row.BeatNumber, StringComparison.Ordinal);
        var storyChanged = !string.Equals(item.StoryContentDraft, item.Row.StoryContent, StringComparison.Ordinal);
        var planChanged = !string.Equals(item.BreakdownPlanDraft, item.Row.BreakdownPlan, StringComparison.Ordinal);
        var noteChanged = !string.Equals(item.NoteDraft, item.Row.Note, StringComparison.Ordinal);
        var level = item.LabelLevelDraft == "未标注" ? null : item.LabelLevelDraft;
        var levelChanged = !string.Equals(level, item.Row.LabelLevel, StringComparison.Ordinal);

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
                LabelLevel: level
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
        if (Document == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行的权限。");
        await ExecuteAsync(async () =>
        {
            var req = new UpdateSceneRowRequest(
                ExpectedVersion: item.Row.Version,
                VideoAction: action,
                Video: video,
                VideoId: videoId,
                VideoName: videoName
            );
            var updated = await Service.UpdateSceneRowAsync(Document.File.Id, item.Row.Id, req);
            item.ApplyRemote(updated);
            SceneChanged();
        });
    }

    public async Task AttachVideoFileAsync(SceneRowItem item, string sourceLocalPath)
    {
        if (Document == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行的权限。");

        var fileName = Path.GetFileName(sourceLocalPath);
        var targetPath = sourceLocalPath;

        if (!string.IsNullOrWhiteSpace(ShareRoot) && SelectedProject is { } project)
        {
            try
            {
                var rawEpisode = Document.File.Name ?? "第1集";
                var episode = rawEpisode.Replace("_节拍表", "", StringComparison.OrdinalIgnoreCase)
                                        .Replace(".xlsx", "", StringComparison.OrdinalIgnoreCase)
                                        .Trim();
                if (string.IsNullOrEmpty(episode) || episode.Equals("节拍表", StringComparison.OrdinalIgnoreCase))
                    episode = "第1集";

                var beatNum = !string.IsNullOrWhiteSpace(item.BeatNumberDraft) ? item.BeatNumberDraft : item.SceneNumberDraft;
                if (string.IsNullOrWhiteSpace(beatNum)) beatNum = "1";
                var beatFolder = $"第{beatNum}beat";

                var targetDir = Path.Combine(ShareRoot, "项目", project.Name, episode, beatFolder);
                Directory.CreateDirectory(targetDir);
                var destPath = Path.Combine(targetDir, fileName);
                if (!string.Equals(Path.GetFullPath(sourceLocalPath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceLocalPath, destPath, overwrite: true);
                }
                targetPath = destPath;
            }
            catch (Exception)
            {
                targetPath = sourceLocalPath;
            }
        }

        var videoItem = new VideoItem(
            Id: Guid.NewGuid().ToString("D"),
            Name: fileName,
            Path: targetPath
        );

        await UpdateSceneVideoAsync(item, "add", videoItem);
    }
}

public sealed class SceneVideoItem(VideoItem video) : ObservableModel
{
    public VideoItem Video { get; } = video;
    public string Id => Video.Id ?? string.Empty;
    public string Name => Video.Name;
    public string? Path => Video.Path;
}

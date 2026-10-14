using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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
    public ImageAsset Asset { get; } = asset;
    private ImageSource? _preview = preview;
    public ImageSource? Preview { get => _preview; private set { _preview = value; Changed(); } }
    public string Name => Asset.Name;
    public string Details => string.Join(" · ", new[] { Asset.Category, Asset.Variant, Asset.RevisionLabel }.Where(s => !string.IsNullOrWhiteSpace(s)));
    private string? _error = error;
    public string? Error { get => _error; private set { _error = value; Changed(); } }
    internal bool Loading { get; set; }
    public void SetPreview(ImageSource? source, string? error = null) { Preview = source; Error = error; }
    private ReviewSummary? _summary;
    public string SummaryText => _summary is { } summary
        ? $"好 {summary.Good} · 不好 {summary.Bad} · 评论 {summary.CommentCount}" : "好 ? · 不好 ? · 评论 ?";
    public void SetSummary(ReviewSummary? summary) { _summary = summary; Changed(nameof(SummaryText)); }
}
public sealed class AssetRowItem(AssetRow row, ImageItem? selected, IReadOnlyList<ImageItem> candidates) : ObservableModel
{
    public AssetRow Row { get; } = row;
    public ImageItem? Selected { get; } = selected;
    public IReadOnlyList<ImageItem> Candidates { get; } = candidates;
    internal bool SummaryLoading { get; set; }
    internal bool SummaryLoaded { get; set; }
    private ReviewSummary? _generalSummary;
    public string GeneralSummaryText => _generalSummary is { } summary ? $"整条记录讨论 · 评论 {summary.CommentCount}" : "整条记录讨论 · 评论 ?";
    public void SetSummaries(IReadOnlyList<ReviewSummary>? summaries)
    {
        _generalSummary = summaries?.FirstOrDefault(s => s.ImageId == null);
        Selected?.SetSummary(summaries?.FirstOrDefault(s => s.ImageId == Selected.Asset.Id));
        foreach (var image in Candidates) image.SetSummary(summaries?.FirstOrDefault(s => s.ImageId == image.Asset.Id));
        Changed(nameof(GeneralSummaryText));
    }
}
public sealed class SceneRowItem(SceneRow row) : ObservableModel
{
    private SceneRow _row = row;
    public SceneRow Row { get => _row; set { _row = value; Changed(); Changed(nameof(StoryPreview)); Changed(nameof(PlanPreview)); } }
    public string StoryPreview => Row.StoryContent ?? "";
    public string PlanPreview => Row.BreakdownPlan ?? "";
    private string _discussionText = "讨论 ?";
    public string DiscussionText { get => _discussionText; set { _discussionText = value; Changed(); } }
    internal bool SummaryLoaded { get; set; }
    internal bool SummaryLoading { get; set; }
}
public sealed record GenericCellItem(string Label, string? Value);
public sealed record GenericRowItem(IReadOnlyList<GenericCellItem> Cells);
public sealed class SheetItem(WorkbookSheet sheet)
{
    public WorkbookSheet Sheet { get; } = sheet;
    public string Name => Sheet.Name;
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
    private sealed record PendingAssetRowCreate(string FileId, string SheetId, string ProjectId, CreateAssetRowRequest Request);
    private PendingAssetRowCreate? _pendingAssetRowCreate;

    public IProjectWorkbookService Service { get; } = service;
    public event Action<ProjectSpaceAssetFolderRecord>? AssetFolderCreated;
    public event Action<ProjectSpaceRecycleBinRecord>? AssetImageArchived;
    public event Action? BackToProjectRequested;
    public ObservableCollection<ProjectFile> Files { get; } = [];
    public ObservableCollection<ProjectInfo> Projects { get; } = [];
    private ProjectInfo? _selectedProject;
    public ProjectInfo? SelectedProject { get => _selectedProject; set { _selectedProject = value; Changed(); } }
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
    public SheetItem? SelectedSheet { get => _selectedSheet; set { _selectedSheet = value; Changed(); Changed(nameof(VisibleScenes)); } }
    private string _search = "";
    public string Search { get => _search; set { _search = value; Changed(); Changed(nameof(VisibleScenes)); } }
    private string _levelFilter = "全部等级";
    public string LevelFilter { get => _levelFilter; set { _levelFilter = value; Changed(); Changed(nameof(VisibleScenes)); } }
    public IReadOnlyList<string> LevelFilters { get; } = ["全部等级", "红", "黄", "绿", "未标注"];
    public IEnumerable<SceneRowItem> VisibleScenes => (SelectedSheet?.Scenes.AsEnumerable() ?? []).Where(item =>
        (LevelFilter == "全部等级" || (LevelFilter == "未标注" ? string.IsNullOrWhiteSpace(item.Row.LabelLevel) : item.Row.LabelLevel == LevelFilter)) &&
        (string.IsNullOrWhiteSpace(Search) || string.Join(" ", item.Row.SceneNumber, item.Row.BeatNumber, item.Row.StoryContent,
            item.Row.BreakdownPlan, item.Row.SceneContext, item.Row.CastContext).Contains(Search, StringComparison.OrdinalIgnoreCase)));
    private readonly SemaphoreSlim _thumbnailGate = new(4);
    private readonly Dictionary<string, ImageSource> _thumbnailCache = new(StringComparer.Ordinal);
    public PendingWorkbookOperation? PendingOperation { get; private set; }
    public string PendingText => PendingOperation == null ? "" : $"待确认：{PendingOperation.Description}。可继续重试这次操作。";
    public bool HasPendingOperation => PendingOperation != null;
    public bool HasPendingAssetRowCreate => _pendingAssetRowCreate != null;
    public bool CanCreateAssetRow => !HasPendingAssetRowCreate;
    public void SetPending(PendingWorkbookOperation? operation)
    {
        if (operation == null && PendingOperation != null) PendingWorkbookOperation.Remove(PendingOperation.Scope, PendingOperation.OperationId);
        if (operation != null) PendingWorkbookOperation.Save(operation);
        PendingOperation = operation ?? PendingWorkbookOperation.Load(Service.OperationScope);
        Changed(nameof(PendingText)); Changed(nameof(HasPendingOperation));
    }
    public async Task InitializeAsync()
    {
        var requestedProjectId = SelectedProject?.Id;
        var projects = await Service.ListProjectsAsync();
        Projects.Clear(); foreach (var project in projects) Projects.Add(project);
        PendingOperation = PendingWorkbookOperation.Load(Service.OperationScope); Changed(nameof(PendingText)); Changed(nameof(HasPendingOperation));
        SelectedProject = Projects.FirstOrDefault(p => p.Id == requestedProjectId)
            ?? Projects.FirstOrDefault(p => p.Id == PendingOperation?.ProjectId)
            ?? Projects.FirstOrDefault();
        await RefreshFilesAsync();
    }

    public async Task SelectProjectAsync(ProjectInfo project)
    {
        SelectedProject = Projects.FirstOrDefault(item => item.Id == project.Id) ?? project;
        ClearDocument();
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
        catch (Exception ex) { Error = ex is WorkbookApiException or InvalidDataException ? ex.Message : "操作未完成，请检查连接后重新加载。"; }
        finally { Busy = false; }
    }
    public async Task RefreshFilesAsync()
    {
        if (SelectedProject == null) { Files.Clear(); return; }
        var files = await Service.ListProjectFilesAsync(SelectedProject.Id);
        Files.Clear(); foreach (var file in files) Files.Add(file);
    }
    public async Task OpenAsync(ProjectFile file)
    {
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
    }
    public void Back()
    {
        ClearDocument();
        BackToProjectRequested?.Invoke();
    }
    private void ClearDocument() { Document = null; SelectedSheet = null; Sheets.Clear(); }
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
            var count = summaries.FirstOrDefault(s => s.ImageId == null)?.CommentCount;
            item.DiscussionText = count == null ? "讨论 ?" : $"讨论 {count}"; item.SummaryLoaded = true; }
        catch { item.DiscussionText = "讨论 ?"; }
        finally { item.SummaryLoading = false; }
    }
    public void SceneChanged() => Changed(nameof(VisibleScenes));
    public async Task UpdateImagesAsync(AssetRowItem item, string? selectedId, IEnumerable<string> candidateIds)
    {
        var document = Document;
        var sheet = SelectedSheet;
        if (document == null || sheet == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行图片的权限。");
        var ids = candidateIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > 100) throw new InvalidDataException("每行最多保留 100 张备选图片。");
        var updated = await Service.UpdateImagesAsync(document.File.Id, item.Row.Id,
            new UpdateImagesRequest(item.Row.Version, selectedId, ids));
        // Replace only after server success. A conflict never mutates local references and never retries.
        var index = sheet.Assets.IndexOf(item);
        if (index >= 0) sheet.Assets[index] = MakeRow(updated);
        if (Document is { } currentDocument && currentDocument.File.Id == document.File.Id &&
            currentDocument.File.Version == document.File.Version && updated.WorkbookVersion is { } workbookVersion)
            Document = currentDocument with { File = currentDocument.File with { Version = workbookVersion } };
    }

    public async Task<CreateAssetRowResult> CreateAssetRowAsync(CreateAssetRowRequest request)
    {
        var document = Document;
        var sheet = SelectedSheet;
        if (document == null || sheet == null || !sheet.IsAsset)
            throw new InvalidDataException("请先打开资产表。");
        var projectId = document.File.ProjectId ?? SelectedProject?.Id
            ?? throw new InvalidDataException("资产表没有关联项目。");
        if (request.ExpectedWorkbookVersion != document.File.Version ||
            !Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.Category) || request.Category.Length > 200 ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 ||
            (request.Variant?.Length ?? 0) > 200 || (request.Appearance?.Length ?? 0) > 4000)
            throw new InvalidDataException("资产信息无效或工作簿已变更，请重新加载后重试。");
        if (_pendingAssetRowCreate is { } pending)
        {
            if (pending.Request.OperationId != request.OperationId || pending.FileId != document.File.Id ||
                pending.SheetId != sheet.Sheet.Id || pending.ProjectId != projectId)
                throw new InvalidDataException("上次资产创建结果待确认，请先重试同一操作。");
        }
        else
        {
            _pendingAssetRowCreate = new PendingAssetRowCreate(document.File.Id, sheet.Sheet.Id, projectId, request);
            Changed(nameof(HasPendingAssetRowCreate));
            Changed(nameof(CanCreateAssetRow));
        }

        try
        {
            var result = await Service.CreateAssetRowAsync(document.File.Id, sheet.Sheet.Id, request);
            if (result.WorkbookVersion < request.ExpectedWorkbookVersion)
                throw new InvalidDataException("服务端返回的工作簿版本无效，请重新加载确认。");
            if (result.Row.ImageFolder is not { } folder || folder.ProjectId != projectId || folder.AssetRowId != result.Row.Id)
                throw new InvalidDataException("资产行已保存，但接口没有返回有效的图片文件夹映射；请重新加载项目树。");

            var existingIndex = sheet.Assets.ToList().FindIndex(item => item.Row.Id == result.Row.Id);
            if (existingIndex >= 0) sheet.Assets[existingIndex] = MakeRow(result.Row);
            else sheet.Assets.Add(MakeRow(result.Row));
            if (Document is { } currentDocument && currentDocument.File.Id == document.File.Id &&
                currentDocument.File.Version == document.File.Version)
                Document = currentDocument with { File = currentDocument.File with { Version = result.WorkbookVersion } };
            _pendingAssetRowCreate = null;
            Changed(nameof(HasPendingAssetRowCreate));
            Changed(nameof(CanCreateAssetRow));
            AssetFolderCreated?.Invoke(folder);
            return result;
        }
        catch (WorkbookApiException ex) when (ex.StatusCode is >= 400 and < 500 and not 408)
        {
            // A definite client rejection did not create a row. Keep only outcomes that may be unknown.
            _pendingAssetRowCreate = null;
            Changed(nameof(HasPendingAssetRowCreate));
            Changed(nameof(CanCreateAssetRow));
            throw;
        }
    }

    public Task<CreateAssetRowResult> RetryPendingAssetRowCreateAsync()
    {
        var pending = _pendingAssetRowCreate ?? throw new InvalidDataException("没有待确认的资产创建操作。");
        if (Document?.File.Id != pending.FileId || SelectedSheet?.Sheet.Id != pending.SheetId)
            throw new InvalidDataException("请重新打开原资产表，再重试这次操作。");
        return CreateAssetRowAsync(pending.Request);
    }

    public async Task<ArchiveAssetRowImageResult> ArchiveAssetRowImageAsync(AssetRowItem item, ImageItem image)
    {
        var document = Document;
        var sheet = SelectedSheet;
        if (document == null || sheet == null)
            throw new InvalidDataException("工作表已关闭，请重新打开后操作。");
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行图片的权限。");

        var result = await Service.ArchiveAssetRowImageAsync(
            document.File.Id, item.Row.Id, image.Asset.Id, item.Row.Version);
        if (result.WorkbookVersion < document.File.Version)
            throw new InvalidDataException("服务端返回的工作簿版本无效，请重新加载确认。");

        var index = sheet.Assets.IndexOf(item);
        if (index >= 0) sheet.Assets[index] = MakeRow(result.Row);
        if (Document is { } currentDocument && currentDocument.File.Id == document.File.Id &&
            currentDocument.File.Version == document.File.Version)
            Document = currentDocument with { File = currentDocument.File with { Version = result.WorkbookVersion } };
        AssetImageArchived?.Invoke(result.RecycleBinItem);
        return result;
    }

    public async Task<AssetRowItem> UploadImageToAssetRowAsync(AssetRowItem item, string path, bool selected)
    {
        var document = Document;
        var sheet = SelectedSheet;
        if (document == null || sheet == null || !sheet.Assets.Any(candidate => candidate.Row.Id == item.Row.Id))
            throw new InvalidDataException("工作表已关闭，请重新打开后操作。");
        if (item.Row.ImageFolder is not { } folder)
            throw new InvalidDataException("此资产行尚未关联图片文件夹，请重新加载或检查服务端数据。");
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行图片的权限。");

        var projectId = document.File.ProjectId ?? SelectedProject?.Id
            ?? throw new InvalidDataException("资产表没有关联项目。");
        var mediaType = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png" : "image/jpeg";
        await using var source = File.OpenRead(path);
        var updated = await Service.UploadImageToAssetRowAsync(
            document.File.Id, item.Row.Id, projectId, folder.Id, item.Row.Version,
            selected, Path.GetFileName(path), mediaType, source);
        if (updated.WorkbookVersion is { } workbookVersion && workbookVersion < document.File.Version)
            throw new InvalidDataException("服务端返回的工作簿版本无效，请重新加载确认。");
        var replacement = MakeRow(updated);
        var currentIndex = sheet.Assets.ToList().FindIndex(candidate => candidate.Row.Id == item.Row.Id);
        if (currentIndex >= 0) sheet.Assets[currentIndex] = replacement;
        if (Document is { } currentDocument && currentDocument.File.Id == document.File.Id &&
            currentDocument.File.Version == document.File.Version && updated.WorkbookVersion is { } updatedWorkbookVersion)
            Document = currentDocument with { File = currentDocument.File with { Version = updatedWorkbookVersion } };
        return replacement;
    }

}

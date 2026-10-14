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
    public ObservableCollection<LinkedAsset> LinkedAssets { get; } = [];
    public ObservableCollection<ImageItem> LinkedThumbnails { get; } = [];
    public bool HasLinkedAssets => LinkedAssets.Count > 0;

    public void ResolveLinkedAssets(IEnumerable<AssetRowItem> assets)
    {
        LinkedAssets.Clear();
        LinkedThumbnails.Clear();
        var content = string.Join(" ", Row.StoryContent, Row.CastContext, Row.SceneContext);
        if (string.IsNullOrWhiteSpace(content)) return;

        foreach (var asset in assets)
        {
            var name = asset.Row.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (content.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                var linked = new LinkedAsset(asset.Row.Category ?? "角色", name, asset.Row.Appearance, asset.Selected?.Asset ?? asset.Row.CandidateImages.FirstOrDefault());
                LinkedAssets.Add(linked);
                if (asset.Selected != null)
                {
                    LinkedThumbnails.Add(asset.Selected);
                }
            }
        }
        Changed(nameof(HasLinkedAssets));
        Changed(nameof(LinkedAssets));
        Changed(nameof(LinkedThumbnails));
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
        catch (Exception ex) { Error = ex is WorkbookApiException or InvalidDataException ? ex.Message : "操作未完成，请检查连接后重新加载。"; }
        finally { Busy = false; }
    }
    public async Task RefreshFilesAsync()
    {
        if (SelectedProject == null) { Files.Clear(); return; }
        var files = await Service.ListProjectFilesAsync(SelectedProject.Id);
        Files.Clear(); foreach (var file in files) Files.Add(file);
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

    public async Task OpenAsync(ProjectFile file)
    {
        if (ConfirmLeaving?.Invoke() == false) return;
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
        if (file.TemplateKind == "scene_beats")
        {
            _ = LoadProjectAssetsAsync();
        }
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
                    if (item.Selected != null) _ = EnsureThumbnailAsync(item.Selected, ct);
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

    public void OpenAssetFolder(AssetRowItem item)
    {
        if (SelectedProject == null) return;
        var shareRoot = ShareRoot ?? "\\\\192.168.1.153\\Assets";
        var category = string.IsNullOrWhiteSpace(item.Row.Category) ? "角色" : item.Row.Category;
        var name = string.IsNullOrWhiteSpace(item.Row.Name) ? "未命名" : item.Row.Name;
        var folderPath = Path.Combine(shareRoot, "Project", SelectedProject.Name, category, name);
        try
        {
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
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
            var count = summaries.FirstOrDefault(s => s.ImageId == null)?.CommentCount;
            item.DiscussionText = count == null ? "讨论 ?" : $"讨论 {count}"; item.SummaryLoaded = true; }
        catch { item.DiscussionText = "讨论 ?"; }
        finally { item.SummaryLoading = false; }
    }
    public void SceneChanged() => Changed(nameof(VisibleScenes));
    public async Task UpdateImagesAsync(AssetRowItem item, string? selectedId, IEnumerable<string> candidateIds, string? reason = null)
    {
        if (Document == null || SelectedSheet == null) return;
        if (!item.Row.CanEdit) throw new InvalidDataException("当前账号没有修改此行图片的权限。");
        var ids = candidateIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > 100) throw new InvalidDataException("每行最多保留 100 张备选图片。");
        var sheet = SelectedSheet;
        var updated = await Service.UpdateImagesAsync(Document.File.Id, item.Row.Id,
            new UpdateImagesRequest(item.Row.Version, selectedId, ids, reason));
        // Replace only after server success. A conflict never mutates local references and never retries.
        var index = sheet.Assets.IndexOf(item);
        if (index >= 0) sheet.Assets[index] = MakeRow(updated);
    }
}

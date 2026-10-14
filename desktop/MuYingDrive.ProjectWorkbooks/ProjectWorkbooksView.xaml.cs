using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace MuYingDrive.ProjectWorkbooks;

public partial class ProjectWorkbooksView : UserControl
{
    private bool _loaded;
    private ProjectWorkbooksViewModel Model => DataContext as ProjectWorkbooksViewModel
        ?? throw new InvalidOperationException("宿主必须提供 ProjectWorkbooksViewModel。");
    public ProjectWorkbooksView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AdoptHostTheme();
        if (_loaded || DataContext is not ProjectWorkbooksViewModel) return;
        _loaded = true;
        await Model.ExecuteAsync(Model.InitializeAsync);
    }
    private void AdoptHostTheme()
    {
        var ancestor = Parent as FrameworkElement;
        foreach (var key in new[] { "Brush.Surface.Canvas", "Brush.Surface.Panel", "Brush.Border.Subtle", "Brush.Text.Primary", "Brush.Text.Secondary", "Brush.Accent.Primary", "Brush.Status.Danger", "Typography.FontFamily", "Typography.Caption", "Button.Primary", "Button.Secondary" })
        {
            var resource = ancestor?.TryFindResource(key) ?? Application.Current?.TryFindResource(key);
            // Remove local fallback so DynamicResource follows the host's changing theme dictionaries.
            if (resource != null) Resources.Remove(key);
        }
        if (ancestor?.TryFindResource("Button.Secondary") is Style || Application.Current?.TryFindResource("Button.Secondary") is Style)
        {
            var style = ancestor?.TryFindResource("Button.Secondary") ?? Application.Current?.TryFindResource("Button.Secondary");
            Resources[typeof(Button)] = style;
        }
    }
    private static AssetRowItem? Row(object sender)
    {
        DependencyObject? current = sender as DependencyObject;
        while (current != null)
        {
            if (current is FrameworkElement { DataContext: AssetRowItem item }) return item;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
    private static string[] Paths(DragEventArgs e) => e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
    private static bool IsWorkbook(string path) => string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase);
    private static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";
    private static void Drag(DragEventArgs e, Func<string, bool> accept)
    {
        var paths = Paths(e);
        e.Effects = paths.Length > 0 && paths.All(accept) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void OnWorkbookDragOver(object sender, DragEventArgs e) => Drag(e, IsWorkbook);
    private void OnImageDragOver(object sender, DragEventArgs e)
    { if (Row(sender) is { Row.CanEdit: true }) Drag(e, IsImage); else { e.Effects = DragDropEffects.None; e.Handled = true; } }
    private async void OnWorkbookDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = Paths(e);
        await Model.ExecuteAsync(() => ImportPathsAsync(paths));
    }
    private async Task ImportPathsAsync(string[] paths)
    {
        if (paths.Length == 0 || paths.Any(path => !IsWorkbook(path))) throw new InvalidDataException("项目只接收 .xlsx 文件。");
        foreach (var path in paths)
        {
            if (Model.HasPendingOperation) throw new InvalidDataException("请先处理待确认的导入/新建，再发起新操作。");
            var project = RequireProject();
            Model.SetPending(await PendingWorkbookOperation.ForImportAsync(Model.Service, project, path, null));
            await SubmitPendingAsync();
        }
    }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", Multiselect = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await Model.ExecuteAsync(() => ImportPathsAsync(picker.FileNames));
    }
    private async void OnCreate(object sender, RoutedEventArgs e) => await Model.ExecuteAsync(async () =>
    {
        if (Model.HasPendingOperation) throw new InvalidDataException("请先重试或取消待确认的操作。");
        var kind = (string)((FrameworkElement)sender).Tag;
        Model.SetPending(new PendingWorkbookOperation(Model.Service.OperationScope, Guid.NewGuid().ToString("D"), RequireProject(), kind, null, null, null));
        var file = await SubmitPendingAsync(); await Model.OpenAsync(file);
    });
    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProjectFile file) await Model.ExecuteAsync(() => Model.OpenAsync(file));
    }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Model.ExecuteAsync(async () =>
    {
        if (Model.Document is { } document) await Model.OpenAsync(document.File);
        else if (Model.SelectedProject == null) await Model.InitializeAsync();
        else await Model.RefreshFilesAsync();
    });
    private void OnBack(object sender, RoutedEventArgs e) => Model.Back();
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (Model.Document is not { } document) return;
        var picker = new SaveFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", DefaultExt = ".xlsx", FileName = Path.GetFileName(document.File.Name) };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await Model.ExecuteAsync(async () =>
            await Model.Service.DownloadOriginalToAsync(document.File.Id, picker.FileName));
    }
    private async void OnPickImages(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        if (!row.Row.CanEdit) return;
        var selected = Equals(((FrameworkElement)sender).Tag, "selected");
        await Model.ExecuteAsync(async () =>
        {
            var dialog = new ImagePickerWindow(Model, Model.Document?.File.ProjectId ?? RequireProject(), !selected) { Owner = Window.GetWindow(this) };
            dialog.Resources.MergedDictionaries.Add(Resources);
            if (dialog.ShowDialog() != true) return;
            var ids = dialog.SelectedImages.Select(image => image.Asset.Id).ToList();
            if (ids.Count == 0) return;
            await UpdateImagesWithReasonAsync(row, selected ? ids.Single() : row.Row.SelectedImage?.Id,
                selected ? row.Row.CandidateImages.Select(a => a.Id) : row.Row.CandidateImages.Select(a => a.Id).Concat(ids));
        });
    }
    private async void OnImageDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Row(sender) is not { } row) return;
        if (!row.Row.CanEdit) return;
        var paths = Paths(e); var selected = Equals(((FrameworkElement)sender).Tag, "selected");
        await Model.ExecuteAsync(async () =>
        {
            if (paths.Length == 0 || paths.Any(path => !IsImage(path))) throw new InvalidDataException("请拖入 PNG 或 JPEG 图片。");
            if (selected && paths.Length != 1) throw new InvalidDataException("选定只接收一张图片，请将多张图片拖入备选区。");
            if (!selected && row.Row.CandidateImages.Count + paths.Length > 100) throw new InvalidDataException("每行最多保留 100 张备选图片。");
            var assets = new List<ImageAsset>();
            foreach (var path in paths)
            {
                await using var source = File.OpenRead(path);
                assets.Add(await Model.Service.UploadProjectImageAsync(Model.Document?.File.ProjectId ?? RequireProject(), Path.GetFileName(path), Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg", source));
            }
            await UpdateImagesWithReasonAsync(row, selected ? assets.Single().Id : row.Row.SelectedImage?.Id,
                selected ? row.Row.CandidateImages.Select(a => a.Id) : row.Row.CandidateImages.Select(a => a.Id).Concat(assets.Select(a => a.Id)));
        });
    }
    private async void OnClearSelected(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row) await Model.ExecuteAsync(() => UpdateImagesWithReasonAsync(row, null, row.Row.CandidateImages.Select(a => a.Id)));
    }
    private async void OnUseCandidate(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row && ((FrameworkElement)sender).DataContext is ImageItem image)
            await Model.ExecuteAsync(() => UpdateImagesWithReasonAsync(row, image.Asset.Id, row.Row.CandidateImages.Select(a => a.Id)));
    }
    private async void OnRemoveCandidate(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row && ((FrameworkElement)sender).DataContext is ImageItem image)
            await Model.ExecuteAsync(() => Model.UpdateImagesAsync(row, row.Row.SelectedImage?.Id, row.Row.CandidateImages.Where(a => a.Id != image.Asset.Id).Select(a => a.Id)));
    }
    private async void OnPreviewSelected(object sender, RoutedEventArgs e)
    { if (Row(sender) is { Selected: { } image } row) await ReviewAsync(row, image); }
    private async void OnPreviewCandidate(object sender, RoutedEventArgs e)
    { if (Row(sender) is { } row && ((FrameworkElement)sender).DataContext is ImageItem image) await ReviewAsync(row, image); }
    private async void OnGeneralReview(object sender, RoutedEventArgs e)
    { if (Row(sender) is { } row) await ReviewAsync(row, null); }
    private Task ReviewAsync(AssetRowItem row, ImageItem? image) => Model.ExecuteAsync(async () =>
    {
        if (Model.Document is not { } document) return;
        var fileId = document.File.Id;
        var window = new ReviewWindow(new ReviewViewModel(Model.Service, fileId, row.Row.Id, image))
            { Owner = Window.GetWindow(this) };
        window.Resources.MergedDictionaries.Add(Resources);
        window.ShowDialog();
        await Model.RefreshReviewSummariesAsync(fileId, row);
    });

    private string RequireProject() => Model.SelectedProject?.Id ?? throw new InvalidDataException("请先选择项目。");
    private async Task<ProjectFile> SubmitPendingAsync()
    {
        var pending = Model.PendingOperation ?? throw new InvalidDataException("没有待重试操作。");
        var file = await pending.SubmitAsync(Model.Service);
        Model.SetPending(null);
        if (file.ProjectId == Model.SelectedProject?.Id && !Model.Files.Any(existing => existing.Id == file.Id)) Model.Files.Add(file);
        return file;
    }
    private async void OnRetryPending(object sender, RoutedEventArgs e) => await Model.ExecuteAsync(async () => { await SubmitPendingAsync(); });
    private async void OnCancelPending(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "服务端可能已完成此操作。取消不会删除已导入文件，稍后可重新进入项目查看。仍要取消？", "取消待确认操作", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            await Model.ExecuteAsync(() => { Model.SetPending(null); return Task.CompletedTask; });
    }
    private async void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || Model.Busy) return;
        await Model.ExecuteAsync(async () => { Model.Back(); await Model.RefreshFilesAsync(); });
    }
    private async void OnImportRevision(object sender, RoutedEventArgs e)
    {
        if (Model.Document is not { } document) return;
        var picker = new OpenFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", Multiselect = false };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        await Model.ExecuteAsync(async () =>
        {
            if (Model.HasPendingOperation) throw new InvalidDataException("请先处理待确认操作。");
            Model.SetPending(await PendingWorkbookOperation.ForImportAsync(Model.Service, document.File.ProjectId ?? RequireProject(), picker.FileName, document.File.Id));
            var file = await SubmitPendingAsync(); await Model.OpenAsync(file);
        });
    }
    private async void OnRevisions(object sender, RoutedEventArgs e) => await Model.ExecuteAsync(async () =>
    {
        if (Model.Document is not { } document) return;
        var revisions = await Model.Service.ListRevisionsAsync(document.File.Id);
        var chosen = WorkflowDialogs.Choose(Window.GetWindow(this), Resources, "浏览工作簿修订", revisions,
            file => $"{file.Name}\n{file.RevisionLabel ?? "未命名修订"} · {file.Id}", false);
        if (chosen?.Count == 1) await Model.OpenAsync(chosen[0]);
    });
    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (Model.Document is not { } document) return;
        var picker = new SaveFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", DefaultExt = ".xlsx", FileName = Path.GetFileNameWithoutExtension(document.File.Name) + "-当前版.xlsx" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        await Model.ExecuteAsync(async () =>
        {
            var snapshot = await Model.Service.OpenAsync(document.File.Id);
            await Model.Service.ExportCurrentAsync(document.File.Id, snapshot.File.Version, picker.FileName);
        });
    }
    private async Task UpdateImagesWithReasonAsync(AssetRowItem row, string? selectedId, IEnumerable<string> candidates)
    {
        string? reason = null;
        if (selectedId != row.Row.SelectedImage?.Id)
        {
            reason = WorkflowDialogs.Prompt(Window.GetWindow(this), Resources, selectedId == null ? "清空选定" : "变更选定图片", "可填写选定原因。此操作会记录操作者、时间与图片版本；不会按票数自动选定。");
            if (reason == null) return;
        }
        await Model.UpdateImagesAsync(row, selectedId, candidates, reason);
    }
    private async void OnSelectionHistory(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row || Model.Document is not { } document) return;
        await Model.ExecuteAsync(async () =>
        {
            var history = await Model.Service.GetSelectionHistoryAsync(document.File.Id, row.Row.Id);
            WorkflowDialogs.ShowText(Window.GetWindow(this), Resources, "选定历史", history.Count == 0 ? "暂无选定变更记录。" : string.Join("\n\n", history.Select(entry =>
                $"{entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {entry.UserName}\n{entry.PreviousImageId ?? "未选定"} → {entry.ImageId ?? "已清空"}\n原因：{entry.Reason ?? "未填写"}")));
        });
    }
    private async void OnCompareRow(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        await Model.ExecuteAsync(async () =>
        {
            var images = row.Candidates.Concat(row.Selected == null ? [] : new[] { row.Selected }).DistinctBy(image => image.Asset.Id).ToList();
            var chosen = WorkflowDialogs.Choose(Window.GetWindow(this), Resources, "选择 2–4 张图片比较", images, image => image.Name + "\n" + image.Details, true);
            if (chosen == null) return;
            if (chosen.Count is < 2 or > 4) throw new InvalidDataException("请选择 2–4 张图片。");
            if (Model.Document is { } document) await Model.RefreshReviewSummariesAsync(document.File.Id, row);
            var window = new CompareImagesWindow(Model.Service, chosen) { Owner = Window.GetWindow(this) }; WorkflowDialogs.Theme(window, Resources); window.ShowDialog();
        });
    }
    private static SceneRowItem? Scene(object sender) => (sender as FrameworkElement)?.DataContext as SceneRowItem;
    private static bool FromRowControl(DependencyObject? source, DependencyObject row)
    {
        while (source != null && source != row)
        {
            if (source is ButtonBase or TextBoxBase or Selector or RangeBase) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source)
                : source is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private void OnSceneRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || FromRowControl(e.OriginalSource as DependencyObject, (DependencyObject)sender)) return;
        e.Handled = true; ((FrameworkElement)sender).Focus(); OnSceneDetails(sender, e);
    }
    private void OnSceneRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not (Key.Enter or Key.Space) || FromRowControl(e.OriginalSource as DependencyObject, (DependencyObject)sender)) return;
        e.Handled = true; OnSceneDetails(sender, e);
    }
    private async void OnSceneDetails(object sender, RoutedEventArgs e)
    {
        if (Scene(sender) is not { } row || Model.Document is not { } document) return;
        await Model.ExecuteAsync(async () =>
        {
            new SceneDetailsWindow(Model, document.File.Id, row, Resources) { Owner = Window.GetWindow(this) }.ShowDialog();
            await Model.RefreshSceneSummaryAsync(document.File.Id, row);
        });
    }
    private async void OnSceneDiscussion(object sender, RoutedEventArgs e)
    {
        if (Scene(sender) is not { } row || Model.Document is not { } document) return;
        await Model.ExecuteAsync(async () =>
        {
            var window = new ReviewWindow(new ReviewViewModel(Model.Service, document.File.Id, row.Row.Id, null, "scene-rows")) { Owner = Window.GetWindow(this) };
            WorkflowDialogs.Theme(window, Resources); window.ShowDialog(); await Model.RefreshSceneSummaryAsync(document.File.Id, row);
        });
    }
    private async void OnSceneOrigins(object sender, RoutedEventArgs e)
    {
        if (Scene(sender) is not { } row || Model.Document is not { } document) return;
        await Model.ExecuteAsync(() => { new SceneOriginsWindow(Model, document.File.Id, row, Resources) { Owner = Window.GetWindow(this) }.ShowDialog(); Model.SceneChanged(); return Task.CompletedTask; });
    }
    private bool _viewportScheduled;
    private void OnWorkbookScroll(object sender, ScrollChangedEventArgs e) => ScheduleViewport();
    private void OnRowLoaded(object sender, RoutedEventArgs e) => ScheduleViewport();
    private void ScheduleViewport()
    {
        if (_viewportScheduled) return; _viewportScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            _viewportScheduled = false;
            if (Model.Document is not { } document) return;
            var work = new List<Task>(); var assets = new HashSet<AssetRowItem>(); var scenes = new HashSet<SceneRowItem>();
            foreach (var element in Descendants(WorkbookScroll))
            {
                if (!InViewport(element, WorkbookScroll)) continue;
                if (element is Image && (element.DataContext is ImageItem || element.DataContext is AssetRowItem { Selected: not null }))
                { var picture = element.DataContext as ImageItem ?? ((AssetRowItem)element.DataContext).Selected!; work.Add(Model.EnsureThumbnailAsync(picture)); }
                if (element.DataContext is AssetRowItem asset && !asset.SummaryLoaded && assets.Add(asset)) work.Add(Model.RefreshReviewSummariesAsync(document.File.Id, asset));
                if (element.DataContext is SceneRowItem scene && !scene.SummaryLoaded && scenes.Add(scene)) work.Add(Model.RefreshSceneSummaryAsync(document.File.Id, scene));
            }
            await Task.WhenAll(work);
        }));
    }
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is FrameworkElement element) yield return element; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static bool InViewport(FrameworkElement element, ScrollViewer viewport)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        try { var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)); return bounds.IntersectsWith(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight)); }
        catch (InvalidOperationException) { return false; }
    }
}

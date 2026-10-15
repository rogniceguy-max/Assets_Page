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
    private ProjectWorkbooksViewModel? _lastKnownModel;

    public ProjectWorkbooksView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ProjectWorkbooksViewModel direct)
                _lastKnownModel = direct;
            else if (e.NewValue != null)
            {
                var vm = (e.NewValue as dynamic)?.ViewModel as ProjectWorkbooksViewModel;
                if (vm != null) _lastKnownModel = vm;
            }
        };
    }

    private ProjectWorkbooksViewModel? TryResolveModel(object? sender = null)
    {
        if (DataContext is ProjectWorkbooksViewModel direct) return direct;
        if (DataContext != null)
        {
            var prop = DataContext.GetType().GetProperty("ViewModel");
            if (prop?.GetValue(DataContext) is ProjectWorkbooksViewModel vm) return vm;
        }

        DependencyObject? cur = sender as DependencyObject ?? this;
        while (cur != null)
        {
            if (cur is FrameworkElement fe)
            {
                if (fe.DataContext is ProjectWorkbooksViewModel vm) return vm;
                if (fe.DataContext != null)
                {
                    var prop = fe.DataContext.GetType().GetProperty("ViewModel");
                    if (prop?.GetValue(fe.DataContext) is ProjectWorkbooksViewModel pvm) return pvm;
                }
            }
            cur = VisualTreeHelper.GetParent(cur);
        }

        return _lastKnownModel;
    }

    private ProjectWorkbooksViewModel Model =>
        TryResolveModel()
        ?? throw new InvalidOperationException("宿主必须提供 ProjectWorkbooksViewModel。");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AdoptHostTheme();
        var model = TryResolveModel();
        if (_loaded || model == null) return;
        _loaded = true;
        model.PropertyChanged += OnModelPropertyChanged;
        // One guard for every way out: closing the workbook from the page, refreshing it,
        // and the host's project tree opening a different one, which never asks this view.
        model.ConfirmLeaving = TryLeaveDetailsPanel;
        await model.ExecuteAsync(model.InitializeAsync);
    }

    // The panel holds a row that belongs to the workbook that is open, and opening another
    // one rebuilds the row list underneath it, so a panel must never outlive its document.
    // ConfirmLeaving is what normally takes it away, before that happens. This is the net for
    // a path that gets past it: it cannot ask, because by the time the document has been
    // replaced the draft it would be asking about is already gone.
    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectWorkbooksViewModel.Document)) return;
        HideScenePanel();
        HideAssetPanel();
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
        // The host's scrollbar is a keyed style, not an implicit one, so it never reaches this
        // library's scroll areas on its own — they were showing the stock Windows scrollbar,
        // which looks nothing like the rest of the app. Adopted by key, and only when the host
        // actually has it, since a StaticResource here would throw where it does not.
        if ((ancestor?.TryFindResource("ScrollBar.FileArea") ?? Application.Current?.TryFindResource("ScrollBar.FileArea")) is Style scrollBars)
            Resources[typeof(ScrollBar)] = scrollBars;
    }
    private static AssetRowItem? Row(object sender)
    {
        if (sender is FrameworkElement { DataContext: AssetRowItem directItem }) return directItem;
        if (sender is MenuItem menuItem)
        {
            if (menuItem.DataContext is AssetRowItem item) return item;
            if (menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement target } && target.DataContext is AssetRowItem targetItem)
                return targetItem;
        }
        DependencyObject? current = sender as DependencyObject;
        while (current != null)
        {
            if (current is FrameworkElement { DataContext: AssetRowItem item }) return item;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
    private static ProjectFileItem? FileItem(object sender)
    {
        if (sender is FrameworkElement { DataContext: ProjectFileItem directItem }) return directItem;
        if (sender is MenuItem menuItem)
        {
            if (menuItem.DataContext is ProjectFileItem item) return item;
            if (menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement target } && target.DataContext is ProjectFileItem targetItem)
                return targetItem;
        }
        DependencyObject? current = sender as DependencyObject;
        while (current != null)
        {
            if (current is FrameworkElement { DataContext: ProjectFileItem item }) return item;
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
    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (Model.HasPendingOperation)
        {
            MessageBox.Show(Window.GetWindow(this), "请先重试或取消待确认的操作。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var kind = (string)((FrameworkElement)sender).Tag;
        var heading = kind == "scene_beats" ? "新建节拍表" : "新建资产表";
        var prompt = kind == "scene_beats" ? "为节拍表取一个名称：" : "为资产表取一个名称：";
        var defaultName = kind == "scene_beats" ? "节拍表" : "资产表";

        var dialog = new WorkbookNameDialog(Window.GetWindow(this), Resources, heading, prompt, defaultName, "创建");
        if (dialog.ShowDialog() != true) return;

        var name = dialog.Value.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        await Model.ExecuteAsync(async () =>
        {
            Model.SetPending(new PendingWorkbookOperation(Model.Service.OperationScope, Guid.NewGuid().ToString("D"), RequireProject(), kind, null, null, null, name));
            var file = await SubmitPendingAsync();
            await Model.OpenAsync(file);
            Model.ProjectFilesChanged?.Invoke();
        });
    }
    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var item = FileItem(sender);
        if (item != null)
        {
            await Model.ExecuteAsync(() => Model.OpenAsync(item.File));
            return;
        }
        if (((FrameworkElement)sender).DataContext is ProjectFile file)
        {
            await Model.ExecuteAsync(() => Model.OpenAsync(file));
        }
    }

    private async void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (FileItem(sender) is not { } item) return;
        if (item.IsEditingName) return;
        if (FromRowControl(e.OriginalSource as DependencyObject, (DependencyObject)sender)) return;
        e.Handled = true;
        await Model.ExecuteAsync(() => Model.OpenAsync(item.File));
    }

    private void OnCardKeyDown(object sender, KeyEventArgs e)
    {
        if (FileItem(sender) is not { } item) return;
        if (item.IsEditingName) return;
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            item.StartRename();
            if (sender is DependencyObject container)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    var tb = Descendants(container).OfType<TextBox>().FirstOrDefault();
                    tb?.Focus();
                    tb?.SelectAll();
                }));
            }
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            _ = Model.ExecuteAsync(() => Model.OpenAsync(item.File));
        }
    }

    private void OnStartRenameFile(object sender, RoutedEventArgs e)
    {
        var item = FileItem(sender);
        if (item == null) return;
        item.StartRename();
        if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu { PlacementTarget: DependencyObject container })
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                var tb = Descendants(container).OfType<TextBox>().FirstOrDefault();
                tb?.Focus();
                tb?.SelectAll();
            }));
        }
    }

    private async void OnCardRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (FileItem(sender) is not { } item) return;
        if (!item.IsEditingName) return;
        item.IsEditingName = false;
        await Model.RenameWorkbookAsync(item, item.NameDraft);
    }

    private async void OnCardRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (FileItem(sender) is not { } item) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            item.IsEditingName = false;
            await Model.RenameWorkbookAsync(item, item.NameDraft);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            item.CancelRename();
        }
    }

    private async void OnDeleteWorkbookFromMenu(object sender, RoutedEventArgs e)
    {
        if (FileItem(sender) is not { } item) return;
        if (MessageBox.Show(Window.GetWindow(this), $"确定要删除工作簿「{item.Name}」吗？\n删除后不可恢复。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        await Model.DeleteWorkbookAsync(item);
    }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Model.ExecuteAsync(async () =>
    {
        if (Model.Document is { } document)
        {
            try
            {
                await Model.OpenAsync(document.File);
            }
            catch
            {
                Model.Back();
                await Model.RefreshFilesAsync();
            }
        }
        else if (Model.SelectedProject == null) await Model.InitializeAsync();
        else await Model.RefreshFilesAsync();
        Model.ProjectFilesChanged?.Invoke();
    });
    private void OnBack(object sender, RoutedEventArgs e) => Model.Back();
    private async void OnAddEpisode(object sender, RoutedEventArgs e)
    {
        if (Model.Document is not { } document) return;
        var matchNumbers = Model.Sheets
            .Select(s => System.Text.RegularExpressions.Regex.Match(s.Name, @"第\s*(\d+)\s*集"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        var nextNumber = matchNumbers.Count > 0 ? matchNumbers.Max() + 1 : 1;
        var defaultName = $"第{nextNumber}集";
        var sheetName = WorkflowDialogs.Prompt(Window.GetWindow(this), Resources, "新增集数", "请输入集数名称（如：第1集、第2集）：", defaultName, false);
        if (string.IsNullOrWhiteSpace(sheetName)) return;
        sheetName = sheetName.Trim();
        if (Model.Sheets.Any(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(Window.GetWindow(this), $"工作表「{sheetName}」已存在。", "无法添加", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await Model.ExecuteAsync(() => Model.AddEpisodeAsync(sheetName));
    }
    private void OnOpenAssetFolder(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row) Model.OpenAssetFolder(row);
    }
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (Model.Document is not { } document) return;
        var picker = new SaveFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", DefaultExt = ".xlsx", FileName = Path.GetFileName(document.File.Name) };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await Model.ExecuteAsync(async () =>
            await Model.Service.DownloadOriginalToAsync(document.File.Id, picker.FileName));
    }
    private async void OnPickSelectedCandidate(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        if (!row.Row.CanEdit) return;
        await PickSelectedCandidateAsync(row);
    }

    private async Task PickSelectedCandidateAsync(AssetRowItem row)
    {
        if (row.Candidates.Count == 0)
        {
            var chooseLocal = MessageBox.Show(
                Window.GetWindow(this),
                $"资产「{(string.IsNullOrWhiteSpace(row.Row.Name) ? "未命名" : row.Row.Name)}」当前尚未添加任何备选图片。\n\n请将图片直接拖入本行的「备选」列，或者现在从本地选择图片加入备选？",
                "当前行暂无备选图片",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!chooseLocal) return;

            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择图片添加到备选",
                Filter = "图片文件 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                Multiselect = true,
            };
            if (picker.ShowDialog(Window.GetWindow(this)) != true || picker.FileNames.Length == 0) return;

            await Model.ExecuteAsync(async () =>
            {
                var assets = new List<ImageAsset>();
                foreach (var path in picker.FileNames)
                {
                    await using var source = File.OpenRead(path);
                    assets.Add(await Model.Service.UploadProjectImageAsync(
                        Model.Document?.File.ProjectId ?? RequireProject(),
                        Path.GetFileName(path),
                        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg",
                        source,
                        row.Row.AssetFolderPath ?? (string.IsNullOrEmpty(row.Row.Category) ? null : $"{row.Row.Category}/{row.Row.Name}")));
                }
                var allIds = row.Row.CandidateImages.Select(a => a.Id).Concat(assets.Select(a => a.Id)).ToList();
                await UpdateImagesWithReasonAsync(row, assets.First().Id, allIds);
            });
            return;
        }

        var dialog = new RowCandidatePickerWindow(Model, row, Resources)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.ChosenCandidate == null) return;

        var chosenId = dialog.ChosenCandidate.Asset.Id;
        if (chosenId == row.Selected?.Asset.Id) return;

        await Model.ExecuteAsync(async () =>
        {
            await UpdateImagesWithReasonAsync(row, chosenId, row.Row.CandidateImages.Select(a => a.Id));
        });
    }

    private async void OnPickCandidateFiles(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        if (!row.Row.CanEdit) return;

        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片添加到备选",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            Multiselect = true,
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true || picker.FileNames.Length == 0) return;

        await Model.ExecuteAsync(async () =>
        {
            var assets = new List<ImageAsset>();
            foreach (var path in picker.FileNames)
            {
                await using var source = File.OpenRead(path);
                assets.Add(await Model.Service.UploadProjectImageAsync(
                    Model.Document?.File.ProjectId ?? RequireProject(),
                    Path.GetFileName(path),
                    Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg",
                    source,
                    row.Row.AssetFolderPath ?? (string.IsNullOrEmpty(row.Row.Category) ? null : $"{row.Row.Category}/{row.Row.Name}")));
            }
            var allIds = row.Row.CandidateImages.Select(a => a.Id).Concat(assets.Select(a => a.Id)).ToList();
            await UpdateImagesWithReasonAsync(row, row.Row.SelectedImage?.Id, allIds);
        });
    }

    private async void OnPickImages(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        if (!row.Row.CanEdit) return;
        var selected = Equals(((FrameworkElement)sender).Tag, "selected");
        if (selected)
        {
            await PickSelectedCandidateAsync(row);
            return;
        }
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
                assets.Add(await Model.Service.UploadProjectImageAsync(
                    Model.Document?.File.ProjectId ?? RequireProject(),
                    Path.GetFileName(path),
                    Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg",
                    source,
                    row.Row.AssetFolderPath ?? (string.IsNullOrEmpty(row.Row.Category) ? null : $"{row.Row.Category}/{row.Row.Name}")));
            }
            await UpdateImagesWithReasonAsync(row, selected ? assets.Single().Id : row.Row.SelectedImage?.Id,
                selected ? row.Row.CandidateImages.Select(a => a.Id).Concat(new[] { assets.Single().Id }) : row.Row.CandidateImages.Select(a => a.Id).Concat(assets.Select(a => a.Id)));
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
    {
        if (Row(sender) is not { } row) return;
        if (row.Selected != null) await ReviewAsync(row, row.Selected);
        else if (row.Row.CanEdit) await PickSelectedCandidateAsync(row);
    }
    private async void OnPreviewCandidate(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row || ((FrameworkElement)sender).DataContext is not ImageItem image) return;
        try
        {
            var bytes = await Model.Service.GetImageContentAsync(image.Asset.Id, false);
            var ext = Path.GetExtension(image.Name);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
            var tempPath = Path.Combine(Path.GetTempPath(), $"muying_{image.Asset.Id}_{image.Name}");
            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length != bytes.Length)
            {
                await File.WriteAllBytesAsync(tempPath, bytes);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"无法打开图片：{ex.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async void OnOpenCandidateReview(object sender, RoutedEventArgs e)
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
        if (file.ProjectId == Model.SelectedProject?.Id && !Model.Files.Any(existing => existing.Id == file.Id)) Model.Files.Add(new ProjectFileItem(file));
        Model.ProjectFilesChanged?.Invoke();
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
    private static SceneRowItem? Scene(object sender)
    {
        if (sender is FrameworkElement { DataContext: SceneRowItem directItem }) return directItem;
        if (sender is MenuItem menuItem)
        {
            if (menuItem.DataContext is SceneRowItem item) return item;
            if (menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement target } && target.DataContext is SceneRowItem targetItem)
                return targetItem;
        }
        DependencyObject? current = sender as DependencyObject;
        while (current != null)
        {
            if (current is FrameworkElement { DataContext: SceneRowItem item }) return item;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
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
    private SceneDetailsPanel? _scenePanel;
    private SceneRowItem? _scenePanelItem;
    private AssetDetailsPanel? _assetPanel;
    private AssetRowItem? _assetPanelItem;

    private async void OnSceneDetails(object sender, RoutedEventArgs e)
    {
        if (Scene(sender) is not { } row || Model.Document is not { } document) return;
        // Clicking the beat that is already open must not rebuild the panel — rebuilding is
        // how the draft gets dropped, and re-clicking a row is a plausible thing to do.
        if (_scenePanel != null && ReferenceEquals(row, _scenePanelItem)) return;
        if (!TryLeaveDetailsPanel()) return;
        ShowScenePanel(document.File.Id, row);
        await Model.ExecuteAsync(() => Model.RefreshSceneSummaryAsync(document.File.Id, row));
    }

    private void ShowScenePanel(string fileId, SceneRowItem item)
    {
        HideAssetPanel();
        var panel = new SceneDetailsPanel(Model, fileId, item, Resources);
        panel.CloseRequested += (_, _) => TryLeaveDetailsPanel();
        _scenePanel = panel; _scenePanelItem = item;
        ScenePanelContent.Content = panel;
        ScenePanelHost.Visibility = Visibility.Visible;
    }

    private void HideScenePanel()
    {
        _scenePanel = null; _scenePanelItem = null;
        if (_assetPanel == null)
        {
            ScenePanelContent.Content = null;
            ScenePanelHost.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnAssetReviewClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row || Model.Document is not { } document) return;
        if (_assetPanel != null && ReferenceEquals(row, _assetPanelItem)) return;
        if (!TryLeaveDetailsPanel()) return;
        ShowAssetPanel(document.File.Id, row);
        await Model.ExecuteAsync(() => Model.RefreshReviewSummariesAsync(document.File.Id, row));
    }

    private void ShowAssetPanel(string fileId, AssetRowItem item)
    {
        HideScenePanel();
        var panel = new AssetDetailsPanel(Model, fileId, item, Resources);
        panel.CloseRequested += (_, _) => TryLeaveDetailsPanel();
        _assetPanel = panel; _assetPanelItem = item;
        ScenePanelContent.Content = panel;
        ScenePanelHost.Visibility = Visibility.Visible;
    }

    private void HideAssetPanel()
    {
        _assetPanel = null; _assetPanelItem = null;
        if (_scenePanel == null)
        {
            ScenePanelContent.Content = null;
            ScenePanelHost.Visibility = Visibility.Collapsed;
        }
    }

    private bool TryLeaveDetailsPanel()
    {
        var sItem = _scenePanelItem;
        var aItem = _assetPanelItem;
        var doc = Model.Document;
        if (_scenePanel is { } sPanel && !sPanel.TryLeave()) return false;
        if (_assetPanel is { } aPanel && !aPanel.TryLeave()) return false;
        HideScenePanel();
        HideAssetPanel();
        if (doc != null)
        {
            if (sItem != null) _ = Model.RefreshSceneSummaryAsync(doc.File.Id, sItem);
            if (aItem != null) _ = Model.RefreshReviewSummariesAsync(doc.File.Id, aItem);
        }
        return true;
    }

    private async void OnAddAssetRow(object sender, RoutedEventArgs e)
    {
        if (Model.Document is not { } document || Model.SelectedSheet is not { IsAsset: true }) return;
        await Model.ExecuteAsync(Model.AddAssetRowAsync);
    }

    private async void OnDeleteAssetRow(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row || !row.Row.CanEdit) return;
        var name = string.IsNullOrWhiteSpace(row.Row.Name) ? "此资产" : $"「{row.Row.Name}」";
        if (MessageBox.Show(Window.GetWindow(this), $"确定要删除资产{name}这一行吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (_assetPanelItem != null && ReferenceEquals(_assetPanelItem, row))
        {
            HideAssetPanel();
        }

        await Model.ExecuteAsync(() => Model.DeleteAssetRowAsync(row));
    }

    private async void OnAssetFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        await Model.ExecuteInlineAsync(() => Model.UpdateAssetFieldsAsync(row));
    }

    private async void OnAssetFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Row(sender) is not { } row) return;
        e.Handled = true;
        if (sender is UIElement ui) ui.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        await Model.ExecuteInlineAsync(() => Model.UpdateAssetFieldsAsync(row));
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

    private long _sidebarClosedTick;

    private void OnOverlayBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OnOverlayBackdropMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _sidebarClosedTick = Environment.TickCount64;
        TryLeaveDetailsPanel();
    }

    private async void OnAddSceneRow(object sender, RoutedEventArgs e)
    {
        if (Model.Document is not { } document || Model.SelectedSheet is not { IsScene: true }) return;
        await Model.ExecuteAsync(Model.AddSceneRowAsync);
    }

    private async void OnDeleteSceneRow(object sender, RoutedEventArgs e)
    {
        if (Scene(sender) is not { } row || !row.Row.CanEdit) return;
        var label = $"场景 {row.SceneNumberDraft} · 节拍 {row.BeatNumberDraft}";
        if (MessageBox.Show(Window.GetWindow(this), $"确定要删除{label}这一行吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (_scenePanelItem != null && ReferenceEquals(_scenePanelItem, row))
        {
            HideScenePanel();
        }

        await Model.ExecuteAsync(() => Model.DeleteSceneRowAsync(row));
    }

    private async void OnSceneFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (Scene(sender) is not { } row) return;
        await Model.ExecuteInlineAsync(() => Model.UpdateSceneFieldsAsync(row));
    }

    private async void OnSceneFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is TextBox { AcceptsReturn: true }) return;
        if (Scene(sender) is not { } row) return;
        e.Handled = true;
        if (sender is UIElement ui) ui.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        await Model.ExecuteInlineAsync(() => Model.UpdateSceneFieldsAsync(row));
    }

    private async void OnSceneLevelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || Model.Busy) return;
        if (Scene(sender) is not { } row) return;
        await Model.ExecuteInlineAsync(() => Model.UpdateSceneFieldsAsync(row));
    }

    private void OnOpenSceneSidebar(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _sidebarClosedTick < 300) return;
        OnSceneDetails(sender, e);
    }

    private static bool IsVideo(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".flv" or ".wmv";
    }

    private void OnVideoDragOver(object sender, DragEventArgs e)
    {
        if (Scene(sender) is { Row.CanEdit: true }) Drag(e, IsVideo);
        else { e.Effects = DragDropEffects.None; e.Handled = true; }
    }

    private static VideoItem? GetVideoItem(object sender)
    {
        if (sender is FrameworkElement { DataContext: SceneVideoItem svi }) return svi.Video;
        if (sender is FrameworkElement { DataContext: VideoItem directVi }) return directVi;
        if (sender is MenuItem menuItem)
        {
            if (menuItem.DataContext is SceneVideoItem itemM) return itemM.Video;
            if (menuItem.DataContext is VideoItem directM) return directM;
            if (menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement target })
            {
                if (target.DataContext is SceneVideoItem targetItem) return targetItem.Video;
                if (target.DataContext is VideoItem targetDirect) return targetDirect;
            }
        }
        return null;
    }

    private void OnPlayVideoDirectly(object sender, RoutedEventArgs e)
    {
        var video = GetVideoItem(sender) ?? Scene(sender)?.Video;
        if (video == null || string.IsNullOrWhiteSpace(video.Path)) return;
        try
        {
            if (File.Exists(video.Path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(video.Path) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(Window.GetWindow(this), $"未找到视频文件「{video.Name}」。", "无法播放", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"播放失败：{ex.Message}", "无法播放", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnDiscardVideoItem(object sender, RoutedEventArgs e)
    {
        if (Scene(sender) is not { } row || !row.Row.CanEdit) return;
        var video = GetVideoItem(sender) ?? row.Video;
        if (video == null) return;
        if (MessageBox.Show(Window.GetWindow(this), $"确定将视频「{video.Name}」移入废片库吗？", "移入废片", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Model.UpdateSceneVideoAsync(row, "discard", videoId: video.Id, videoName: video.Name);
    }

    private async void OnVideoDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Scene(sender) is not { } row || !row.Row.CanEdit) return;
        var paths = Paths(e);
        if (paths.Length == 0 || paths.Any(p => !IsVideo(p)))
        {
            MessageBox.Show(Window.GetWindow(this), "请拖入视频文件（如 .mp4, .mov, .mkv 等）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var model = TryResolveModel(sender) ?? Model;
        foreach (var path in paths)
        {
            await model.AttachVideoFileAsync(row, path);
        }
    }

    private async void OnPickVideo(object sender, RoutedEventArgs e)
    {
        if (Scene(sender) is not { } row || !row.Row.CanEdit) return;
        var picker = new OpenFileDialog
        {
            Title = "选择关联视频",
            Filter = "视频文件 (*.mp4;*.mov;*.mkv;*.avi;*.webm)|*.mp4;*.mov;*.mkv;*.avi;*.webm|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        var model = TryResolveModel(sender) ?? Model;
        foreach (var path in picker.FileNames)
        {
            await model.AttachVideoFileAsync(row, path);
        }
    }

    private void OnDiscardVideo(object sender, RoutedEventArgs e) => OnDiscardVideoItem(sender, e);

    private async void OnOpenDiscardedVideos(object sender, RoutedEventArgs e)
    {
        if (Scene(sender) is not { } row) return;
        if (row.DiscardedVideos.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "当前节拍没有已移入废片的视频。", "废片库", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var chosen = WorkflowDialogs.Choose(
            Window.GetWindow(this),
            Resources,
            $"场景 {row.SceneNumberDraft} · 节拍 {row.BeatNumberDraft} 废片库",
            row.DiscardedVideos.ToList(),
            v => $"{v.Name}\n废弃时间：{v.DiscardedAt ?? "未知"}",
            false
        );

        if (chosen?.Count == 1 && row.Row.CanEdit)
        {
            var target = chosen[0];
            if (MessageBox.Show(Window.GetWindow(this), $"要恢复视频「{target.Name}」为当前关联视频吗？\n当前的关联视频将被移入废片。", "恢复视频", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await Model.UpdateSceneVideoAsync(row, "restore", videoId: target.Id, videoName: target.Name);
            }
        }
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
                if (element.DataContext is AssetRowItem asset && assets.Add(asset))
                {
                    if (asset.Selected != null) work.Add(Model.EnsureThumbnailAsync(asset.Selected));
                    foreach (var candidate in asset.Candidates) work.Add(Model.EnsureThumbnailAsync(candidate));
                    if (!asset.SummaryLoaded) work.Add(Model.RefreshReviewSummariesAsync(document.File.Id, asset));
                }
                if (element.DataContext is SceneRowItem scene && !scene.SummaryLoaded && scenes.Add(scene))
                    work.Add(Model.RefreshSceneSummaryAsync(document.File.Id, scene));
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

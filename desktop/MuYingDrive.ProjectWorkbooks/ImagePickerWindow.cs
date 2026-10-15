using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MuYingDrive.ProjectWorkbooks;

public sealed class ImagePickerWindow : Window
{
    private readonly ListBox _list;
    private readonly ObservableCollection<ImageItem> _images = [];
    private readonly TextBox _query;
    private readonly ComboBox _category;
    private readonly TextBlock _status;
    private readonly ProjectWorkbooksViewModel _model;
    private readonly string _projectId;
    private string? _cursor;
    private bool _busy;
    private string? _activeQuery, _activeCategory;
    private CancellationTokenSource? _queryLifetime;
    private int _generation;
    public IReadOnlyList<ImageItem> SelectedImages => _list.SelectedItems.Cast<ImageItem>().ToList();
    public ImagePickerWindow(ProjectWorkbooksViewModel model, string projectId, bool multiple)
    {
        _model = model; _projectId = projectId;
        Title = multiple ? "本项目素材 · 添加备选" : "本项目素材 · 选择一张"; Width = 900; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Surface.Panel"); SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        SetResourceReference(FontFamilyProperty, "Typography.FontFamily");
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var toolbar = new WrapPanel(); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        _query = WorkflowDialogs.Input("", false); _query.Width = 240; _query.MaxLength = 200; _query.ToolTip = "名称或形态关键词"; toolbar.Children.Add(_query);
        _category = new ComboBox { ItemsSource = new[] { "全部类别", "角色", "场景" }, SelectedIndex = 0, Width = 120, Margin = new Thickness(8, 0, 8, 10) }; toolbar.Children.Add(_category);
        toolbar.Children.Add(WorkflowDialogs.Button("搜索", async (_, _) => await LoadAsync(reset: true)));
        toolbar.Children.Add(WorkflowDialogs.Button("加载下一页", async (_, _) => { if (_cursor != null) await LoadAsync(reset: false); }));
        toolbar.Children.Add(WorkflowDialogs.Button("修改显示名称", async (_, _) => await RenameSelectedAsync()));
        _status = WorkflowDialogs.Text("先显示当前页素材信息，缩略图随后加载。", 12); DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);
        var bottom = new WrapPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(WorkflowDialogs.Button("确认选择", (_, _) => { if (_busy) return;
            if (!multiple && SelectedImages.Count != 1) { _status.Text = "选定只包含一张图片，请保留一张选择后确认。"; return; }
            if (SelectedImages.Count > 0) DialogResult = true; }));
        _list = new ListBox { ItemsSource = _images, SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0) };
        _list.SetResourceReference(BackgroundProperty, "Brush.Surface.Panel"); _list.SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var wrap = new FrameworkElementFactory(typeof(WrapPanel)); _list.ItemsPanel = new ItemsPanelTemplate(wrap);
        var stack = new FrameworkElementFactory(typeof(StackPanel)); stack.SetValue(FrameworkElement.WidthProperty, 150d); stack.SetValue(FrameworkElement.MarginProperty, new Thickness(6));
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new Binding(nameof(ImageItem.Preview)));
        image.SetValue(FrameworkElement.HeightProperty, 110d); image.SetValue(Image.StretchProperty, Stretch.Uniform); stack.AppendChild(image);
        foreach (var property in new[] { nameof(ImageItem.Name), nameof(ImageItem.Details), nameof(ImageItem.Error) })
        { var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(property)); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); stack.AppendChild(text); }
        _list.ItemTemplate = new DataTemplate { VisualTree = stack }; root.Children.Add(_list);
        Loaded += async (_, _) => await LoadAsync(reset: true);
        _model.ProjectImageChanged += OnProjectImageChanged;
        Closed += (_, _) => { _model.ProjectImageChanged -= OnProjectImageChanged; _generation++; _queryLifetime?.Cancel(); _queryLifetime?.Dispose(); };
    }
    private async Task LoadAsync(bool reset)
    {
        if (_busy && !reset) return;
        if (reset) { _queryLifetime?.Cancel(); _queryLifetime?.Dispose(); _queryLifetime = new CancellationTokenSource(); _generation++; _cursor = null; }
        var generation = _generation; var ct = _queryLifetime?.Token ?? CancellationToken.None;
        var query = reset ? _query.Text.Trim() : _activeQuery;
        var category = reset ? (_category.SelectedIndex == 0 ? null : _category.SelectedItem as string) : _activeCategory;
        _busy = true;
        try
        {
            var page = await _model.Service.SearchImagesAsync(_projectId, query, category, reset ? null : _cursor, 40, ct);
            if (generation != _generation) return;
            _activeQuery = query; _activeCategory = category;
            if (reset) _images.Clear();
            var added = page.Items.Where(asset => !_images.Any(image => image.Asset.Id == asset.Id)).Select(asset => new ImageItem(asset, null)).ToList();
            foreach (var image in added) _images.Add(image);
            _cursor = page.NextCursor;
            _status.Text = $"已列出 {_images.Count} 张" + (_cursor == null ? "，当前检索已到末页。" : "，可加载下一页。") + " 可按 Ctrl / Shift 多选。";
            // Do not delay metadata rendering. Shared loader caps thumbnail requests at four.
            _busy = false;
            _ = LoadThumbnailsAsync(added, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _generation) _status.Text = ex is WorkbookApiException ? ex.Message : "素材读取失败，请重试搜索；旧列表未变，没有退回全库。"; }
        finally { if (generation == _generation) _busy = false; }
    }
    private async Task LoadThumbnailsAsync(IEnumerable<ImageItem> items, CancellationToken ct)
    { try { await Task.WhenAll(items.Select(image => _model.EnsureThumbnailAsync(image, ct))); } catch (OperationCanceledException) { } }

    private async Task RenameSelectedAsync()
    {
        if (_busy) return;
        if (SelectedImages.Count != 1)
        {
            _status.Text = "请选择一张图片，再修改它的显示名称。";
            return;
        }
        var image = SelectedImages[0];
        var before = image.Asset;
        var name = WorkflowDialogs.Prompt(this, Resources, "修改图片显示名称",
            "只修改素材列表中的显示名称，不会重命名磁盘文件。", before.Name, allowEmpty: false);
        if (name == null || string.Equals(name, before.Name, StringComparison.Ordinal)) return;
        try
        {
            _busy = true;
            var renamed = await _model.Service.RenameImageAsync(before.Id, before.Version, name,
                _queryLifetime?.Token ?? CancellationToken.None);
            image.UpdateAsset(renamed);
            _status.Text = "显示名称已保存；图片文件名和所在目录保持不变。";
        }
        catch (Exception ex)
        {
            _status.Text = ex is WorkbookApiException ? ex.Message : "修改图片显示名称失败，请稍后重试。";
        }
        finally { _busy = false; }
    }

    private void OnProjectImageChanged(ImageAsset asset)
    {
        var item = _images.FirstOrDefault(image => image.Asset.Id == asset.Id);
        if (item is null) return;
        item.UpdateAsset(asset);
        _ = _model.EnsureThumbnailAsync(item);
    }
}

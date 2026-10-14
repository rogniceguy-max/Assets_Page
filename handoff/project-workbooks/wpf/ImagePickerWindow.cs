using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MuYingDrive.ProjectWorkbooks;

public sealed class ImagePickerWindow : Window
{
    private sealed record PickerAttachment(ImageItem Image, string Role, bool CanRemove);

    private readonly ListBox _list;
    private readonly ObservableCollection<ImageItem> _images = [];
    private readonly ObservableCollection<PickerAttachment> _attachments = [];
    private readonly TextBox _query;
    private readonly ComboBox _category;
    private readonly TextBlock _status;
    private TextBlock _attachmentTitle = null!;
    private TextBlock _attachmentEmpty = null!;
    private readonly Button _confirm;
    private readonly ProjectWorkbooksViewModel _model;
    private readonly string _projectId;
    private readonly string _rowId;
    private string? _cursor;
    private bool _busy;
    private bool _attachmentBusy;
    private bool _rowChanged;
    private string? _activeQuery, _activeCategory;
    private CancellationTokenSource? _queryLifetime;
    private readonly CancellationTokenSource _attachmentLifetime = new();
    private int _generation;

    public IReadOnlyList<ImageItem> SelectedImages => _list.SelectedItems.Cast<ImageItem>().ToList();

    public ImagePickerWindow(ProjectWorkbooksViewModel model, string projectId, AssetRowItem row)
    {
        _model = model; _projectId = projectId; _rowId = row.Row.Id;
        AddCurrentAttachments(row);
        Title = "本项目素材 · 选择一张"; Width = 900; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Surface.Panel"); SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        SetResourceReference(FontFamilyProperty, "Typography.FontFamily");

        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var toolbar = new WrapPanel(); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        _query = WorkflowDialogs.Input("", false); _query.Width = 240; _query.MaxLength = 200; _query.ToolTip = "名称或形态关键词"; toolbar.Children.Add(_query);
        _category = new ComboBox { ItemsSource = new[] { "全部类别", "角色", "场景" }, SelectedIndex = 0, Width = 120, Margin = new Thickness(8, 0, 8, 10) }; toolbar.Children.Add(_category);
        toolbar.Children.Add(WorkflowDialogs.Button("搜索", async (_, _) => await LoadAsync(reset: true)));
        toolbar.Children.Add(WorkflowDialogs.Button("加载下一页", async (_, _) => { if (_cursor != null) await LoadAsync(reset: false); }));

        var attachmentArea = BuildAttachmentArea(); DockPanel.SetDock(attachmentArea, Dock.Top); root.Children.Add(attachmentArea);
        _status = WorkflowDialogs.Text("先显示当前页素材信息，缩略图随后加载。", 12); DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);

        var bottom = new WrapPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        _confirm = WorkflowDialogs.Button("确认选择", (_, _) => Confirm());
        bottom.Children.Add(_confirm);

        _list = new ListBox { ItemsSource = _images, SelectionMode = SelectionMode.Single, BorderThickness = new Thickness(0) };
        _list.SetResourceReference(BackgroundProperty, "Brush.Surface.Panel"); _list.SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => UpdateConfirmLabel();
        var wrap = new FrameworkElementFactory(typeof(WrapPanel)); _list.ItemsPanel = new ItemsPanelTemplate(wrap);
        var stack = new FrameworkElementFactory(typeof(StackPanel)); stack.SetValue(FrameworkElement.WidthProperty, 150d); stack.SetValue(FrameworkElement.MarginProperty, new Thickness(6));
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new Binding(nameof(ImageItem.Preview)));
        image.SetValue(FrameworkElement.HeightProperty, 110d); image.SetValue(Image.StretchProperty, Stretch.Uniform); stack.AppendChild(image);
        foreach (var property in new[] { nameof(ImageItem.Name), nameof(ImageItem.Details), nameof(ImageItem.Error) })
        { var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(property)); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); stack.AppendChild(text); }
        _list.ItemTemplate = new DataTemplate { VisualTree = stack }; root.Children.Add(_list);

        UpdateAttachmentLabel();
        Loaded += async (_, _) =>
        {
            var search = LoadAsync(reset: true);
            var thumbnails = LoadAttachmentThumbnailsAsync();
            await Task.WhenAll(search, thumbnails);
        };
        Closed += (_, _) =>
        {
            _generation++; _queryLifetime?.Cancel(); _queryLifetime?.Dispose();
            _attachmentLifetime.Cancel(); _attachmentLifetime.Dispose();
        };
    }

    private void AddCurrentAttachments(AssetRowItem row)
    {
        var candidates = row.Row.CandidateImages.ToDictionary(image => image.Id, StringComparer.Ordinal);
        if (row.Selected is { } selected)
        {
            var alsoCandidate = candidates.Remove(selected.Asset.Id);
            _attachments.Add(new PickerAttachment(selected, alsoCandidate ? "选定 · 备选" : "选定", row.Row.CanEdit));
        }
        foreach (var image in row.Candidates)
            if (candidates.Remove(image.Asset.Id)) _attachments.Add(new PickerAttachment(image, "备选", row.Row.CanEdit));
    }

    private UIElement BuildAttachmentArea()
    {
        var content = new StackPanel();
        _attachmentTitle = WorkflowDialogs.Text("本表已关联", 13);
        _attachmentTitle.Margin = new Thickness(0, 0, 0, 5);
        _attachmentTitle.FontWeight = FontWeights.SemiBold;
        content.Children.Add(_attachmentTitle);
        _attachmentEmpty = WorkflowDialogs.Text("暂无关联图片。", 12);
        content.Children.Add(_attachmentEmpty);

        var items = new ItemsControl { ItemsSource = _attachments };
        var wrap = new FrameworkElementFactory(typeof(WrapPanel));
        items.ItemsPanel = new ItemsPanelTemplate(wrap);
        var card = new FrameworkElementFactory(typeof(Border));
        card.SetValue(Border.WidthProperty, 132d); card.SetValue(Border.MarginProperty, new Thickness(0, 0, 8, 8));
        card.SetValue(Border.PaddingProperty, new Thickness(8)); card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface.Canvas");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border.Subtle");
        var stack = new FrameworkElementFactory(typeof(StackPanel)); card.AppendChild(stack);
        var role = new FrameworkElementFactory(typeof(TextBlock));
        role.SetBinding(TextBlock.TextProperty, new Binding(nameof(PickerAttachment.Role)));
        role.SetValue(TextBlock.FontSizeProperty, 11d); role.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 0, 4)); stack.AppendChild(role);
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new Binding($"{nameof(PickerAttachment.Image)}.{nameof(ImageItem.Preview)}"));
        image.SetValue(FrameworkElement.HeightProperty, 64d); image.SetValue(Image.StretchProperty, Stretch.Uniform); stack.AppendChild(image);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding($"{nameof(PickerAttachment.Image)}.{nameof(ImageItem.Name)}"));
        name.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); name.SetValue(TextBlock.FontSizeProperty, 11d); stack.AppendChild(name);
        var details = new FrameworkElementFactory(typeof(TextBlock));
        details.SetBinding(TextBlock.TextProperty, new Binding($"{nameof(PickerAttachment.Image)}.{nameof(ImageItem.Details)}"));
        details.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); details.SetValue(TextBlock.FontSizeProperty, 10d); stack.AppendChild(details);
        var remove = new FrameworkElementFactory(typeof(Button));
        remove.SetValue(Button.ContentProperty, "移入回收站"); remove.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 0));
        remove.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        remove.SetBinding(Button.IsEnabledProperty, new Binding(nameof(PickerAttachment.CanRemove)));
        remove.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnRemoveAttachment)); stack.AppendChild(remove);
        items.ItemTemplate = new DataTemplate { VisualTree = card };
        content.Children.Add(items);

        var scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = 170,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 4, 0, 8)
        };
        var section = new Border { Padding = new Thickness(10, 8, 10, 2), Margin = new Thickness(0, 2, 0, 4), Child = scroll, BorderThickness = new Thickness(1) };
        section.SetResourceReference(Border.BackgroundProperty, "Brush.Surface.Panel");
        section.SetResourceReference(Border.BorderBrushProperty, "Brush.Border.Subtle");
        return section;
    }

    private void UpdateAttachmentLabel()
    {
        _attachmentTitle.Text = $"本表已关联（{_attachments.Count}）";
        _attachmentEmpty.Visibility = _attachments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateConfirmLabel()
    {
        _confirm.Content = _rowChanged && SelectedImages.Count == 0
            ? "完成"
            : "确认选择";
    }

    private void Confirm()
    {
        if (_busy || _attachmentBusy) return;
        if (SelectedImages.Count != 1 && !_rowChanged)
        { _status.Text = "选定只包含一张图片，请保留一张选择后确认。"; return; }
        if (SelectedImages.Count > 0 || _rowChanged) DialogResult = true;
    }

    private async void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if (_attachmentBusy || sender is not FrameworkElement { DataContext: PickerAttachment attachment } || !attachment.CanRemove) return;
        var row = _model.SelectedSheet?.Assets.FirstOrDefault(item => item.Row.Id == _rowId);
        if (row == null || _model.Document == null)
        { _status.Text = "工作表已关闭，请重新打开后操作。"; return; }

        var imageId = attachment.Image.Asset.Id;
        _attachmentBusy = true;
        _status.Text = "正在移入回收站…";
        try
        {
            // The backend soft-hides the same file and removes its row relation atomically.
            // Reviews and comments remain attached to the stable image ID for restoration.
            await _model.ArchiveAssetRowImageAsync(row, attachment.Image);
            _attachments.Remove(attachment);
            foreach (var searchImage in _images.Where(image => image.Asset.Id == imageId).ToList())
            {
                _list.SelectedItems.Remove(searchImage);
                _images.Remove(searchImage);
            }
            _rowChanged = true;
            UpdateAttachmentLabel(); UpdateConfirmLabel();
            _status.Text = "已移入回收站；评论和评价保留。";
        }
        catch (WorkbookApiException ex) when (ex.StatusCode == 409)
        { _status.Text = "内容已变更，本次未覆盖。请关闭后重新加载并核对。"; }
        catch (Exception ex)
        { _status.Text = ex is WorkbookApiException or InvalidDataException ? ex.Message : "移除未完成，请检查连接后重新加载确认。"; }
        finally { _attachmentBusy = false; }
    }

    private async Task LoadAttachmentThumbnailsAsync()
    {
        try { await Task.WhenAll(_attachments.Select(item => _model.EnsureThumbnailAsync(item.Image, _attachmentLifetime.Token))); }
        catch (OperationCanceledException) { }
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
            _status.Text = $"已列出 {_images.Count} 张" + (_cursor == null ? "，当前检索已到末页。" : "，可加载下一页。");
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
}

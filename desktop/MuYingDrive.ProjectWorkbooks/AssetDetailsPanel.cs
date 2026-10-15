using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MuYingDrive.ProjectWorkbooks;

public sealed class AssetDetailsPanel : UserControl
{
    private readonly ProjectWorkbooksViewModel _model;
    private readonly string _fileId;
    private readonly AssetRowItem _item;
    private readonly ResourceDictionary _resources;
    private readonly ReviewPanel _review;

    public event EventHandler? CloseRequested;
    public string Heading { get; }

    public AssetDetailsPanel(ProjectWorkbooksViewModel model, string fileId, AssetRowItem item, ResourceDictionary resources)
    {
        _model = model;
        _fileId = fileId;
        _item = item;
        _resources = resources;
        Heading = $"资产评审 · {(string.IsNullOrWhiteSpace(item.Row.Name) ? "未命名" : item.Row.Name)}";

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Asset info / actions
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // ReviewPanel
        Content = root;

        // Header: Close button + Title
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var close = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 10, 10),
        };
        close.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(close, 0);
        header.Children.Add(close);

        var title = new TextBlock
        {
            Text = Heading,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(10, 0, 0, 10),
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Asset metadata & actions
        var infoPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var metaText = new TextBlock
        {
            Text = $"类别：{item.Row.Category ?? "角色"}    出场：{item.Row.Appearance ?? "未设置"}",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        };
        metaText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        infoPanel.Children.Add(metaText);

        var actionsWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };

        var openFolderBtn = new Button { Content = "打开文件夹", Margin = new Thickness(0, 0, 6, 6) };
        openFolderBtn.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        openFolderBtn.Click += (_, _) => _model.OpenAssetFolder(_item);
        actionsWrap.Children.Add(openFolderBtn);

        var historyBtn = new Button { Content = "选定历史", Margin = new Thickness(0, 0, 6, 6) };
        historyBtn.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        historyBtn.Click += async (_, _) =>
        {
            await _model.ExecuteAsync(async () =>
            {
                var history = await _model.Service.GetSelectionHistoryAsync(_fileId, _item.Row.Id);
                WorkflowDialogs.ShowText(Window.GetWindow(this), _resources, "选定历史",
                    history.Count == 0 ? "暂无选定变更记录。" : string.Join("\n\n", history.Select(entry =>
                        $"{entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {entry.UserName}\n{entry.PreviousImageId ?? "未选定"} → {entry.ImageId ?? "已清空"}\n原因：{entry.Reason ?? "未填写"}")));
            });
        };
        actionsWrap.Children.Add(historyBtn);

        var compareBtn = new Button { Content = "比较图片", Margin = new Thickness(0, 0, 6, 6) };
        compareBtn.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        compareBtn.Click += async (_, _) =>
        {
            await _model.ExecuteAsync(async () =>
            {
                var images = _item.Candidates.Concat(_item.Selected == null ? [] : new[] { _item.Selected }).DistinctBy(img => img.Asset.Id).ToList();
                var chosen = WorkflowDialogs.Choose(Window.GetWindow(this), _resources, "选择 2–4 张图片比较", images, img => img.Name + "\n" + img.Details, true);
                if (chosen == null) return;
                if (chosen.Count is < 2 or > 4) throw new InvalidDataException("请选择 2–4 张图片。");
                await _model.RefreshReviewSummariesAsync(_fileId, _item);
                var window = new CompareImagesWindow(_model.Service, chosen) { Owner = Window.GetWindow(this) };
                WorkflowDialogs.Theme(window, _resources);
                window.ShowDialog();
            });
        };
        actionsWrap.Children.Add(compareBtn);

        if (_item.Row.CanEdit)
        {
            var deleteBtn = new Button { Content = "删除行", Margin = new Thickness(0, 0, 6, 6) };
            deleteBtn.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
            deleteBtn.Click += async (_, _) =>
            {
                if (MessageBox.Show(Window.GetWindow(this), $"确定要删除资产「{_item.Row.Name}」这一行吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    await _model.ExecuteAsync(() => _model.DeleteAssetRowAsync(_item));
                }
            };
            actionsWrap.Children.Add(deleteBtn);
        }

        infoPanel.Children.Add(actionsWrap);
        Grid.SetRow(infoPanel, 1);
        root.Children.Add(infoPanel);

        // Embedded ReviewPanel for user comments and composer
        _review = new ReviewPanel
        {
            DataContext = new ReviewViewModel(_model.Service, _fileId, _item.Row.Id, null, "asset-rows"),
        };
        Grid.SetRow(_review, 2);
        root.Children.Add(_review);
    }

    public bool TryLeave() => _review.TryLeave();
}

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

        // Asset metadata
        var infoPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var metaText = new TextBlock
        {
            Text = $"类别：{item.Row.Category ?? "角色"}    出场：{item.Row.Appearance ?? "未设置"}",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        };
        metaText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        infoPanel.Children.Add(metaText);

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

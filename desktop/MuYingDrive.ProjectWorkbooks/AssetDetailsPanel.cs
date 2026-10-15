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

    public event EventHandler? CloseRequested { add { } remove { } }
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

        // Header: Clean border + title
        var header = new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(0, 0, 0, 8),
        };
        header.SetResourceReference(Border.BorderBrushProperty, "Brush.Border.Subtle");
        header.BorderThickness = new Thickness(0, 0, 0, 1);

        var title = new TextBlock
        {
            Text = Heading,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Child = title;

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

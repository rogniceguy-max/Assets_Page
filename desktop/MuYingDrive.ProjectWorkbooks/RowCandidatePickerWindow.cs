using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MuYingDrive.ProjectWorkbooks;

public sealed class RowCandidatePickerWindow : Window
{
    private readonly ProjectWorkbooksViewModel _model;
    private readonly AssetRowItem _row;
    private readonly ListBox _list;
    public ImageItem? ChosenCandidate { get; private set; }

    public RowCandidatePickerWindow(ProjectWorkbooksViewModel model, AssetRowItem row, ResourceDictionary resources)
    {
        _model = model;
        _row = row;

        Resources.MergedDictionaries.Add(resources);
        SetResourceReference(BackgroundProperty, "Brush.Surface.Panel");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        SetResourceReference(FontFamilyProperty, "Typography.FontFamily");

        Title = $"选择选定图片 · {(string.IsNullOrWhiteSpace(row.Row.Name) ? "未命名资产" : row.Row.Name)}";
        Width = 640;
        Height = 460;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Candidate Grid
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Bottom Bar
        Content = root;

        // 1. Header
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var titleText = new TextBlock
        {
            Text = $"选择选定图片 · {(string.IsNullOrWhiteSpace(row.Row.Name) ? "未命名资产" : row.Row.Name)}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        };
        header.Children.Add(titleText);

        var hintText = new TextBlock
        {
            Text = $"从当前资产行的 {row.Candidates.Count} 张备选图片中选取一张（双击或点击确认）：",
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        header.Children.Add(hintText);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // 2. Candidate ListBox
        _list = new ListBox
        {
            ItemsSource = row.Candidates,
            SelectionMode = SelectionMode.Single,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
        };
        _list.SetResourceReference(ListBox.BackgroundProperty, "Brush.Surface.Canvas");
        _list.SetResourceReference(ListBox.BorderBrushProperty, "Brush.Border.Subtle");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

        // ItemsPanel: WrapPanel
        var wrapFactory = new FrameworkElementFactory(typeof(WrapPanel));
        _list.ItemsPanel = new ItemsPanelTemplate(wrapFactory);

        // ItemTemplate: Card with image + name
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.WidthProperty, 120d);
        borderFactory.SetValue(Border.HeightProperty, 130d);
        borderFactory.SetValue(Border.MarginProperty, new Thickness(6));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(6));
        borderFactory.SetResourceReference(Border.BackgroundProperty, "Brush.Surface.Panel");
        borderFactory.SetResourceReference(Border.BorderBrushProperty, "Brush.Border.Subtle");
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
        stackFactory.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

        var imgFactory = new FrameworkElementFactory(typeof(Image));
        imgFactory.SetBinding(Image.SourceProperty, new Binding(nameof(ImageItem.Preview)));
        imgFactory.SetValue(Image.HeightProperty, 88d);
        imgFactory.SetValue(Image.StretchProperty, Stretch.Uniform);
        stackFactory.AppendChild(imgFactory);

        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(ImageItem.Name)));
        textFactory.SetValue(TextBlock.FontSizeProperty, 11d);
        textFactory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        textFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        textFactory.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
        textFactory.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Primary");
        stackFactory.AppendChild(textFactory);

        borderFactory.AppendChild(stackFactory);
        _list.ItemTemplate = new DataTemplate { VisualTree = borderFactory };

        // Select current selected if matches
        if (row.Selected != null)
        {
            var match = row.Candidates.FirstOrDefault(c => c.Asset.Id == row.Selected.Asset.Id);
            if (match != null) _list.SelectedItem = match;
        }
        else if (row.Candidates.Count > 0)
        {
            _list.SelectedIndex = 0;
        }

        _list.MouseDoubleClick += (s, e) => ConfirmSelection();

        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        // 3. Bottom Bar
        var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };

        var countText = new TextBlock
        {
            Text = $"共 {row.Candidates.Count} 张备选",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        countText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        DockPanel.SetDock(countText, Dock.Left);
        bottom.Children.Add(countText);

        var btnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var okBtn = new Button
        {
            Content = "设为选定",
            MinWidth = 80,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 10, 0),
            IsDefault = true,
        };
        okBtn.SetResourceReference(Button.StyleProperty, "Button.Primary");
        okBtn.Click += (s, e) => ConfirmSelection();
        btnStack.Children.Add(okBtn);

        var cancelBtn = new Button
        {
            Content = "取消",
            MinWidth = 70,
            Padding = new Thickness(12, 5, 12, 5),
            IsCancel = true,
        };
        cancelBtn.SetResourceReference(Button.StyleProperty, "Button.Secondary");
        cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
        btnStack.Children.Add(cancelBtn);

        bottom.Children.Add(btnStack);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        // Ensure thumbnails are loaded
        Loaded += async (s, e) =>
        {
            try
            {
                await Task.WhenAll(row.Candidates.Select(c => _model.EnsureThumbnailAsync(c)));
            }
            catch { }
        };
    }

    private void ConfirmSelection()
    {
        if (_list.SelectedItem is not ImageItem item) return;
        ChosenCandidate = item;
        DialogResult = true;
        Close();
    }
}

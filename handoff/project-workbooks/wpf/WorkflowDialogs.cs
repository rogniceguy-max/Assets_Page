using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MuYingDrive.ProjectWorkbooks;

internal static class WorkflowDialogs
{
    internal static void Theme(Window window, ResourceDictionary resources)
    {
        window.Resources.MergedDictionaries.Add(resources);
        window.SetResourceReference(Control.BackgroundProperty, "Brush.Surface.Canvas");
        window.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
        window.SetResourceReference(Control.FontFamilyProperty, "Typography.FontFamily");
    }
    internal static TextBlock Text(string? value, double size = 14) => new() { Text = value ?? "", TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 0, 0, 8) };
    internal static Button Button(string label, RoutedEventHandler action)
    { var button = new Button { Content = label }; button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary"); button.Click += action; return button; }
    internal static TextBox Input(string? value, bool multiline = true)
    {
        var input = new TextBox { Text = value ?? "", TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline,
            MinHeight = multiline ? 90 : 30, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 10) };
        input.SetResourceReference(Control.BackgroundProperty, "Brush.Surface.Panel"); input.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
        input.SetResourceReference(Control.BorderBrushProperty, "Brush.Border.Subtle"); input.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Text.Primary"); return input;
    }
    internal static string? Prompt(Window owner, ResourceDictionary resources, string title, string message, string initial = "", bool allowEmpty = true)
    {
        var window = new Window { Owner = owner, Title = title, Width = 560, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Theme(window, resources); var panel = new StackPanel { Margin = new Thickness(20) }; window.Content = panel;
        panel.Children.Add(Text(message)); var input = Input(initial); panel.Children.Add(input);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        buttons.Children.Add(Button("确认", (_, _) => { if (allowEmpty || !string.IsNullOrWhiteSpace(input.Text)) window.DialogResult = true; }));
        buttons.Children.Add(Button("取消", (_, _) => window.DialogResult = false));
        window.Closing += (_, e) => { if (window.DialogResult != true && input.Text != initial && MessageBox.Show(window, "说明尚未提交，是否放弃当前输入？", "保留草稿", MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true; };
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
    internal static void ShowText(Window owner, ResourceDictionary resources, string title, string text)
    {
        var window = new Window { Owner = owner, Title = title, Width = 760, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = Text(text), Margin = new Thickness(20), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Theme(window, resources); window.ShowDialog();
    }
    internal static IReadOnlyList<T>? Choose<T>(Window owner, ResourceDictionary resources, string title, IReadOnlyList<T> items, Func<T, string> label, bool multiple)
    {
        var window = new Window { Owner = owner, Title = title, Width = 720, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Theme(window, resources); var root = new DockPanel { Margin = new Thickness(18) }; window.Content = root;
        var list = new ListBox { SelectionMode = multiple ? SelectionMode.Extended : SelectionMode.Single };
        foreach (var item in items) list.Items.Add(new ListBoxItem { Content = Text(label(item)), Tag = item });
        list.SetResourceReference(Control.BackgroundProperty, "Brush.Surface.Panel"); list.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var hint = Text(multiple ? "按住 Ctrl / Shift 多选；确认后仅关联所选项。" : "选择要打开的条目。"); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var confirm = Button("确认", (_, _) => window.DialogResult = true); DockPanel.SetDock(confirm, Dock.Bottom); root.Children.Add(confirm); root.Children.Add(list);
        return window.ShowDialog() == true ? list.SelectedItems.Cast<ListBoxItem>().Select(item => (T)item.Tag).ToList() : null;
    }
}
// Image navigation is local to the preview; workbook layouts remain free of horizontal scrolling.
public sealed class ZoomImageView : DockPanel
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(ZoomImageView),
        new PropertyMetadata(null, (d, e) => ((ZoomImageView)d)._image.Source = (ImageSource?)e.NewValue));
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    private readonly Image _image;
    private readonly ScrollViewer _scroll;
    private readonly ScaleTransform _scale = new(1, 1);
    private Point? _drag;
    private double _startX, _startY;
    public ZoomImageView()
    {
        var slider = new Slider { Minimum = 0.5, Maximum = 5, Value = 1, Width = 170, Margin = new Thickness(8), ToolTip = "缩放；按住鼠标左键拖动图片" };
        var tools = new WrapPanel(); tools.Children.Add(WorkflowDialogs.Text("缩放 / 拖动平移", 12)); tools.Children.Add(slider); SetDock(tools, Dock.Bottom); Children.Add(tools);
        _image = new Image { Width = 480, Stretch = Stretch.Uniform, LayoutTransform = _scale };
        _scroll = new ScrollViewer { Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Children.Add(_scroll);
        SizeChanged += (_, e) => { if (e.NewSize.Width > 30) _image.Width = e.NewSize.Width - 24; };
        slider.ValueChanged += (_, e) => { _scale.ScaleX = e.NewValue; _scale.ScaleY = e.NewValue; };
        _scroll.PreviewMouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Image) { _drag = e.GetPosition(_scroll); _startX = _scroll.HorizontalOffset; _startY = _scroll.VerticalOffset; _scroll.CaptureMouse(); e.Handled = true; } };
        _scroll.PreviewMouseMove += (_, e) => { if (_drag is { } start && e.LeftButton == MouseButtonState.Pressed) { var now = e.GetPosition(_scroll); _scroll.ScrollToHorizontalOffset(_startX + start.X - now.X); _scroll.ScrollToVerticalOffset(_startY + start.Y - now.Y); } };
        _scroll.PreviewMouseLeftButtonUp += (_, _) => { _drag = null; _scroll.ReleaseMouseCapture(); };
        _scroll.LostMouseCapture += (_, _) => _drag = null;
    }
}

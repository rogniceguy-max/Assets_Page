using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MuYingDrive.NativeWpf.Views;

public enum RecycledProjectItemKind
{
    Project,
    Workbook
}

public sealed record RecycledProjectItem(
    RecycledProjectItemKind Kind,
    string Id,
    string Name,
    string ProjectId,
    string ProjectName,
    DateTimeOffset DeletedAt)
{
    public string KindLabel => Kind == RecycledProjectItemKind.Project ? "项目" : "工作簿";
    public string LocationLabel => Kind == RecycledProjectItemKind.Project ? "项目" : $"{ProjectName} · 工作簿";
}

public sealed class ProjectRecycleBinWindow : Window
{
    private readonly ListBox _items;

    public ProjectRecycleBinWindow(IReadOnlyList<RecycledProjectItem> items)
    {
        Title = "项目回收站";
        Width = 700;
        Height = 520;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current?.TryFindResource("Brush.Surface.Canvas") as Brush ?? Brushes.Transparent;

        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;
        _items = new ListBox
        {
            ItemsSource = items.OrderByDescending(item => item.DeletedAt).ToArray(),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            ItemTemplate = CreateTemplate()
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        var close = new Button { Content = "关闭", MinWidth = 88, Margin = new Thickness(0, 14, 8, 0) };
        close.Click += (_, _) => { DialogResult = false; Close(); };
        actions.Children.Add(close);
        var restore = new Button { Content = "恢复所选项目或工作簿", MinWidth = 170, Margin = new Thickness(0, 14, 0, 0) };
        restore.Click += (_, _) =>
        {
            if (_items.SelectedItem is RecycledProjectItem) { DialogResult = true; Close(); }
        };
        actions.Children.Add(restore);

        root.Children.Add(_items);
        if (items.Count == 0)
        {
            root.Children.Insert(0, new TextBlock
            {
                Text = "没有已删除的项目或工作簿。",
                Margin = new Thickness(0, 8, 0, 12),
                Foreground = Application.Current?.TryFindResource("Brush.Text.Secondary") as Brush ?? Brushes.Gray
            });
        }
        else _items.SelectedIndex = 0;
    }

    public RecycledProjectItem? SelectedItem => _items.SelectedItem as RecycledProjectItem;

    private static DataTemplate CreateTemplate()
    {
        var template = new DataTemplate(typeof(RecycledProjectItem));
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.MarginProperty, new Thickness(6, 8, 6, 8));

        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(RecycledProjectItem.Name)));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        panel.AppendChild(title);

        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding { Converter = new ItemSummaryConverter() });
        detail.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
        detail.SetValue(TextBlock.ForegroundProperty,
            Application.Current?.TryFindResource("Brush.Text.Secondary") as Brush ?? Brushes.Gray);
        panel.AppendChild(detail);

        template.VisualTree = panel;
        return template;
    }

    private sealed class ItemSummaryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not RecycledProjectItem item) return string.Empty;
            return $"{item.KindLabel} · {item.LocationLabel} · 删除于 {item.DeletedAt.LocalDateTime:g}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }
}

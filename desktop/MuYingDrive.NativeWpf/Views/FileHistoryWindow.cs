using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Views;

public sealed class FileHistoryWindow : Window
{
    private readonly ListBox _revisions;

    public FileHistoryWindow(string fileName, IReadOnlyList<FileRevisionEntry> revisions, bool isReadOnly = false)
    {
        Title = $"版本历史 · {fileName}";
        Width = 680;
        Height = 520;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current?.TryFindResource("Brush.Surface.Canvas") as Brush ?? Brushes.Transparent;

        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;
        _revisions = new ListBox();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        var close = new Button { Content = "关闭", MinWidth = 88, Margin = new Thickness(0, 14, 8, 0) };
        close.Click += (_, _) => { DialogResult = false; Close(); };
        actions.Children.Add(close);
        var restore = new Button { Content = "恢复所选版本", MinWidth = 120, Margin = new Thickness(0, 14, 0, 0) };
        restore.Click += (_, _) =>
        {
            if (_revisions.SelectedItem is FileRevisionEntry) { DialogResult = true; Close(); }
        };
        if (isReadOnly) restore.Visibility = Visibility.Collapsed;
        actions.Children.Add(restore);

        _revisions.ItemsSource = revisions.OrderByDescending(item => item.RevisionNumber).ToArray();
        _revisions.BorderThickness = new Thickness(1);
        _revisions.Padding = new Thickness(6);
        _revisions.ItemTemplate = CreateRevisionTemplate();
        root.Children.Add(_revisions);
        if (revisions.Count > 0) _revisions.SelectedIndex = 0;
        else root.Children.Insert(0, new TextBlock
        {
            Text = "此文件还没有可恢复的历史版本。",
            Margin = new Thickness(0, 8, 0, 12),
            Foreground = Application.Current?.TryFindResource("Brush.Text.Secondary") as Brush ?? Brushes.Gray
        });
    }

    public FileRevisionEntry? SelectedRevision => _revisions.SelectedItem as FileRevisionEntry;

    private static DataTemplate CreateRevisionTemplate()
    {
        var template = new DataTemplate(typeof(FileRevisionEntry));
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.MarginProperty, new Thickness(6, 8, 6, 8));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileRevisionEntry.RevisionNumber)) { StringFormat = "版本 {0}" });
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        panel.AppendChild(title);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding { Converter = new RevisionSummaryConverter() });
        detail.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
        detail.SetValue(TextBlock.ForegroundProperty,
            Application.Current?.TryFindResource("Brush.Text.Secondary") as Brush ?? Brushes.Gray);
        panel.AppendChild(detail);
        template.VisualTree = panel;
        return template;
    }

    private sealed class RevisionSummaryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not FileRevisionEntry revision) return string.Empty;
            var actor = string.IsNullOrWhiteSpace(revision.ActorName) ? "未知用户" : revision.ActorName;
            var source = revision.Source == "sync" ? "同步目录" : "WPF 操作";
            return $"{revision.CreatedAt.LocalDateTime:g} · {actor} · {source} · {FormatSize(revision.Size)}";
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
        private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024d:0.#} KB";
    }
}

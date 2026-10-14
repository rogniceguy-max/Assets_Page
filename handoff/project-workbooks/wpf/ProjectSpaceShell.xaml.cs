using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MuYingDrive.ProjectWorkbooks;

public partial class ProjectSpaceShell : UserControl
{
    private ProjectSpaceShellViewModel Model => DataContext as ProjectSpaceShellViewModel
        ?? throw new InvalidOperationException("宿主必须为 ProjectSpaceShell 设置 ProjectSpaceShellViewModel。");

    public ProjectSpaceShell() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => AdoptHostTheme();

    private void AdoptHostTheme()
    {
        var ancestor = Parent as FrameworkElement;
        foreach (var key in new[]
        {
            "Brush.Surface.Canvas", "Brush.Surface.Panel", "Brush.Border.Subtle",
            "Brush.Text.Primary", "Brush.Text.Secondary", "Brush.Accent.Primary",
            "Brush.Status.Danger", "Typography.FontFamily"
        })
        {
            var resource = ancestor?.TryFindResource(key) ?? Application.Current?.TryFindResource(key);
            if (resource != null) Resources.Remove(key);
        }
    }

    private void OnPrimaryNavigationClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key) Model.NavigatePrimary(key);
    }

    private void OnProjectTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ProjectSpaceNavigationNode node) Model.Navigate(node);
    }

    private void OnBeginCreateProject(object sender, RoutedEventArgs e) => Model.BeginCreateProject();
    private void OnCreateProject(object sender, RoutedEventArgs e) => Model.RequestCreateProject();
    private void OnCancelCreateProject(object sender, RoutedEventArgs e) => Model.CancelCreateProject();
    private void OnBeginRenameProject(object sender, RoutedEventArgs e) => Model.BeginRenameProject();
    private void OnRenameProject(object sender, RoutedEventArgs e) => Model.RequestRenameProject();
    private void OnCancelRenameProject(object sender, RoutedEventArgs e) => Model.CancelRenameProject();
    private void OnOpenRecycleBin(object sender, RoutedEventArgs e) => Model.OpenRecycleBin();
    private void OnBackFromRecycleBin(object sender, RoutedEventArgs e) => Model.BackFromRecycleBin();

    private void OnOpenWorkbookCard(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectSpaceWorkbookRecord workbook)
            Model.OpenWorkbook(workbook);
    }

    private void OnImportWorkbook(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "导入工作簿",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            Multiselect = true
        };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
            Model.RequestWorkbookImport(picker.FileNames);
    }

    private void OnWorkbookDragOver(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        e.Effects = Model.CanImportWorkbook && paths.Length > 0 && paths.All(path =>
            string.Equals(System.IO.Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWorkbookDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        Model.RequestWorkbookImport(paths);
    }

    private void OnCreateProjectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Model.RequestCreateProject(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Model.CancelCreateProject(); }
    }

    private void OnRenameProjectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Model.RequestRenameProject(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Model.CancelRenameProject(); }
    }

    private void OnImportMedia(object sender, RoutedEventArgs e)
    {
        var kind = Model.ActiveMediaKind;
        var picker = kind == ProjectSpaceMediaKind.Image
            ? new OpenFileDialog
            {
                Title = "导入图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.heic",
                Multiselect = true
            }
            : new OpenFileDialog
            {
                Title = "导入视频",
                Filter = "视频文件|*.mp4;*.mov;*.m4v;*.avi;*.mkv;*.webm",
                Multiselect = true
            };

        if (picker.ShowDialog(Window.GetWindow(this)) == true)
            Model.RequestMediaUpload(picker.FileNames, kind);
    }

    private void OnMediaDragOver(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        e.Effects = Model.CanAcceptDroppedFiles(paths, Model.ActiveMediaKind)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnMediaDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        Model.RequestMediaUpload(paths, Model.ActiveMediaKind);
    }

    private void OnOpenMedia(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectSpaceMediaRecord item)
            Model.RequestOpenMedia(item);
    }

    private void OnArchiveMedia(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectSpaceMediaRecord item)
            Model.RequestArchive(item);
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectSpaceRecycleBinRecord item)
            Model.RequestRestore(item);
    }
}

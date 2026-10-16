using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;

namespace MuYingDrive.NativeWpf.Views;

/// <summary>
/// The shared file browser. Driven by <see cref="IFileBrowser"/> in both employee and
/// read-only visitor workspaces.
/// </summary>
public partial class FilesView : UserControl
{
    private IFileBrowser? _viewModel;

    public FilesView()
    {
        InitializeComponent();
        DataContextChanged += HandleDataContextChanged;
    }

    private void HandleDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.SelectionRequested -= HandleSelectionRequested;
        }

        _viewModel = DataContext as IFileBrowser;
        if (_viewModel is not null)
        {
            _viewModel.SelectionRequested += HandleSelectionRequested;
        }
    }

    private void HandleSelectionRequested(SelectionRequest request)
    {
        var list = _viewModel?.IsGridView == true ? GalleryList : FileList;
        if (request == SelectionRequest.SelectAll)
        {
            list.SelectAll();
        }
        else
        {
            list.SelectedItems.Clear();
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListBox list && DataContext is IFileBrowser viewModel)
        {
            // The items, not a count: rename, duplicate and delete act on what is selected.
            viewModel.UpdateSelection(list.SelectedItems.OfType<DriveFileItem>().ToArray());
        }
    }

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (sender is not ListBox list || DataContext is not IFileBrowser viewModel)
        {
            return;
        }

        if (args.Key == Key.Enter && list.SelectedItem is not null)
        {
            viewModel.OpenCommand.Execute(list.SelectedItem);
            args.Handled = true;
        }
        else if (args.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            list.SelectAll();
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            list.SelectedItems.Clear();
            args.Handled = true;
        }
        else if (args.Key == Key.Delete && list.SelectedItem is not null && viewModel.CanModify)
        {
            // The row is passed so the command can apply itself to the whole selection when
            // that row belongs to one, and to the row alone otherwise. A read-only browser
            // never reaches here: CanModify is false, so there is no delete path to invoke.
            viewModel.DeleteCommand.Execute(list.SelectedItem);
            args.Handled = true;
        }
    }

    private void GalleryItem_MouseDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (sender is ListBoxItem item && DataContext is IFileBrowser viewModel)
        {
            viewModel.OpenCommand.Execute(item.DataContext);
            args.Handled = true;
        }
    }

    private void GalleryItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is not ListBoxItem item)
        {
            return;
        }

        if (!item.IsSelected)
        {
            if (ItemsControl.ItemsControlFromItemContainer(item) is ListBox owner)
            {
                owner.SelectedItems.Clear();
            }

            item.IsSelected = true;
        }

        item.Focus();
    }

    private void Files_DragOver(object sender, DragEventArgs args)
    {
        var paths = args.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var target = FindDirectoryDropTarget(args.OriginalSource as DependencyObject);
        var allowed = DataContext is IFileBrowser { CanAcceptFileDrop: true } && paths.Length > 0 &&
                      paths.All(path => File.Exists(path) || Directory.Exists(path)) &&
                      (target is null || target.IsDirectory) && !IsOverFileItem(args.OriginalSource as DependencyObject);
        if (DataContext is IFileBrowser viewModel) viewModel.SetDropTargetActive(allowed);
        args.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private void Files_DragLeave(object sender, DragEventArgs args)
    {
        if (DataContext is IFileBrowser viewModel && sender is FrameworkElement element && !element.IsMouseOver)
            viewModel.SetDropTargetActive(false);
    }

    private async void Files_Drop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (DataContext is not IFileBrowser viewModel) return;
        viewModel.SetDropTargetActive(false);
        var paths = args.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        if (paths.Length == 0 || IsOverFileItem(args.OriginalSource as DependencyObject)) return;
        await viewModel.HandleFileDropAsync(paths, FindDirectoryDropTarget(args.OriginalSource as DependencyObject));
    }

    private static bool IsOverFileItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem { DataContext: DriveFileItem { IsDirectory: false } }) return true;
            if (source is FrameworkContentElement content) { source = content.Parent; continue; }
            try { source = VisualTreeHelper.GetParent(source); }
            catch (InvalidOperationException) { return false; }
        }
        return false;
    }

    private static DriveFileItem? FindDirectoryDropTarget(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem { DataContext: DriveFileItem item })
                return item.IsDirectory ? item : null;

            if (source is FrameworkContentElement content)
            {
                source = content.Parent;
                continue;
            }

            try { source = VisualTreeHelper.GetParent(source); }
            catch (InvalidOperationException) { return null; }
        }
        return null;
    }
}

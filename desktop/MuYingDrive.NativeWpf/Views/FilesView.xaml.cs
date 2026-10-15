using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
}

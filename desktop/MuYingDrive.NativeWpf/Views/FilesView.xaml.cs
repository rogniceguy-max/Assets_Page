using MuYingDrive.NativeWpf.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MuYingDrive.NativeWpf.Views;

/// <summary>
/// The shared file browser. Driven by <see cref="IFileBrowser"/> so the employee file
/// page and the visitor tour render exactly the same view.
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
            viewModel.UpdateSelectionCount(list.SelectedItems.Count);
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
            // A read-only browser has no delete path to invoke at all.
            viewModel.UnsupportedEditCommand.Execute("删除");
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

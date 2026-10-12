using MuYingDrive.NativeWpf.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MuYingDrive.NativeWpf.Views;

public partial class FilesView : UserControl
{
    private FilesViewModel? _viewModel;

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

        _viewModel = DataContext as FilesViewModel;
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
        if (sender is ListBox list && DataContext is FilesViewModel viewModel)
        {
            viewModel.UpdateSelectionCount(list.SelectedItems.Count);
        }
    }

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (sender is not ListBox list || DataContext is not FilesViewModel viewModel)
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
        else if (args.Key == Key.Delete && list.SelectedItem is not null)
        {
            viewModel.UnsupportedEditCommand.Execute("删除");
            args.Handled = true;
        }
    }

    private void GalleryItem_MouseDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (sender is ListBoxItem item && DataContext is FilesViewModel viewModel)
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

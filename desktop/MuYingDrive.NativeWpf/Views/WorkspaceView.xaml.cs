using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MuYingDrive.NativeWpf.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView() => InitializeComponent();

    private void AccountButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is ContextMenu menu)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnProjectNodeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ProjectTree_ContextMenuOpening(sender, e);
    }

    private void ProjectTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement fe)
        {
            if (fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.DataContext = fe.DataContext;
            }

            if (fe.DataContext is ViewModels.ProjectNode node)
            {
                (DataContext as ViewModels.WorkspaceViewModel)?.SelectProjectNode(node);
            }
            else
            {
                e.Handled = true;
            }
        }
    }

    private void OnProjectNodePreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: ViewModels.ProjectNode node })
        {
            (DataContext as ViewModels.WorkspaceViewModel)?.SelectProjectNode(node);
        }
    }
}

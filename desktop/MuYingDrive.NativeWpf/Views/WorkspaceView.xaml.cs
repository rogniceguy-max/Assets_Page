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
        if (sender is System.Windows.FrameworkElement { DataContext: ViewModels.ProjectNode node })
        {
            if (!node.IsOpenable && !node.IsProject)
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

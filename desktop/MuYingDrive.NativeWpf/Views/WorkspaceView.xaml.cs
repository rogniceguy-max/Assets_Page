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
}

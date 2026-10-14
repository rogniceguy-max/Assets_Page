using System.ComponentModel;
using System.Windows;

namespace MuYingDrive.ProjectWorkbooks;

public partial class ReviewWindow : Window
{
    public ReviewWindow(ReviewViewModel model) { InitializeComponent(); DataContext = model; }

    // The panel holds the draft and the in-flight request, so it owns both refusals: wait for
    // the current bounded request, and ask before dropping input that has not been sent.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!Panel.TryLeave()) e.Cancel = true;
    }
}

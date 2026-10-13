using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.NativeWpf.ViewModels;

namespace MuYingDrive.NativeWpf.Views;

public partial class TransferView : UserControl
{
    public TransferView() => InitializeComponent();

    private void ReportFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TransferTask task } || task.State != TransferState.Failed) return;
        DependencyObject? parent = this;
        while (parent is not null && parent is not WorkspaceView) parent = VisualTreeHelper.GetParent(parent);
        if (parent is not WorkspaceView { DataContext: WorkspaceViewModel workspace }) return;
        var draft = workspace.Feedback.Draft;
        if (string.IsNullOrWhiteSpace(draft.Title) && string.IsNullOrWhiteSpace(draft.Description) && draft.Images.Count == 0)
        {
            var summary = FeedbackFailureSummary.From(task);
            draft.Kind = "problem"; draft.Category = summary.Category;
            draft.Title = summary.Title; draft.Description = summary.Description;
            draft.PrivacyConfirmed = false;
        }
        new FeedbackCreateWindow(draft) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}


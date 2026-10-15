using System.Windows;
using System.Windows.Controls;

namespace MuYingDrive.ProjectWorkbooks;

// HOST ADJUSTMENT. This markup and these handlers were ReviewWindow's, which is now a thin
// shell around this control. The discussion is reachable from two places — the asset row's
// 讨论 button, which still opens a window, and the beat detail panel, which has no room for
// one — and the alternative was a second copy of the comment markup in the panel. A second
// copy is how the two start disagreeing about what a comment looks like.
public partial class ReviewPanel : UserControl
{
    private bool _loaded;
    public ReviewPanel() => InitializeComponent();
    private ReviewViewModel Model => (ReviewViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return; _loaded = true;
        await Model.ExecuteAsync(Model.RefreshAsync);
    }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Model.ExecuteAsync(Model.RefreshAsync);
    private async void OnVote(object sender, RoutedEventArgs e) => await Model.ExecuteAsync(() => Model.VoteAsync((string)((FrameworkElement)sender).Tag));
    private async void OnWithdraw(object sender, RoutedEventArgs e) => await Model.ExecuteAsync(() => Model.VoteAsync(null));
    private async void OnSend(object sender, RoutedEventArgs e) => await Model.ExecuteAsync(Model.SendAsync);
    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ReviewComment comment) return;
        if (Model.WouldReplaceDraft(comment) && MessageBox.Show(Window.GetWindow(this), "切换编辑会替换当前输入的草稿，是否继续？", "保留草稿",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Model.BeginEdit(comment);
    }
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ReviewComment comment) return;
        if (Model.HasDraft && MessageBox.Show(Window.GetWindow(this), "删除正在编辑的评论可能同时清除该评论的草稿。仍要删除？", "删除评论", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Model.ExecuteAsync(() => Model.DeleteAsync(comment));
    }
    /// <summary>Asked before this panel is taken away; false means keep it and its draft.</summary>
    public bool TryLeave() => !Model.Busy && ConfirmDiscard();
    private bool ConfirmDiscard() => !Model.HasDraft || MessageBox.Show(Window.GetWindow(this), "当前输入尚未保存，是否放弃这份草稿？", "保留草稿", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void OnCancelEdit(object sender, RoutedEventArgs e) { if (ConfirmDiscard()) Model.CancelEdit(); }
}

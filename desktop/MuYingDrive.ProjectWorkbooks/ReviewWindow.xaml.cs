using System.ComponentModel;
using System.Windows;

namespace MuYingDrive.ProjectWorkbooks;

public partial class ReviewWindow : Window
{
    private ReviewViewModel Model => (ReviewViewModel)DataContext;
    private bool _loaded;
    public ReviewWindow(ReviewViewModel model) { InitializeComponent(); DataContext = model; }
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
        if (Model.WouldReplaceDraft(comment) && MessageBox.Show(this, "切换编辑会替换当前输入的草稿，是否继续？", "保留草稿",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Model.BeginEdit(comment);
    }
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ReviewComment comment) return;
        if (Model.HasDraft && MessageBox.Show(this, "删除正在编辑的评论可能同时清除该评论的草稿。仍要删除？", "删除评论", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Model.ExecuteAsync(() => Model.DeleteAsync(comment));
    }
    private bool ConfirmDiscard() => !Model.HasDraft || MessageBox.Show(this, "当前输入尚未保存，是否放弃这份草稿？", "保留草稿", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void OnCancelEdit(object sender, RoutedEventArgs e) { if (ConfirmDiscard()) Model.CancelEdit(); }
    private async void OnResolve(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ReviewComment comment || !comment.CanResolve) return;
        var note = comment.Resolved ? "" : WorkflowDialogs.Prompt(this, Resources, "标记已解决", "填写解决说明；处理状态与好/不好评价无关。", Model.ResolutionDraft(comment), allowEmpty: false);
        if (note == null) return;
        await Model.ExecuteAsync(() => Model.ResolveAsync(comment, !comment.Resolved, note));
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Wait for the current bounded request before parent summaries refresh.
        if (Model.Busy || !ConfirmDiscard()) e.Cancel = true;
    }
}

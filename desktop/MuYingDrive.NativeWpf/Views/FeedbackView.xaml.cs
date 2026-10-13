using System.Windows;
using System.Windows.Controls;
using MuYingDrive.NativeWpf.ViewModels;

namespace MuYingDrive.NativeWpf.Views;

public partial class FeedbackView : UserControl
{
    public FeedbackView() => InitializeComponent();
    private async void NewFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FeedbackViewModel model) return;
        var dialog = new FeedbackCreateWindow(model.Draft) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        await model.EnsureLoadedAsync();
    }
}

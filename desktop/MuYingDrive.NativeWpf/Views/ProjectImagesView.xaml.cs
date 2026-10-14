using System.Windows;
using System.Windows.Controls;
using MuYingDrive.NativeWpf.ViewModels;

namespace MuYingDrive.NativeWpf.Views;

public partial class ProjectImagesView : UserControl
{
    private ProjectImagesViewModel Model => DataContext as ProjectImagesViewModel
        ?? throw new InvalidOperationException("宿主必须提供 ProjectImagesViewModel。");

    public ProjectImagesView() => InitializeComponent();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await Model.LoadAsync();
    private void OnLoadMore(object sender, RoutedEventArgs e) => Model.LoadMore();
}

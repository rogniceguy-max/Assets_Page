using System.Windows;
using MuYingDrive.Updater.ViewModels;
using MuYingDrive.Updater.Views;

namespace MuYingDrive.Updater;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new UpdaterWindow();
        MainWindow = window;
        window.Show();
        await window.RunAsync(e.Args);
    }
}

using System.Windows;
using MuYingDrive.Bootstrap.Views;

namespace MuYingDrive.Bootstrap;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new SetupWindow(e.Args);
        MainWindow = window;
        window.Show();
        await window.RunAsync(e.Args);
    }
}

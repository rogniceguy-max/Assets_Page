using System.Windows;

namespace AssetsPage.WpfCfHdropSpike;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Window window = e.Args.Any(arg => string.Equals(arg, "--receiver", StringComparison.OrdinalIgnoreCase))
            ? new ReceiverWindow()
            : new MainWindow();

        MainWindow = window;
        window.Show();
    }
}

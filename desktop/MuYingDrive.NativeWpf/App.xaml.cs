using System.Windows;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.NativeWpf;

public partial class App : Application
{
    public static bool IsUpdateHealthCheck { get; private set; }
    private FileStream? _executionLock;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsUpdateHealthCheck = e.Args.Contains("--update-health-check", StringComparer.Ordinal);
        try
        {
            if (!IsUpdateHealthCheck)
                _executionLock = UpdateExecutionGuard.AcquireClientLock();
        }
        catch (IOException)
        {
            if (!IsUpdateHealthCheck)
                MessageBox.Show("沐影盘正在更新，完成后请重新启动。", "更新进行中", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            if (!IsUpdateHealthCheck)
                MessageBox.Show("当前用户无法安全启动沐影盘。请联系管理员。", "无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (IsUpdateHealthCheck)
        {
            try
            {
                var healthWindow = new MainWindow();
                MainWindow = healthWindow;
                healthWindow.ShowInTaskbar = false;
                healthWindow.ShowActivated = false;
                healthWindow.WindowState = WindowState.Minimized;
                healthWindow.Show();
                await healthWindow.RunUpdateHealthCheckAsync(e.Args);
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}

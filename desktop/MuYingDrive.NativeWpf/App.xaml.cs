using System.Text;
using System.Windows;
using System.Windows.Threading;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.NativeWpf;

public partial class App : Application
{
    public static bool IsUpdateHealthCheck { get; private set; }
    private FileStream? _executionLock;
    private static int _dialogActive;

    /// <summary>Where a crash is written, so there is somewhere to look afterwards.</summary>
    public static string CrashLogPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuYingDrive", "client-crash.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Log unhandled UI exceptions before WPF terminates the app. Continuing after an
        // unknown UI failure can leave the application in an inconsistent state.
        DispatcherUnhandledException += (_, args) =>
        {
            Record(args.Exception, "界面线程");

            if (Interlocked.CompareExchange(ref _dialogActive, 1, 0) == 0)
            {
                try
                {
                    MessageBox.Show(
                        $"沐影盘遇到了一个未预期的问题，已记录到：\n{CrashLogPath}\n\n若应用出现异常，可尝试重启软件。",
                        "沐影盘遇到问题", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Exchange(ref _dialogActive, 0);
                }
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record(args.ExceptionObject as Exception, "后台线程");

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

    /// <summary>
    /// Appends a crash to <see cref="CrashLogPath"/>, never throwing.
    /// </summary>
    /// <remarks>
    /// Appends rather than overwrites: the interesting crash is often the second one, and a
    /// log that keeps only the latest loses the sequence. Everything here is wrapped,
    /// including the write itself — a crash handler that throws is a crash handler that
    /// loses the crash it was reporting.
    /// </remarks>
    public static void Record(Exception? exception, string origin)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var entry = new StringBuilder()
                .AppendLine($"--- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {origin} ---")
                .AppendLine(exception?.ToString() ?? "(no exception object)")
                .AppendLine()
                .ToString();
            File.AppendAllText(CrashLogPath, entry, Encoding.UTF8);
        }
        catch
        {
            // Nothing useful left to do; the process is ending either way.
        }
    }
}

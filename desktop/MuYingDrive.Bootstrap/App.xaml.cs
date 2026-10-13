using System.Text.Json;
using System.Windows;
using MuYingDrive.Bootstrap.Views;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.Bootstrap;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // Traced before the window exists so a hang during startup is distinguishable
        // from a hang during the install.
        Services.InstallTrace.Write($"app startup: args=[{string.Join(' ', e.Args)}]");
        SetupWindow window;
        try
        {
            window = new SetupWindow(e.Args);
        }
        catch (Exception exception)
        {
            Services.InstallTrace.Write($"FAILED constructing the window: {exception.GetType().Name}: {exception.Message}");
            MessageBox.Show(exception.Message, "沐影盘安装程序", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 2;
            return;
        }

        MainWindow = window;

        try
        {
            window.Show();
            await window.RunAsync(e.Args);
            WriteResult(window.ViewModel.Title, window.ViewModel.Explanation, window.ViewModel.IsSucceeded, exception: null);
            Environment.ExitCode = window.ViewModel.IsSucceeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            WriteResult("安装程序异常", exception.Message, succeeded: false, exception.GetType().Name);
            Environment.ExitCode = 2;
            // The window may never have been shown, so surface the failure rather than
            // exiting silently.
            MessageBox.Show(exception.Message, "沐影盘安装程序", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Records the outcome next to the user data.
    /// </summary>
    /// <remarks>
    /// Unattended (<c>--quiet</c>) runs close the window as soon as the work finishes, so
    /// without this a failed install is completely invisible: the window disappears with
    /// no message and nothing to inspect. The exit code carries the same information for
    /// scripts. This file is deliberately not deleted on success so the last run is always
    /// auditable.
    /// </remarks>
    private static void WriteResult(string title, string explanation, bool succeeded, string? exception)
    {
        try
        {
            Directory.CreateDirectory(UpdatePathPolicy.UserDataRoot);
            var content = JsonSerializer.Serialize(new
            {
                succeeded,
                title,
                explanation,
                exception,
                exitCode = succeeded ? 0 : 1,
                timestampUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(UpdatePathPolicy.UserDataRoot, "installer-result.json"), content);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

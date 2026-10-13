using System.Diagnostics;
using System.Windows.Input;
using MuYingDrive.Bootstrap.Infrastructure;
using MuYingDrive.Bootstrap.Services;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.Bootstrap.ViewModels;

/// <summary>
/// Drives the single setup window. There is no wizard: the window shows the current
/// stage, an indeterminate progress bar (the payload size is known but extraction
/// progress is not), and a status line, matching the updater window's block order.
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    private string _title = "正在安装沐影盘";
    private string _explanation = "正在准备安装，请稍候。";
    private string _status = "准备中";
    private bool _isBusy = true;
    private bool _isSucceeded;
    private bool _hasFailed;
    private string? _launchPath;
    private readonly CancellationTokenSource _cancellation = new();

    public SetupViewModel(Action closeWindow)
    {
        CloseCommand = new RelayCommand(_ => closeWindow(), _ => !IsBusy);
        LaunchCommand = new RelayCommand(_ => LaunchAndClose(closeWindow), _ => !IsBusy && HasLaunchPath);
    }

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Explanation { get => _explanation; private set => SetProperty(ref _explanation, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(ActionText)); } }
    public bool IsSucceeded { get => _isSucceeded; private set => SetProperty(ref _isSucceeded, value); }
    public bool HasFailed { get => _hasFailed; private set => SetProperty(ref _hasFailed, value); }
    public bool HasLaunchPath => !string.IsNullOrWhiteSpace(_launchPath);
    public string ActionText => IsSucceeded ? "关闭" : "关闭";

    public ICommand CloseCommand { get; }
    public ICommand LaunchCommand { get; }

    public async Task RunAsync(string[] arguments)
    {
        Trace($"start: args=[{string.Join(' ', arguments)}] installRoot={InstallEngine.InstallRoot}");
        try
        {
            var uninstall = arguments.Contains("--uninstall");
            if (uninstall)
            {
                Title = "正在卸载沐影盘";
                Explanation = "正在移除程序文件，请稍候。";
                Status = "正在卸载";
            }

            var progress = new SynchronousUiProgress(ApplyProgress);
            var result = uninstall
                ? await InstallEngine.UninstallAsync(arguments.Contains("--purge"), _cancellation.Token)
                    .ConfigureAwait(true)
                : await InstallEngine.InstallAsync(
                        progress,
                        arguments.Contains("--desktop-shortcut"),
                        ReadOption(arguments, "--config"),
                        arguments.Contains("--force-config"),
                        ReadOption(arguments, "--package"),
                        ReadOption(arguments, "--manifest"),
                        _cancellation.Token)
                    .ConfigureAwait(true);

            ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
            // The window is closing; nothing to report.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or System.Security.SecurityException
            or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            SetFailure(exception.Message);
        }
        catch (Exception exception)
        {
            SetFailure($"安装未完成：{exception.Message}");
        }
    }

    private void ApplyProgress(InstallProgress progress)
    {
        Title = progress.Title;
        Explanation = progress.Explanation;
        Status = progress.Status;
        Trace($"{progress.Status}: {progress.Explanation}");
    }

    private static void Trace(string message) => InstallTrace.Write(message);

    private void ApplyResult(InstallResult result)
    {
        Title = result.Title;
        Explanation = result.Explanation;
        Status = result.Succeeded ? "已完成" : "未完成";
        IsBusy = false;
        IsSucceeded = result.Succeeded;
        HasFailed = !result.Succeeded;
        _launchPath = result.LaunchPath;
        OnPropertyChanged(nameof(HasLaunchPath));
    }

    private void SetFailure(string message)
    {
        Trace("FAILED: " + message);
        Title = "安装未完成";
        Explanation = message;
        Status = "未完成";
        IsBusy = false;
        HasFailed = true;
    }

    private void LaunchAndClose(Action closeWindow)
    {
        if (!string.IsNullOrWhiteSpace(_launchPath) && File.Exists(_launchPath))
        {
            Process.Start(new ProcessStartInfo(_launchPath) { UseShellExecute = true });
        }
        closeWindow();
    }

    private static string? ReadOption(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    private sealed class SynchronousUiProgress(Action<InstallProgress> apply) : IProgress<InstallProgress>
    {
        // Reports come from the install engine's own thread; applying them inline keeps
        // the window's stage text in step with the work.
        public void Report(InstallProgress value) => apply(value);
    }
}

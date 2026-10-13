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
    private bool _wasCancelled;
    private string? _launchPath;
    private double _progress;
    private bool _isProgressIndeterminate = true;
    private readonly CancellationTokenSource _cancellation = new();

    public SetupViewModel(Action closeWindow)
    {
        CloseCommand = new RelayCommand(_ => closeWindow(), _ => !IsBusy);
        LaunchCommand = new RelayCommand(_ => LaunchAndClose(closeWindow), _ => !IsBusy && HasLaunchPath);
        CancelCommand = new RelayCommand(_ => RequestCancel(), _ => IsBusy && !WasCancelled);
    }

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Explanation { get => _explanation; private set => SetProperty(ref _explanation, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(ActionText));
            RaiseCommandStates();
        }
    }
    public bool IsSucceeded { get => _isSucceeded; private set => SetProperty(ref _isSucceeded, value); }
    public bool HasFailed { get => _hasFailed; private set => SetProperty(ref _hasFailed, value); }
    public bool WasCancelled
    {
        get => _wasCancelled;
        private set
        {
            if (!SetProperty(ref _wasCancelled, value)) return;
            RaiseCommandStates();
        }
    }
    public bool HasLaunchPath => !string.IsNullOrWhiteSpace(_launchPath);
    public string ActionText => IsSucceeded ? "完成" : "关闭";

    /// <summary>How far the current stage has got, for the determinate progress bar.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    /// <summary>True while the current stage cannot measure itself (everything but the download).</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    // RelayCommand rather than ICommand throughout: every one of these decides whether it
    // is enabled from properties that change while the window is open, and only the
    // concrete type can raise CanExecuteChanged when they do.
    public RelayCommand CloseCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand CancelCommand { get; }

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
                        // A desktop shortcut is what a colleague who double-clicks Setup.exe
                        // expects; without it the only way in is the Start Menu. Opt out with
                        // --no-desktop-shortcut. An unattended deployment (--quiet) is the
                        // exception: it must not put icons on anyone's desktop unless the
                        // operator asks for it explicitly.
                        !arguments.Contains("--no-desktop-shortcut")
                            && (!arguments.Contains("--quiet") || arguments.Contains("--desktop-shortcut")),
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
            // The user cancelled, or closed the window while it was working. The engine
            // removes its own staging directory before the token reaches here, so the
            // machine is untouched — report that plainly rather than as a failure.
            SetCancelled();
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
        // Only the download knows its own size. Every other stage reports null, which
        // keeps the bar indeterminate instead of inventing a percentage for it.
        IsProgressIndeterminate = progress.Percent is null;
        if (progress.Percent is double percent) Progress = percent;
        Trace($"{progress.Status}: {progress.Explanation}");
    }

    /// <summary>
    /// Asks the running install to stop.
    /// </summary>
    /// <remarks>
    /// The token is the only lever, and that is deliberate: the engine checks it between
    /// stages and cleans up its own staging directory on the way out, so cancelling can
    /// never leave a half-written install behind. A cancel that lands after the version
    /// swap has already happened is simply ignored — those last steps do not await — and
    /// the install finishes normally instead of reporting a cancellation that did not occur.
    /// </remarks>
    private void RequestCancel()
    {
        if (!IsBusy || WasCancelled) return;
        WasCancelled = true;
        Status = "正在取消";
        Explanation = "正在安全停止，请稍候。";
        Trace("cancel requested by the user");
        _cancellation.Cancel();
    }

    private void SetCancelled()
    {
        Trace("CANCELLED.");
        Title = "已取消安装";
        Explanation = "安装已取消，未对这台电脑做任何更改。";
        Status = "已取消";
        IsBusy = false;
        IsProgressIndeterminate = true;
    }

    private static void Trace(string message) => InstallTrace.Write(message);

    /// <summary>
    /// Tells WPF to re-ask every command whether it is enabled.
    /// </summary>
    /// <remarks>
    /// WPF queries <c>CanExecute</c> once when the binding is applied and only asks again
    /// when the command raises <c>CanExecuteChanged</c>. The view model starts busy, so
    /// that first query answers "disabled" for everything — and without this the answer
    /// is never revisited. The buttons then render in the disabled chrome, where a dark
    /// theme's near-white label is invisible on the light system fill: the window looks
    /// finished but nothing on it can be clicked.
    /// </remarks>
    private void RaiseCommandStates()
    {
        CloseCommand.RaiseCanExecuteChanged();
        LaunchCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

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
        // After the launch path, not before: IsBusy above already re-asked, but it did so
        // while HasLaunchPath was still false, so the launch button would stay disabled.
        RaiseCommandStates();
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

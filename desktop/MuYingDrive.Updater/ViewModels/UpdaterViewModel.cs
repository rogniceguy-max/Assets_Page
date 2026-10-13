using System.Windows.Input;
using System.Windows;
using MuYingDrive.Updater.Infrastructure;
using MuYingDrive.Updater.Services;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.Updater.ViewModels;

public sealed class UpdaterViewModel : ObservableObject
{
    private string _title = "正在准备更新";
    private string _explanation = "正在等待沐影盘完成安全退出。";
    private string _status = "等待应用退出";
    private double _progress;
    private bool _isProgressIndeterminate = true;
    private bool _isBusy = true;
    private bool _isSucceeded;
    private bool _hasFailed;
    private bool _canCancel = true;
    private string? _launchPath;
    private UpdateFailureContext? _launchFailureContext;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly UpdateRunner _runner = new();

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Explanation { get => _explanation; private set => SetProperty(ref _explanation, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool IsSucceeded { get => _isSucceeded; private set => SetProperty(ref _isSucceeded, value); }
    public bool HasFailed { get => _hasFailed; private set => SetProperty(ref _hasFailed, value); }
    public bool CanCancel { get => _canCancel; private set => SetProperty(ref _canCancel, value); }
    public bool HasLaunchPath => !string.IsNullOrWhiteSpace(_launchPath);
    public string LaunchActionText => IsSucceeded ? "启动沐影盘" : "重新启动沐影盘";
    public string ActionText => IsSucceeded ? "关闭更新程序" : "关闭";
    public ICommand CloseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LaunchCommand { get; }

    public UpdaterViewModel(Action closeWindow)
    {
        CloseCommand = new RelayCommand(_ => closeWindow(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => CancelUpdate(), _ => IsBusy && CanCancel);
        LaunchCommand = new RelayCommand(_ => LaunchAndClose(closeWindow), _ => !IsBusy && HasLaunchPath);
    }

    public async Task RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length != 2 || arguments[0] != "--request")
            {
                SetFailure("更新请求无效。请从沐影盘重新发起更新。", "未开始安装");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            CanCancel = true;
            var progress = new SynchronousUiProgress(ApplyProgress);
            var result = await _runner.RunAsync(arguments[1], progress, _cancellationTokenSource.Token);
            Title = result.Title;
            Explanation = result.Explanation;
            Status = result.Succeeded ? "更新完成" : "更新未完成";
            IsSucceeded = result.Succeeded;
            HasFailed = !result.Succeeded;
            _launchPath = result.LaunchPath;
            _launchFailureContext = result.FailureContext;
            if (result.FailureReport is not null)
            {
                UpdateFailureReporter.Queue(result.FailureReport);
                _ = UpdateFailureReporter.SendPendingAsync();
            }
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(HasLaunchPath));
            OnPropertyChanged(nameof(LaunchActionText));
            if (result.Succeeded)
                LaunchAfterSuccessfulUpdate();
        }
        catch (Exception)
        {
            SetFailure("更新程序无法确认当前安装状态。请保留本机更新文件，联系管理员检查后再启动沐影盘。", "状态需要确认");
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
            IsProgressIndeterminate = false;
            Progress = 0;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            OnPropertyChanged(nameof(ActionText));
            RaiseCommands();
        }
    }

    private void ApplyProgress(UpdateProgress progress)
    {
        Title = progress.Title;
        Explanation = progress.Explanation;
        Status = progress.Status;
        CanCancel = progress.Cancellable;
        IsProgressIndeterminate = progress.Completed is null || progress.Total is null || progress.Total <= 0;
        if (!IsProgressIndeterminate)
            Progress = Math.Clamp(progress.Completed!.Value * 100d / progress.Total!.Value, 0, 100);
    }

    private void SetFailure(string message, string status)
    {
        IsBusy = false;
        HasFailed = true;
        IsSucceeded = false;
        IsProgressIndeterminate = false;
        Progress = 0;
        Title = "更新未完成";
        Explanation = message;
        Status = status;
        OnPropertyChanged(nameof(ActionText));
        RaiseCommands();
    }

    public void CancelUpdate()
    {
        if (!IsBusy || !CanCancel) return;
        CanCancel = false;
        Title = "正在取消更新";
        Explanation = "正在安全停止更新准备，当前已安装版本不会被替换。";
        Status = "正在取消";
        _cancellationTokenSource?.Cancel();
        RaiseCommands();
    }

    private void LaunchAndClose(Action closeWindow)
    {
        if (_launchPath is null) return;
        if (!TryStartClient())
        {
            ShowLaunchFailure();
            return;
        }
        closeWindow();
    }

    private void LaunchAfterSuccessfulUpdate()
    {
        if (_launchPath is null) return;
        if (!TryStartClient())
        {
            ShowLaunchFailure();
            return;
        }
        _launchPath = null;
        OnPropertyChanged(nameof(HasLaunchPath));
        RaiseCommands();
    }

    private bool TryStartClient()
    {
        if (_launchPath is null) return false;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_launchPath) { UseShellExecute = true });
            return process is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void ShowLaunchFailure()
    {
        if (_launchFailureContext is not null)
        {
            UpdateFailureReporter.Queue(_launchFailureContext.CreateReport("launch", "CLIENT_LAUNCH_FAILED"));
            _ = UpdateFailureReporter.SendPendingAsync();
        }
        Title = "沐影盘无法启动";
        Explanation = "更新已完成，但沐影盘未能启动。原版本备份仍保留；可以重试启动，或联系管理员恢复。";
        Status = "更新完成，启动失败";
        HasFailed = true;
        OnPropertyChanged(nameof(ActionText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        (CloseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LaunchCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private sealed class SynchronousUiProgress(Action<UpdateProgress> apply) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value)
        {
            var dispatcher = Application.Current.Dispatcher;
            if (dispatcher.CheckAccess()) apply(value);
            else dispatcher.Invoke(() => apply(value));
        }
    }
}

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

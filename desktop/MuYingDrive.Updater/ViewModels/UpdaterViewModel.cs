using System.Windows.Input;
using System.Windows;
using MuYingDrive.Updater.Infrastructure;
using MuYingDrive.Updater.Services;

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
                SetFailure("更新请求无效。请从沐影盘重新发起更新。");
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
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(HasLaunchPath));
            OnPropertyChanged(nameof(LaunchActionText));
        }
        catch (Exception)
        {
            SetFailure("更新过程遇到问题。当前版本仍保留；可关闭更新程序后重试或联系管理员。");
        }
        finally
        {
            IsBusy = false;
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

    private void SetFailure(string message)
    {
        IsBusy = false;
        HasFailed = true;
        IsSucceeded = false;
        Title = "更新未完成";
        Explanation = message;
        Status = "旧版本仍保留";
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
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_launchPath) { UseShellExecute = true }); }
        catch { return; }
        closeWindow();
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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class TransferViewModel : ObservableObject
{
    private readonly ResumableTransferEngine _engine;
    private string _operationErrorMessage = string.Empty;

    public TransferViewModel(long accountId)
    {
        _engine = new ResumableTransferEngine(accountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _engine.TransferChanged += HandleTransferChanged;
        Tasks = [];
        Tasks.CollectionChanged += HandleTasksChanged;
        PauseCommand = new AsyncRelayCommand(parameter => RunCommandSafelyAsync(
            () => PauseAsync(parameter as TransferTask), "暂停传输失败，请检查存储位置后重试。"));
        ResumeCommand = new AsyncRelayCommand(parameter => RunCommandSafelyAsync(
            () => ResumeAsync(parameter as TransferTask), "继续传输失败，请检查文件和 NAS 连接后重试。"));
        RetryCommand = new AsyncRelayCommand(parameter => RunCommandSafelyAsync(
            () => RetryAsync(parameter as TransferTask), "重试传输失败，请检查文件和 NAS 连接后重试。"));
        CancelCommand = new AsyncRelayCommand(parameter => RunCommandSafelyAsync(
            () => CancelAsync(parameter as TransferTask), "取消传输失败，请稍后重试。"));
    }

    public ObservableCollection<TransferTask> Tasks { get; }
    public bool HasTasks => Tasks.Count > 0;
    public bool IsEmpty => !HasTasks;
    public string OperationErrorMessage
    {
        get => _operationErrorMessage;
        private set
        {
            if (SetProperty(ref _operationErrorMessage, value))
                OnPropertyChanged(nameof(HasOperationError));
        }
    }
    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationErrorMessage);
    public AsyncRelayCommand PauseCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }

    public async Task InitializeAsync()
    {
        var restored = await _engine.InitializeAsync();
        foreach (var snapshot in restored)
            Upsert(snapshot);
    }

    public async Task AddDownloadAsync(DriveFileItem file, string localDestination)
    {
        ArgumentNullException.ThrowIfNull(file);
        await StartAsync(() => _engine.StartDownloadAsync(file.FullPath, localDestination));
    }

    public async Task AddUploadAsync(string localSource, string nasDestination)
    {
        await StartAsync(() => _engine.StartUploadAsync(localSource, nasDestination));
    }

    public async Task PauseAllAsync()
    {
        try
        {
            var running = Tasks.Where(task => task.State == TransferState.Running).ToArray();
            await Task.WhenAll(running.Select(task => _engine.PauseAsync(task.Id)));
        }
        catch (Exception)
        {
            OperationErrorMessage = "正在保存传输进度时遇到问题；未完成任务会在下次启动时检查。";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _engine.TransferChanged -= HandleTransferChanged;
        Tasks.CollectionChanged -= HandleTasksChanged;
        await _engine.DisposeAsync();
    }

    private async Task StartAsync(Func<Task<ResumableTransferSnapshot>> start)
    {
        await RunCommandSafelyAsync(async () => Upsert(await start()), "无法开始传输，请检查目标位置、权限和 NAS 连接后重试。");
    }

    private async Task RunCommandSafelyAsync(Func<Task> action, string fallbackMessage)
    {
        OperationErrorMessage = string.Empty;
        try
        {
            await action();
        }
        catch (IOException)
        {
            OperationErrorMessage = "文件暂时无法访问，或目标已存在；请检查目标位置和 NAS 连接后重试。";
        }
        catch (UnauthorizedAccessException)
        {
            OperationErrorMessage = "当前目录没有执行此操作的权限。";
        }
        catch (Exception)
        {
            OperationErrorMessage = fallbackMessage;
        }
    }

    private async Task PauseAsync(TransferTask? task)
    {
        if (task is not null)
            await _engine.PauseAsync(task.Id);
    }

    private async Task ResumeAsync(TransferTask? task)
    {
        if (task is not null)
            Upsert(await _engine.ResumeAsync(task.Id));
    }

    private async Task RetryAsync(TransferTask? task)
    {
        if (task is not null)
            Upsert(await _engine.RetryAsync(task.Id));
    }

    private async Task CancelAsync(TransferTask? task)
    {
        if (task is not null)
            await _engine.CancelAsync(task.Id);
    }

    private void HandleTransferChanged(object? sender, ResumableTransferChangedEventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (dispatcher.CheckAccess())
            Upsert(args.Snapshot);
        else
            dispatcher.BeginInvoke(() => Upsert(args.Snapshot));
    }

    private void Upsert(ResumableTransferSnapshot snapshot)
    {
        var task = Tasks.FirstOrDefault(item => item.Id == snapshot.Id);
        if (snapshot.Status == ResumableTransferStatus.Canceled)
        {
            if (task is not null)
                Tasks.Remove(task);
            return;
        }

        if (task is null)
        {
            Tasks.Insert(0, new TransferTask(snapshot));
            return;
        }

        task.Update(snapshot);
    }

    private void HandleTasksChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

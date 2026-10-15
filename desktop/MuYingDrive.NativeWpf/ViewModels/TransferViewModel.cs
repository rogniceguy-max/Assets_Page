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
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private string _operationErrorMessage = string.Empty;
    private bool _acceptTransferActions = true;

    public TransferViewModel(long accountId)
    {
        // A cap on concurrent workers, which only matters now that one action can queue many
        // files: uploading a folder of two hundred otherwise starts two hundred simultaneous
        // SMB streams, which is slower than a few at a time and makes the queue unreadable.
        // Downloads and single uploads behave exactly as before.
        _engine = new ResumableTransferEngine(
            accountId.ToString(System.Globalization.CultureInfo.InvariantCulture), maxConcurrentWorkers: 4);
        _engine.TransferChanged += HandleTransferChanged;
        Tasks = [];
        Tasks.CollectionChanged += HandleTasksChanged;
        PauseCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => PauseAsync(parameter as TransferTask), "暂停传输失败，请检查存储位置后重试。"), _ => _acceptTransferActions);
        ResumeCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => ResumeAsync(parameter as TransferTask), "继续传输失败，请检查文件和 NAS 连接后重试。"), _ => _acceptTransferActions);
        RetryCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => RetryAsync(parameter as TransferTask), "重试传输失败，请检查文件和 NAS 连接后重试。"), _ => _acceptTransferActions);
        CancelCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => CancelAsync(parameter as TransferTask), "取消传输失败，请稍后重试。"), _ => _acceptTransferActions);
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
        await StartTransferAsync(() => _engine.StartDownloadAsync(file.FullPath, localDestination));
    }

    public async Task AddUploadAsync(string localSource, string nasDestination)
    {
        await StartTransferAsync(() => _engine.StartUploadAsync(localSource, nasDestination));
    }

    public async Task<bool> PauseAllAsync()
    {
        await _lifecycleGate.WaitAsync();
        try { return await PauseRunningAndVerifyAsync(); }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<bool> PrepareForUpdateAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            _acceptTransferActions = false;
            RaiseTransferCommandStates();
            if (await PauseRunningAndVerifyAsync())
                return true;
            _acceptTransferActions = true;
            RaiseTransferCommandStates();
            return false;
        }
        catch
        {
            _acceptTransferActions = true;
            RaiseTransferCommandStates();
            return false;
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<bool> PauseRunningAndVerifyAsync()
    {
        try
        {
            var running = _engine.GetTasks().Where(task => task.Status == ResumableTransferStatus.Running).ToArray();
            await Task.WhenAll(running.Select(task => _engine.PauseAsync(task.Id)));
            var afterPause = _engine.GetTasks().ToDictionary(task => task.Id, StringComparer.Ordinal);
            return running.All(previous => afterPause.TryGetValue(previous.Id, out var current) &&
                current.Status is ResumableTransferStatus.Paused or ResumableTransferStatus.Completed);
        }
        catch (Exception)
        {
            OperationErrorMessage = "正在保存传输进度时遇到问题；未完成任务会在下次启动时检查。";
            return false;
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

    private async Task StartTransferAsync(Func<Task<ResumableTransferSnapshot>> start)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!_acceptTransferActions)
            {
                OperationErrorMessage = "正在准备安全退出，暂时不能开始新的传输。";
                return;
            }
            await StartAsync(start);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task RunLifecycleCommandAsync(Func<Task> action, string fallbackMessage)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_acceptTransferActions)
                await RunCommandSafelyAsync(action, fallbackMessage);
        }
        finally { _lifecycleGate.Release(); }
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

    private sealed class FolderTransferGroup
    {
        public string GroupId { get; }
        public string FolderName { get; }
        public string DestinationDirectory { get; }
        public TransferTask TaskItem { get; }
        public HashSet<string> ChildTaskIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ResumableTransferSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);

        public FolderTransferGroup(string groupId, string folderName, string destinationDirectory, TransferTask taskItem)
        {
            GroupId = groupId;
            FolderName = folderName;
            DestinationDirectory = destinationDirectory;
            TaskItem = taskItem;
        }

        public void UpdateGroupState()
        {
            long totalBytes = 0;
            long transferred = 0;
            var anyRunning = false;
            var anyPaused = false;
            var anyFailed = false;
            var allCompleted = Snapshots.Count > 0;
            string? firstError = null;

            foreach (var s in Snapshots.Values)
            {
                totalBytes += s.TotalBytes;
                transferred += s.BytesTransferred;
                if (s.Status == ResumableTransferStatus.Running) anyRunning = true;
                if (s.Status == ResumableTransferStatus.Paused) anyPaused = true;
                if (s.Status == ResumableTransferStatus.Failed)
                {
                    anyFailed = true;
                    firstError ??= s.Error;
                }
                if (s.Status != ResumableTransferStatus.Completed) allCompleted = false;
            }

            TransferState state = allCompleted
                ? TransferState.Completed
                : anyFailed
                    ? TransferState.Failed
                    : anyRunning
                        ? TransferState.Running
                        : anyPaused
                            ? TransferState.Paused
                            : TransferState.Queued;

            TaskItem.UpdateManual(totalBytes, transferred, state, firstError);
        }
    }

    private readonly List<FolderTransferGroup> _folderGroups = [];
    private readonly Dictionary<string, FolderTransferGroup> _taskIdToGroup = new(StringComparer.Ordinal);

    public async Task AddFolderUploadAsync(string folderName, string destinationDirectory, IReadOnlyList<(string LocalPath, string DestinationPath)> files)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!_acceptTransferActions)
            {
                OperationErrorMessage = "正在准备安全退出，暂时不能开始新的传输。";
                return;
            }

            long estimatedTotalBytes = 0;
            foreach (var (localPath, _) in files)
            {
                try
                {
                    if (File.Exists(localPath))
                        estimatedTotalBytes += new FileInfo(localPath).Length;
                }
                catch { }
            }

            var groupId = Guid.NewGuid().ToString("D");
            var folderTask = new TransferTask(groupId, folderName, "上传", estimatedTotalBytes, 0, TransferState.Queued);
            var group = new FolderTransferGroup(groupId, folderName, destinationDirectory, folderTask);
            _folderGroups.Add(group);
            Tasks.Insert(0, folderTask);

            foreach (var (localPath, destPath) in files)
            {
                var snapshot = await _engine.StartUploadAsync(localPath, destPath);
                group.ChildTaskIds.Add(snapshot.Id);
                group.Snapshots[snapshot.Id] = snapshot;
                _taskIdToGroup[snapshot.Id] = group;
            }
            group.UpdateGroupState();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task PauseAsync(TransferTask? task)
    {
        if (task is null) return;
        var group = _folderGroups.FirstOrDefault(g => g.TaskItem == task || g.GroupId == task.Id);
        if (group is not null)
        {
            foreach (var id in group.ChildTaskIds)
                await _engine.PauseAsync(id);
            return;
        }
        await _engine.PauseAsync(task.Id);
    }

    private async Task ResumeAsync(TransferTask? task)
    {
        if (task is null) return;
        var group = _folderGroups.FirstOrDefault(g => g.TaskItem == task || g.GroupId == task.Id);
        if (group is not null)
        {
            foreach (var id in group.ChildTaskIds)
            {
                var updated = await _engine.ResumeAsync(id);
                group.Snapshots[id] = updated;
            }
            group.UpdateGroupState();
            return;
        }
        Upsert(await _engine.ResumeAsync(task.Id));
    }

    private async Task RetryAsync(TransferTask? task)
    {
        if (task is null) return;
        var group = _folderGroups.FirstOrDefault(g => g.TaskItem == task || g.GroupId == task.Id);
        if (group is not null)
        {
            foreach (var id in group.ChildTaskIds)
            {
                var updated = await _engine.RetryAsync(id);
                group.Snapshots[id] = updated;
            }
            group.UpdateGroupState();
            return;
        }
        Upsert(await _engine.RetryAsync(task.Id));
    }

    private async Task CancelAsync(TransferTask? task)
    {
        if (task is null) return;
        var group = _folderGroups.FirstOrDefault(g => g.TaskItem == task || g.GroupId == task.Id);
        if (group is not null)
        {
            foreach (var id in group.ChildTaskIds)
            {
                await _engine.CancelAsync(id);
                _taskIdToGroup.Remove(id);
            }
            _folderGroups.Remove(group);
            Tasks.Remove(group.TaskItem);
            return;
        }
        await _engine.CancelAsync(task.Id);
    }

    /// <summary>
    /// Raised on the UI thread when an upload finishes, with the directory it landed in.
    /// </summary>
    /// <remarks>
    /// Nothing else tells the file browser that a transfer completed, so a newly uploaded
    /// file only appeared after navigating away and back. A folder upload made that
    /// impossible to ignore: the whole shape of the folder was on the NAS while the page
    /// still showed the listing from before it started.
    /// </remarks>
    public event Action<string>? UploadCompleted;

    private void HandleTransferChanged(object? sender, ResumableTransferChangedEventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (dispatcher.CheckAccess())
            ApplyTransferChanged(args.Snapshot);
        else
            dispatcher.BeginInvoke(() => ApplyTransferChanged(args.Snapshot));
    }

    private void ApplyTransferChanged(ResumableTransferSnapshot snapshot)
    {
        if (_taskIdToGroup.TryGetValue(snapshot.Id, out var group))
        {
            group.Snapshots[snapshot.Id] = snapshot;
            group.UpdateGroupState();
            if (group.TaskItem.State == TransferState.Completed)
            {
                UploadCompleted?.Invoke(group.DestinationDirectory);
            }
            return;
        }

        Upsert(snapshot);
        if (snapshot.Direction == ResumableTransferDirection.Upload &&
            snapshot.Status == ResumableTransferStatus.Completed)
        {
            UploadCompleted?.Invoke(snapshot.Location);
        }
    }

    private void Upsert(ResumableTransferSnapshot snapshot)
    {
        if (_taskIdToGroup.ContainsKey(snapshot.Id))
            return;

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

    private void RaiseTransferCommandStates()
    {
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}

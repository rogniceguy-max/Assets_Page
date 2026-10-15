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
        // A shared cap keeps a folder selection from opening one SMB stream per file.
        // Single transfers still run immediately when a worker slot is available.
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
        var displayItems = new List<(DateTimeOffset LastActivityUtc, TransferTask Task)>();

        foreach (var grouped in restored
                     .Where(snapshot => IsValidFolderContext(snapshot.FolderContext) &&
                         snapshot.Status != ResumableTransferStatus.Canceled)
                     .GroupBy(snapshot => snapshot.FolderContext!.GroupId, StringComparer.Ordinal))
        {
            var snapshots = grouped.ToArray();
            var context = snapshots[0].FolderContext!;
            var persistedExpectedCounts = snapshots
                .Select(snapshot => snapshot.FolderContext?.ExpectedChildCount ?? 0)
                .Where(count => count > 0)
                .ToArray();
            var expectedChildCount = Math.Max(snapshots.Length,
                persistedExpectedCounts.Length > 0 ? persistedExpectedCounts.Min() : snapshots.Length);
            var folderTask = new TransferTask(context.GroupId, context.FolderName, "上传", 0, 0,
                TransferState.Queued, isFolderTransfer: true);
            var group = new FolderTransferGroup(context, expectedChildCount, folderTask,
                enqueueComplete: true, incompleteMembership: snapshots.Length < expectedChildCount);
            _folderGroupsById[group.GroupId] = group;
            foreach (var snapshot in snapshots)
                RegisterFolderSnapshot(group, snapshot);

            displayItems.Add((snapshots.Max(snapshot => snapshot.LastActivityUtc), folderTask));
        }

        foreach (var snapshot in restored.Where(snapshot =>
                     !IsValidFolderContext(snapshot.FolderContext) &&
                     snapshot.Status != ResumableTransferStatus.Canceled))
        {
            displayItems.Add((snapshot.LastActivityUtc, new TransferTask(snapshot)));
        }

        foreach (var item in displayItems.OrderByDescending(item => item.LastActivityUtc))
            Tasks.Add(item.Task);
    }

    public async Task AddDownloadAsync(DriveFileItem file, string localDestination)
    {
        ArgumentNullException.ThrowIfNull(file);
        await StartTransferAsync(() => _engine.StartDownloadAsync(file.FullPath, localDestination));
    }

    public Task<bool> AddUploadAsync(string localSource, string nasDestination) =>
        StartTransferAsync(() => _engine.StartUploadAsync(localSource, nasDestination));

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
            var active = _engine.GetTasks()
                .Where(task => task.Status is ResumableTransferStatus.Running or ResumableTransferStatus.Queued)
                .ToArray();
            await Task.WhenAll(active.Select(task => _engine.PauseAsync(task.Id)));
            var afterPause = _engine.GetTasks().ToDictionary(task => task.Id, StringComparer.Ordinal);
            return active.All(previous => afterPause.TryGetValue(previous.Id, out var current) &&
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

    private Task<bool> StartAsync(Func<Task<ResumableTransferSnapshot>> start) =>
        RunCommandSafelyAsync(async () => Upsert(await start()),
            "无法开始传输，请检查目标位置、权限和 NAS 连接后重试。");

    private async Task<bool> StartTransferAsync(Func<Task<ResumableTransferSnapshot>> start)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!_acceptTransferActions)
            {
                OperationErrorMessage = "正在准备安全退出，暂时不能开始新的传输。";
                return false;
            }
            return await StartAsync(start);
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

    private async Task<bool> RunCommandSafelyAsync(Func<Task> action, string fallbackMessage)
    {
        OperationErrorMessage = string.Empty;
        try
        {
            await action();
            return true;
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
        return false;
    }

    private sealed class FolderTransferGroup
    {
        public TransferFolderContext Context { get; }
        public string GroupId => Context.GroupId;
        public string FolderName => Context.FolderName;
        public string DestinationDirectory => Context.DestinationDirectory;
        public int ExpectedChildCount { get; private set; }
        public bool HasIncompleteMembership => _incompleteMembership;
        public TransferTask TaskItem { get; }
        public HashSet<string> ChildTaskIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ResumableTransferSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);
        private long _totalBytes;
        private long _bytesTransferred;
        private int _runningCount;
        private int _pausedCount;
        private int _failedCount;
        private int _completedCount;
        private bool _enqueueComplete;
        private readonly bool _incompleteMembership;
        private string? _firstFailureId;
        private string? _firstError;

        public FolderTransferGroup(TransferFolderContext context, int expectedChildCount, TransferTask taskItem,
            bool enqueueComplete = false, bool incompleteMembership = false)
        {
            Context = context;
            ExpectedChildCount = Math.Max(0, expectedChildCount);
            TaskItem = taskItem;
            _enqueueComplete = enqueueComplete;
            _incompleteMembership = incompleteMembership;
        }

        public void ApplySnapshot(ResumableTransferSnapshot snapshot)
        {
            if (Snapshots.TryGetValue(snapshot.Id, out var previous))
            {
                if (snapshot.Revision < previous.Revision) return;
                Adjust(previous, -1);
                if (previous.Status == ResumableTransferStatus.Failed &&
                    (snapshot.Status != ResumableTransferStatus.Failed || snapshot.Id == _firstFailureId))
                {
                    _firstFailureId = null;
                    _firstError = null;
                }
            }

            Snapshots[snapshot.Id] = snapshot;
            ChildTaskIds.Add(snapshot.Id);
            Adjust(snapshot, 1);
            if (snapshot.Status == ResumableTransferStatus.Failed && _firstFailureId is null)
            {
                _firstFailureId = snapshot.Id;
                _firstError = snapshot.Error;
            }
            else if (snapshot.Status == ResumableTransferStatus.Failed && snapshot.Id == _firstFailureId)
            {
                _firstError = snapshot.Error;
            }
            if (_firstFailureId is null && _failedCount > 0)
            {
                var otherFailure = Snapshots.Values.First(item => item.Status == ResumableTransferStatus.Failed);
                _firstFailureId = otherFailure.Id;
                _firstError = otherFailure.Error;
            }

            var allCompleted = _enqueueComplete && ChildTaskIds.Count == ExpectedChildCount &&
                ExpectedChildCount > 0 && _completedCount == ExpectedChildCount;
            var incompleteAndFinished = _incompleteMembership && ChildTaskIds.Count > 0 &&
                _completedCount == ChildTaskIds.Count;
            var state = incompleteAndFinished
                ? TransferState.Failed
                : allCompleted
                ? TransferState.Completed
                : _failedCount > 0
                    ? TransferState.Failed
                    : _runningCount > 0
                        ? TransferState.Running
                        : _pausedCount > 0
                            ? TransferState.Paused
                            : TransferState.Queued;

            TaskItem.UpdateManual(_totalBytes, _bytesTransferred, state,
                _firstError ?? (_incompleteMembership
                    ? "应用关闭时，文件夹中的部分文件尚未进入传输队列；请重新选择文件夹上传遗漏内容。"
                    : null), canRetry: _failedCount > 0);
        }

        public void SealEnqueue()
        {
            _enqueueComplete = true;
            var allCompleted = ChildTaskIds.Count == ExpectedChildCount &&
                _completedCount == ExpectedChildCount;
            var state = allCompleted
                ? TransferState.Completed
                : _failedCount > 0
                    ? TransferState.Failed
                    : _runningCount > 0
                        ? TransferState.Running
                        : _pausedCount > 0
                            ? TransferState.Paused
                            : TransferState.Queued;
            TaskItem.UpdateManual(_totalBytes, _bytesTransferred, state, _firstError,
                canRetry: _failedCount > 0);
        }

        public void SetExpectedChildCount(int count) => ExpectedChildCount = Math.Max(0, count);

        private void Adjust(ResumableTransferSnapshot snapshot, int amount)
        {
            _totalBytes += amount * snapshot.TotalBytes;
            _bytesTransferred += amount * snapshot.BytesTransferred;
            switch (snapshot.Status)
            {
                case ResumableTransferStatus.Running: _runningCount += amount; break;
                case ResumableTransferStatus.Paused: _pausedCount += amount; break;
                case ResumableTransferStatus.Failed: _failedCount += amount; break;
                case ResumableTransferStatus.Completed: _completedCount += amount; break;
            }
        }
    }

    private readonly Dictionary<string, FolderTransferGroup> _folderGroupsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FolderTransferGroup> _taskIdToGroup = new(StringComparer.Ordinal);

    public async Task<(int QueuedCount, string? ErrorMessage)> AddFolderUploadAsync(
        string folderName, string destinationDirectory, IReadOnlyList<(string LocalPath, string DestinationPath)> files)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!_acceptTransferActions)
            {
                return (0, "正在准备安全退出，暂时不能开始新的传输。");
            }

            var estimatedTotalBytes = await Task.Run(() => files.Sum(file =>
            {
                try { return File.Exists(file.LocalPath) ? new FileInfo(file.LocalPath).Length : 0L; }
                catch { return 0L; }
            }));

            var groupId = Guid.NewGuid().ToString("D");
            var context = new TransferFolderContext(groupId, folderName, destinationDirectory, files.Count);
            var folderTask = new TransferTask(groupId, folderName, "上传", estimatedTotalBytes, 0,
                TransferState.Queued, isFolderTransfer: true);
            var group = new FolderTransferGroup(context, files.Count, folderTask);
            _folderGroupsById[groupId] = group;
            Tasks.Insert(0, folderTask);

            Exception? enqueueFailure = null;
            foreach (var (localPath, destPath) in files)
            {
                try
                {
                    var snapshot = await _engine.StartUploadAsync(localPath, destPath, folderContext: context);
                    if (!group.Snapshots.ContainsKey(snapshot.Id))
                        RegisterFolderSnapshot(group, snapshot);
                }
                catch (Exception exception)
                {
                    enqueueFailure = exception;
                    break;
                }
            }

            var wasCompleted = folderTask.State == TransferState.Completed;
            group.SetExpectedChildCount(group.ChildTaskIds.Count);
            if (enqueueFailure is not null)
            {
                try
                {
                    await _engine.SetFolderExpectedChildCountAsync(groupId, group.ChildTaskIds.Count);
                }
                catch
                {
                    enqueueFailure = new IOException("Unable to save the final folder transfer membership.");
                }
            }
            group.SealEnqueue();
            var queuedCount = group.ChildTaskIds.Count;
            if (files.Count > 0 && queuedCount == 0)
            {
                _folderGroupsById.Remove(groupId);
                Tasks.Remove(folderTask);
            }
            if (!wasCompleted && folderTask.State == TransferState.Completed)
                UploadCompleted?.Invoke(destinationDirectory);

            if (enqueueFailure is null)
                return (queuedCount, null);

            var message = enqueueFailure switch
            {
                UnauthorizedAccessException => "文件或目标目录没有访问权限。",
                IOException => "文件暂时无法访问，或 NAS 目标位置不可用。",
                _ => "启动文件夹传输时遇到问题。"
            };
            return (queuedCount, queuedCount > 0
                ? $"{message} 已加入的 {queuedCount} 项会继续传输。"
                : message);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task PauseAsync(TransferTask? task)
    {
        if (task is null) return;
        var group = GetFolderGroup(task);
        if (group is not null)
        {
            var ids = group.Snapshots.Values
                .Where(snapshot => snapshot.Status is ResumableTransferStatus.Running or ResumableTransferStatus.Queued)
                .Select(snapshot => snapshot.Id)
                .ToArray();
            await Task.WhenAll(ids.Select(id => _engine.PauseAsync(id)));
            return;
        }
        await _engine.PauseAsync(task.Id);
    }

    private async Task ResumeAsync(TransferTask? task)
    {
        if (task is null) return;
        var group = GetFolderGroup(task);
        if (group is not null)
        {
            var ids = group.Snapshots.Values
                .Where(snapshot => snapshot.Status == ResumableTransferStatus.Paused)
                .Select(snapshot => snapshot.Id)
                .ToArray();
            foreach (var id in ids)
            {
                var updated = await _engine.ResumeAsync(id);
                group.ApplySnapshot(updated);
            }
            return;
        }
        Upsert(await _engine.ResumeAsync(task.Id));
    }

    private async Task RetryAsync(TransferTask? task)
    {
        if (task is null) return;
        var group = GetFolderGroup(task);
        if (group is not null)
        {
            var ids = group.Snapshots.Values
                .Where(snapshot => snapshot.Status == ResumableTransferStatus.Failed)
                .Select(snapshot => snapshot.Id)
                .ToArray();
            foreach (var id in ids)
            {
                var updated = await _engine.RetryAsync(id);
                group.ApplySnapshot(updated);
            }
            if (ids.Length == 0 && group.HasIncompleteMembership)
                OperationErrorMessage = "部分文件没有进入传输队列；请重新选择该文件夹以补传遗漏内容。";
            return;
        }
        Upsert(await _engine.RetryAsync(task.Id));
    }

    private async Task CancelAsync(TransferTask? task)
    {
        if (task is null) return;
        var group = GetFolderGroup(task);
        if (group is not null)
        {
            foreach (var id in group.ChildTaskIds)
            {
                await _engine.CancelAsync(id);
                _taskIdToGroup.Remove(id);
            }
            _folderGroupsById.Remove(group.GroupId);
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
        FolderTransferGroup? group = null;
        if (IsValidFolderContext(snapshot.FolderContext) &&
            _folderGroupsById.TryGetValue(snapshot.FolderContext!.GroupId, out var groupByContext))
        {
            group = groupByContext;
        }
        else
        {
            _taskIdToGroup.TryGetValue(snapshot.Id, out group);
        }

        if (group is not null)
        {
            var wasCompleted = group.TaskItem.State == TransferState.Completed;
            RegisterFolderSnapshot(group, snapshot);
            if (!wasCompleted && group.TaskItem.State == TransferState.Completed)
                UploadCompleted?.Invoke(group.DestinationDirectory);
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

    private void RegisterFolderSnapshot(FolderTransferGroup group, ResumableTransferSnapshot snapshot)
    {
        _taskIdToGroup[snapshot.Id] = group;
        group.ApplySnapshot(snapshot);
    }

    private FolderTransferGroup? GetFolderGroup(TransferTask task) =>
        _folderGroupsById.TryGetValue(task.Id, out var group) ? group : null;

    private static bool IsValidFolderContext(TransferFolderContext? context) =>
        context is not null && Guid.TryParseExact(context.GroupId, "D", out _) &&
        !string.IsNullOrWhiteSpace(context.FolderName) &&
        !string.IsNullOrWhiteSpace(context.DestinationDirectory);

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

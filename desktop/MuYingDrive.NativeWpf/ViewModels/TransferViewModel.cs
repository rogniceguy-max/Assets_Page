using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>WPF presentation and control surface for the out-of-process Rust transfer agent.</summary>
public sealed class TransferViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaximumBatchItems = 512;
    private readonly V2ApiClient _api;
    private readonly DriveSession _session;
    private readonly NasClientSettings _settings;
    private readonly RustTransferAgentClient _agent;
    private readonly string _clientId;
    private readonly object _snapshotGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, TransferAgentTaskSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, TransferAgentTaskSnapshot>> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferTask> _groupTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<TransferAgentTaskSnapshot>> _transferWaiters = new(StringComparer.Ordinal);
    private string _operationErrorMessage = string.Empty;
    private bool _acceptTransferActions = true;
    private bool _initialized;

    public TransferViewModel(V2ApiClient api, DriveSession session, NasClientSettings settings,
        RustTransferAgentClient agent, string clientId)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        Tasks = [];
        Tasks.CollectionChanged += HandleTasksChanged;
        _agent.EventReceived += HandleAgentEvent;
        PauseCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => PauseAsync(parameter as TransferTask), "暂停传输失败，请稍后重试。"), _ => _acceptTransferActions);
        ResumeCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => ResumeAsync(parameter as TransferTask), "继续传输失败，请稍后重试。"), _ => _acceptTransferActions);
        RetryCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => RetryAsync(parameter as TransferTask), "重试传输失败，请检查文件后重试。"), _ => _acceptTransferActions);
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
    public event Action<string>? UploadCompleted;
    public event Action<TransferAgentTaskSnapshot>? TaskUpdated;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _agent.EnsureConnectedAsync().ConfigureAwait(true);
        await _agent.ConfigureAsync(_settings.ApiBaseUri, _session.SessionToken, _clientId,
            syncRoots: null).ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        _initialized = true;
    }

    public Task ConfigureSyncRootsAsync(IEnumerable<TransferAgentSyncRoot> roots,
        CancellationToken cancellationToken = default) =>
        _agent.ConfigureAsync(_settings.ApiBaseUri, _session.SessionToken, _clientId, roots, cancellationToken);

    public async Task ResumeShutdownPausedSyncTasksAsync(IEnumerable<string> rootIds,
        CancellationToken cancellationToken = default)
    {
        var roots = rootIds.Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length == 0) return;
        _ = await _agent.RequestAsync("resume_sync_roots", new { root_ids = roots }, cancellationToken)
            .ConfigureAwait(false);
        await RefreshTasksAsync().ConfigureAwait(false);
    }

    public Task StopWatchingAsync(CancellationToken cancellationToken = default) =>
        _agent.StopWatchingAsync(cancellationToken);

    public async Task RescanSyncRootAsync(string rootId, CancellationToken cancellationToken = default) =>
        _ = await _agent.RequestAsync("rescan", new { root_id = rootId }, cancellationToken).ConfigureAwait(false);

    public async Task<bool> AddUploadAsync(string localSource, string scopeId, string? projectId,
        string logicalPath, string? fileId = null, long? expectedVersion = null,
        string? displayName = null, CancellationToken cancellationToken = default) =>
        await QueueUploadAsync(localSource, scopeId, projectId, logicalPath, fileId,
            expectedVersion, displayName, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<string?> QueueUploadAsync(string localSource, string scopeId, string? projectId,
        string logicalPath, string? fileId = null, long? expectedVersion = null,
        string? displayName = null, CancellationToken cancellationToken = default)
    {
        if (!_acceptTransferActions) return null;
        try
        {
            var info = new FileInfo(localSource);
            if (!info.Exists) throw new FileNotFoundException("所选文件已不存在。", localSource);
            var operationId = Guid.NewGuid().ToString("D");
            var payload = new
            {
                operation_id = operationId,
                source_path = Path.GetFullPath(localSource),
                scope_id = scopeId,
                project_id = projectId,
                logical_path = LogicalFileScope.Normalize(logicalPath),
                file_id = fileId,
                expected_version = expectedVersion,
                size = info.Length,
                media_type = MediaTypeFor(localSource),
                source = "wpf",
                display_name = displayName ?? Path.GetFileName(localSource)
            };
            _ = await _agent.RequestAsync("enqueue_upload", payload, cancellationToken).ConfigureAwait(false);
            return operationId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or TransferAgentException or TimeoutException)
        {
            OperationErrorMessage = exception.Message;
            return null;
        }
    }

    public async Task<TransferAgentTaskSnapshot> WaitForTransferAsync(string operationId,
        CancellationToken cancellationToken = default)
    {
        if (FindSnapshotByOperationId(operationId) is { } current &&
            current.State == "completed" && !string.IsNullOrWhiteSpace(current.FileId)) return current;
        if (FindSnapshotByOperationId(operationId) is { State: "cancelled" or "canceled" } cancelled)
            throw new OperationCanceledException(cancelled.Message ?? "文件上传已取消。");
        var completion = new TaskCompletionSource<TransferAgentTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_transferWaiters) _transferWaiters[operationId] = completion;
        try
        {
            await RefreshTasksAsync().ConfigureAwait(false);
            if (FindSnapshotByOperationId(operationId) is { } refreshed)
                CompleteTransferWaiter(refreshed);
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
        }
        finally { lock (_transferWaiters) _transferWaiters.Remove(operationId); }
    }

    public async Task<(int QueuedCount, string? ErrorMessage)> AddFolderUploadAsync(
        string folderName, string folderGroup, string scopeId, string? projectId,
        IReadOnlyList<(string LocalPath, string LogicalPath)> files,
        IReadOnlyList<string> directories, CancellationToken cancellationToken = default)
    {
        if (!_acceptTransferActions) return (0, "正在安全退出，暂时不能开始新的传输。");
        var queuedCount = 0;
        try
        {
            if (directories.Count > 4096)
                return (0, "此文件夹包含的目录过多，无法安全加入传输队列。");

            var items = files.Select(file =>
            {
                var info = new FileInfo(file.LocalPath);
                if (!info.Exists) throw new FileNotFoundException("所选文件已不存在。", file.LocalPath);
                return new TransferAgentUploadBatchItem(
                    Guid.NewGuid().ToString("D"),
                    Path.GetFullPath(file.LocalPath),
                    scopeId,
                    projectId,
                    LogicalFileScope.Normalize(file.LogicalPath),
                    info.Length,
                    MediaTypeFor(file.LocalPath),
                    "wpf",
                    Path.GetFileName(file.LocalPath),
                    folderGroup);
            }).ToList();
            var directoryEntries = directories.Select(path => new TransferAgentDirectoryEntry(
                scopeId, projectId, LogicalFileScope.Normalize(path))).ToArray();

            // Each agent batch becomes one backend /file-operations/batch request, which
            // accepts at most 512 items. Measure the full IPC envelope as well: Rust rejects
            // frames larger than 1 MiB, so leave headroom for future fields.
            var requests = new List<TransferAgentEnqueueBatchRequest>();
            var offset = 0;
            while (offset < items.Count)
            {
                var batchId = Guid.NewGuid().ToString("D");
                // Store the directory manifest with the first file batch only. Repeating
                // it for every chunk needlessly inflates IPC/backend requests and can push
                // later chunks over the frame limit even when their files fit.
                IReadOnlyList<TransferAgentDirectoryEntry> batchDirectories = requests.Count == 0
                    ? directoryEntries
                    : Array.Empty<TransferAgentDirectoryEntry>();
                var chunk = new List<TransferAgentUploadBatchItem>(Math.Min(MaximumBatchItems, items.Count - offset));
                while (offset < items.Count && chunk.Count < MaximumBatchItems)
                {
                    chunk.Add(items[offset]);
                    var candidate = new TransferAgentEnqueueBatchRequest(batchId, batchDirectories, chunk);
                    if (RustTransferAgentClient.EstimateRequestFrameBytes("enqueue_batch", candidate) >
                        RustTransferAgentClient.RecommendedBatchFrameBytes)
                    {
                        chunk.RemoveAt(chunk.Count - 1);
                        if (chunk.Count == 0)
                            return (0, "单个文件或目录清单超过传输代理的单帧限制，请缩短路径后重试。");
                        break;
                    }
                    offset++;
                }
                requests.Add(new TransferAgentEnqueueBatchRequest(batchId, batchDirectories, chunk.ToArray()));
            }

            if (items.Count == 0)
            {
                var emptyBatch = new TransferAgentEnqueueBatchRequest(Guid.NewGuid().ToString("D"), directoryEntries, []);
                if (RustTransferAgentClient.EstimateRequestFrameBytes("enqueue_batch", emptyBatch) >
                    RustTransferAgentClient.RecommendedBatchFrameBytes)
                    return (0, "此文件夹的目录清单超过传输代理的单帧限制，请拆分后上传。");
                requests.Add(emptyBatch);
            }

            var errors = new List<string>();
            foreach (var request in requests)
            {
                var response = await _agent.RequestAsync("enqueue_batch", request, cancellationToken)
                    .ConfigureAwait(false);
                if (response.TryGetProperty("items_queued", out var queuedElement))
                    queuedCount += queuedElement.GetInt32();
                else
                    queuedCount += request.Items.Count;
                if (response.TryGetProperty("errors", out var errorArray) && errorArray.ValueKind == JsonValueKind.Array)
                {
                    errors.AddRange(errorArray.EnumerateArray()
                        .Select(item => item.TryGetProperty("message", out var message) ? message.GetString() : null)
                        .Where(message => !string.IsNullOrWhiteSpace(message))
                        .Select(message => message!));
                }
            }
            await RefreshTasksAsync().ConfigureAwait(false);
            return (queuedCount, errors.Count == 0 ? null : string.Join("\n", errors));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or TransferAgentException or TimeoutException)
        {
            OperationErrorMessage = exception.Message;
            var message = queuedCount > 0
                ? $"已加入 {queuedCount} 项；后续批次未能加入。{Environment.NewLine}{exception.Message}"
                : exception.Message;
            return (queuedCount, message);
        }
    }

    public async Task<bool> AddDownloadAsync(FileDownloadTicketResponse ticket, string localDestination,
        string displayName, string? folderGroup = null, CancellationToken cancellationToken = default) =>
        await QueueDownloadAsync(ticket, localDestination, displayName, folderGroup, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<string?> QueueDownloadAsync(FileDownloadTicketResponse ticket, string localDestination,
        string displayName, string? folderGroup = null, CancellationToken cancellationToken = default)
    {
        if (!_acceptTransferActions) return null;
        try
        {
            var operationId = Guid.NewGuid().ToString("D");
            _ = await _agent.RequestAsync("enqueue_download", new
            {
                operation_id = operationId,
                file_id = ticket.FileId,
                revision_id = ticket.RevisionId,
                object_id = ticket.ObjectId,
                destination_path = Path.GetFullPath(localDestination),
                size = ticket.Size,
                sha256 = ticket.Sha256,
                transfer_url = ticket.TransferUrl,
                ticket = ticket.Ticket,
                display_name = displayName,
                folder_group = folderGroup
            }, cancellationToken).ConfigureAwait(false);
            return operationId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or TransferAgentException or TimeoutException)
        {
            OperationErrorMessage = exception.Message;
            return null;
        }
    }

    public async Task<bool> PauseAllAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var active = Tasks.Where(task => task.CanPause).ToArray();
            foreach (var task in active)
                foreach (var id in TaskIds(task))
                    _ = await _agent.RequestAsync("pause_for_shutdown", new { task_id = id }).ConfigureAwait(false);
            await RefreshTasksAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            OperationErrorMessage = exception.Message;
            return false;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<bool> LogoutAgentAsync()
    {
        try
        {
            await _agent.LogoutAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TransferAgentException or TimeoutException)
        {
            OperationErrorMessage = "本地传输代理尚未确认退出登录；服务器会在会话失效后拒绝后续请求。";
            return false;
        }
    }

    public Task StopAgentAsync(CancellationToken cancellationToken = default) =>
        _agent.StopAgentAsync(cancellationToken);

    public async Task ResumeAfterFailedStopAsync(CancellationToken cancellationToken = default)
    {
        _acceptTransferActions = true;
        RaiseTransferCommandStates();
        await _agent.ResumeAfterFailedStopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PrepareForUpdateAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _acceptTransferActions = false;
            RaiseTransferCommandStates();
            var active = Tasks.Where(task => task.State is TransferState.Running or TransferState.Queued).ToArray();
            foreach (var task in active) await SendTaskCommandAsync("pause", task.Id).ConfigureAwait(false);
            await RefreshTasksAsync().ConfigureAwait(false);
            return Tasks.Where(task => active.Any(item => item.Id == task.Id))
                .All(task => task.State is TransferState.Paused or TransferState.Completed);
        }
        catch
        {
            _acceptTransferActions = true;
            RaiseTransferCommandStates();
            return false;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _agent.EventReceived -= HandleAgentEvent;
        Tasks.CollectionChanged -= HandleTasksChanged;
        await _agent.DisposeAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
        _refreshGate.Dispose();
    }

    private async Task PauseAsync(TransferTask? task)
    {
        if (task is null) return;
        foreach (var id in TaskIds(task)) await SendTaskCommandAsync("pause", id).ConfigureAwait(false);
    }

    private async Task ResumeAsync(TransferTask? task)
    {
        if (task is null) return;
        foreach (var id in TaskIds(task)) await SendTaskCommandAsync("resume", id).ConfigureAwait(false);
    }

    private async Task RetryAsync(TransferTask? task)
    {
        if (task is null) return;
        foreach (var id in TaskIds(task)) await SendTaskCommandAsync("resume", id).ConfigureAwait(false);
    }

    private async Task CancelAsync(TransferTask? task)
    {
        if (task is null) return;
        foreach (var id in TaskIds(task)) await SendTaskCommandAsync("cancel", id).ConfigureAwait(false);
    }

    private IEnumerable<string> TaskIds(TransferTask task) =>
        _groups.TryGetValue(task.Id, out var children) ? children.Keys.ToArray() : [task.Id];

    private async Task SendTaskCommandAsync(string command, string taskId)
    {
        _ = await _agent.RequestAsync(command, new { task_id = taskId }).ConfigureAwait(false);
        await RefreshTasksAsync().ConfigureAwait(false);
    }

    private async Task RunLifecycleCommandAsync(Func<Task> action, string fallbackMessage)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_acceptTransferActions) return;
            OperationErrorMessage = string.Empty;
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            OperationErrorMessage = string.IsNullOrWhiteSpace(exception.Message) ? fallbackMessage : exception.Message;
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task RefreshTasksAsync()
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var payload = await _agent.ListTasksAsync().ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<TransferAgentTaskListResponse>(payload.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true })
                ?? new TransferAgentTaskListResponse();
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            if (dispatcher.CheckAccess()) ApplySnapshots(response.Tasks);
            else await dispatcher.InvokeAsync(() => ApplySnapshots(response.Tasks));
        }
        finally { _refreshGate.Release(); }
    }

    private void ApplySnapshots(IEnumerable<TransferAgentTaskSnapshot> snapshots)
    {
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots.OrderByDescending(item => item.UpdatedAt))
        {
            var id = string.IsNullOrWhiteSpace(snapshot.TaskId) ? snapshot.OperationId : snapshot.TaskId;
            if (string.IsNullOrWhiteSpace(id)) continue;
            activeIds.Add(id);
            lock (_snapshotGate) _snapshots[id] = snapshot;
            if (!string.IsNullOrWhiteSpace(snapshot.FolderGroup))
            {
                if (!_groups.TryGetValue(snapshot.FolderGroup, out var group))
                    _groups[snapshot.FolderGroup] = group = new Dictionary<string, TransferAgentTaskSnapshot>(StringComparer.Ordinal);
                group[id] = snapshot;
                UpdateGroup(snapshot.FolderGroup, snapshot.DisplayName);
            }
            else
            {
                Upsert(snapshot);
            }
            TaskUpdated?.Invoke(snapshot);
            CompleteTransferWaiter(snapshot);
        }
    }

    private void CompleteTransferWaiter(TransferAgentTaskSnapshot snapshot)
    {
        lock (_transferWaiters)
        {
            if (!_transferWaiters.TryGetValue(snapshot.OperationId, out var waiter)) return;
            if (snapshot.State is "failed" or "conflict")
                waiter.TrySetException(new InvalidOperationException(snapshot.Message ?? "文件上传失败。"));
            else if (snapshot.State is "cancelled" or "canceled")
                waiter.TrySetException(new OperationCanceledException(snapshot.Message ?? "文件上传已取消。"));
            else if (snapshot.State == "completed" && !string.IsNullOrWhiteSpace(snapshot.FileId))
                waiter.TrySetResult(snapshot);
        }
    }

    private TransferAgentTaskSnapshot? FindSnapshotByOperationId(string operationId)
    {
        lock (_snapshotGate)
            return _snapshots.Values.FirstOrDefault(snapshot => snapshot.OperationId == operationId);
    }

    private void UpdateGroup(string groupId, string displayName)
    {
        if (!_groups.TryGetValue(groupId, out var children) || children.Count == 0) return;
        if (!_groupTasks.TryGetValue(groupId, out var parent))
        {
            parent = new TransferTask(groupId, displayName, "上传", 0, 0, TransferState.Queued, isFolderTransfer: true);
            _groupTasks.Add(groupId, parent);
            Tasks.Insert(0, parent);
        }

        var snapshots = children.Values.ToArray();
        var total = snapshots.Sum(task => task.BytesTotal ?? 0);
        var done = snapshots.Sum(task => task.BytesDone);
        var state = snapshots.All(task => task.State == "completed") ? TransferState.Completed
            : snapshots.Any(task => task.State == "conflict") ? TransferState.Conflict
            : snapshots.Any(task => task.State == "failed") ? TransferState.Failed
            : snapshots.Any(task => task.State is "transferring" or "verifying" or "staged" or "committing") ? TransferState.Running
            : snapshots.All(task => task.State == "paused") ? TransferState.Paused
            : TransferState.Queued;
        var error = snapshots.FirstOrDefault(task => task.State is "failed" or "conflict")?.Message;
        var canRetry = snapshots.Any(task => task.Retryable);
        var wasCompleted = parent.State == TransferState.Completed;
        parent.UpdateManual(total, done, state, error, canRetry);
        if (!wasCompleted && state == TransferState.Completed)
            UploadCompleted?.Invoke(LogicalFileScope.Parent(snapshots[0].LogicalPath ?? string.Empty));
    }

    private void Upsert(TransferAgentTaskSnapshot snapshot)
    {
        var id = string.IsNullOrWhiteSpace(snapshot.TaskId) ? snapshot.OperationId : snapshot.TaskId;
        var task = Tasks.FirstOrDefault(item => item.Id == id);
        if (task is null)
        {
            task = new TransferTask(snapshot);
            Tasks.Insert(0, task);
        }
        else task.Update(snapshot);

        if (snapshot.State == "completed" && snapshot.Kind.Contains("upload", StringComparison.OrdinalIgnoreCase))
            UploadCompleted?.Invoke(LogicalFileScope.Parent(snapshot.LogicalPath ?? string.Empty));
    }

    private void HandleAgentEvent(object? sender, TransferAgentEventArgs args)
    {
        if (args.EventName is "connected" or "scan_warning")
        {
            _ = Task.Run(async () =>
            {
                try { await RefreshTasksAsync().ConfigureAwait(false); }
                catch (Exception exception) { SetOperationError(exception.Message); }
            });
            return;
        }
        if (args.EventName == "task_state")
        {
            _ = Task.Run(async () =>
            {
                try { await RefreshTasksAsync().ConfigureAwait(false); }
                catch (Exception exception) { SetOperationError(exception.Message); }
            });
            return;
        }
        if (args.EventName == "progress" && args.TaskId is { Length: > 0 } id)
        {
            try
            {
                var bytesDone = args.Payload.TryGetProperty("bytes_done", out var done) ? done.GetInt64() : 0;
                var bytesTotal = args.Payload.TryGetProperty("bytes_total", out var total) ? total.GetInt64() : 0;
                var state = args.Payload.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
                TransferAgentTaskSnapshot? previous;
                lock (_snapshotGate) _snapshots.TryGetValue(id, out previous);
                if (previous is not null)
                {
                    var updated = previous with
                    {
                        BytesDone = bytesDone,
                        BytesTotal = bytesTotal > 0 ? bytesTotal : previous.BytesTotal,
                        State = state ?? previous.State,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher is not null)
                    {
                        if (dispatcher.CheckAccess()) ApplySnapshots([updated]);
                        else dispatcher.BeginInvoke(() => ApplySnapshots([updated]));
                    }
                }
                else if (id is not null && _groupTasks.TryGetValue(id, out var groupTask))
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher is not null)
                    {
                        void Update()
                        {
                            var status = state switch { "completed" => TransferState.Completed, "failed" => TransferState.Failed, "paused" => TransferState.Paused, "transferring" => TransferState.Running, _ => TransferState.Queued };
                            groupTask.UpdateManual(bytesTotal, bytesDone, status);
                        }
                        if (dispatcher.CheckAccess()) Update(); else dispatcher.BeginInvoke(Update);
                    }
                }
            }
            catch (Exception) { }
            return;
        }
        if (args.EventName == "error")
        {
            var message = args.Payload.TryGetProperty("message", out var text) ? text.GetString() : null;
            if (!string.IsNullOrWhiteSpace(message)) SetOperationError(message);
        }
    }

    private void SetOperationError(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) OperationErrorMessage = message;
        else dispatcher.BeginInvoke(() => OperationErrorMessage = message);
    }

    private static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        _ => "application/octet-stream"
    };

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

public sealed record TransferAgentDirectoryEntry(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("logical_path")] string LogicalPath);

public sealed record TransferAgentUploadBatchItem(
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("logical_path")] string LogicalPath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("folder_group")] string FolderGroup);

public sealed record TransferAgentEnqueueBatchRequest(
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("directories")] IReadOnlyList<TransferAgentDirectoryEntry> Directories,
    [property: JsonPropertyName("items")] IReadOnlyList<TransferAgentUploadBatchItem> Items);

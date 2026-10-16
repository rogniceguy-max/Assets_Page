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
    private readonly CancellationTokenSource _agentInitializationCancellation = new();
    private readonly SemaphoreSlim _agentInitializationGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, TransferAgentTaskSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, TransferAgentTaskSnapshot>> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferTask> _groupTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TaskCompletionSource<TransferAgentTaskSnapshot>>> _transferWaiters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _updatePausedTaskIds = new(StringComparer.Ordinal);
    private string _operationErrorMessage = string.Empty;
    private bool _acceptTransferActions = true;
    private bool _initialized;
    private bool _agentNeedsReconnect;
    private bool _isAgentInitializationInProgress;
    private bool _agentEventConnectionFaulted;
    private bool _agentEventConnected;
    private bool _disposed;
    private string _updatePreparationErrorMessage = string.Empty;

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
        _agent.ConnectionFaulted += HandleAgentConnectionFaulted;
        PauseCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => PauseAsync(parameter as TransferTask), "暂停传输失败，请稍后重试。"), _ => _acceptTransferActions);
        ResumeCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => ResumeAsync(parameter as TransferTask), "继续传输失败，请稍后重试。"), _ => _acceptTransferActions);
        RetryCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => RetryAsync(parameter as TransferTask), "重试传输失败，请检查文件后重试。"), _ => _acceptTransferActions);
        CancelCommand = new AsyncRelayCommand(parameter => RunLifecycleCommandAsync(
            () => CancelAsync(parameter as TransferTask), "取消传输失败，请稍后重试。"), _ => _acceptTransferActions);
        RetryAgentInitializationCommand = new AsyncRelayCommand(
            _ => InitializeAgentAsync(force: true), _ => CanRetryAgentInitialization);
    }

    public ObservableCollection<TransferTask> Tasks { get; }
    /// <summary>Current durable agent snapshots, copied under the snapshot lock for UI restoration.</summary>
    public IReadOnlyList<TransferAgentTaskSnapshot> TaskSnapshots
    {
        get { lock (_snapshotGate) return _snapshots.Values.OrderByDescending(item => item.UpdatedAt).ToArray(); }
    }
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
    public string UpdatePreparationErrorMessage
    {
        get => _updatePreparationErrorMessage;
        private set => SetProperty(ref _updatePreparationErrorMessage, value);
    }
    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationErrorMessage);
    public bool IsAgentInitializationInProgress
    {
        get => _isAgentInitializationInProgress;
        private set
        {
            if (!SetProperty(ref _isAgentInitializationInProgress, value)) return;
            OnPropertyChanged(nameof(CanRetryAgentInitialization));
            RetryAgentInitializationCommand?.RaiseCanExecuteChanged();
        }
    }
    public bool CanRetryAgentInitialization => _agentNeedsReconnect && !IsAgentInitializationInProgress;
    public AsyncRelayCommand PauseCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand RetryAgentInitializationCommand { get; }
    public event Action<string>? UploadCompleted;
    public event Action<TransferAgentTaskSnapshot>? TaskUpdated;

    public Task InitializeAsync() => InitializeAgentAsync(force: false);

    private async Task InitializeAgentAsync(bool force)
    {
        if (_disposed) return;
        var cancellationToken = _agentInitializationCancellation.Token;
        try { await _agentInitializationGate.WaitAsync(cancellationToken).ConfigureAwait(true); }
        catch (OperationCanceledException) when (_disposed) { return; }
        try
        {
            if (_disposed) return;
            if (_initialized && !force) return;
            IsAgentInitializationInProgress = true;
            SetAgentNeedsReconnect(false);
            OperationErrorMessage = string.Empty;
            try
            {
                await _agent.EnsureConnectedAsync(cancellationToken).ConfigureAwait(true);
                await _agent.ConfigureAsync(_settings.ApiBaseUri, _session.SessionToken, _clientId,
                    syncRoots: null, cancellationToken: cancellationToken).ConfigureAwait(true);
                await RefreshTasksAsync(cancellationToken).ConfigureAwait(true);
                _initialized = true;
                if (_agentEventConnected) HandleAgentConnectionRecovered();
                await RetryRestoreUpdatePausedTasksAfterReconnectAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_disposed)
            {
                _initialized = false;
            }
            catch (Exception exception)
            {
                _initialized = false;
                SetAgentNeedsReconnect(true);
                App.Record(exception, "RustTransferAgent.InitializeAsync");
                OperationErrorMessage = $"本地传输代理初始化失败（{exception.GetType().Name}）：{exception.Message}";
            }
            finally
            {
                IsAgentInitializationInProgress = false;
            }
        }
        finally { _agentInitializationGate.Release(); }
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

    public async Task ResumeShutdownPausedProjectAssetDownloadsAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        _ = await _agent.RequestAsync("resume_project_asset_downloads", new { project_id = projectId }, cancellationToken)
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
        string? displayName = null, CancellationToken cancellationToken = default, string? operationId = null,
        string source = "wpf")
    {
        if (!_acceptTransferActions) return null;
        try
        {
            if (source is not ("wpf" or "sync"))
                throw new InvalidOperationException("上传任务来源必须是 wpf 或 sync。");
            var info = new FileInfo(localSource);
            if (!info.Exists) throw new FileNotFoundException("所选文件已不存在。", localSource);
            var resolvedOperationId = operationId ?? Guid.NewGuid().ToString("D");
            if (!Guid.TryParseExact(resolvedOperationId, "D", out var parsedOperationId) ||
                parsedOperationId.ToString("D") != resolvedOperationId)
                throw new InvalidOperationException("传输操作编号无效。");
            var payload = new
            {
                operation_id = resolvedOperationId,
                source_path = Path.GetFullPath(localSource),
                scope_id = scopeId,
                project_id = projectId,
                logical_path = LogicalFileScope.Normalize(logicalPath),
                file_id = fileId,
                expected_version = expectedVersion,
                size = info.Length,
                media_type = MediaTypeFor(localSource),
                source,
                display_name = displayName ?? Path.GetFileName(localSource)
            };
            _ = await _agent.RequestAsync("enqueue_upload", payload, cancellationToken).ConfigureAwait(false);
            return resolvedOperationId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or TransferAgentException or TimeoutException)
        {
            if (exception is IOException or TimeoutException)
                App.Record(exception, "RustTransferAgent.EnqueueUpload");
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
        lock (_transferWaiters)
        {
            if (!_transferWaiters.TryGetValue(operationId, out var waiters))
                _transferWaiters[operationId] = waiters = [];
            waiters.Add(completion);
        }
        try
        {
            if (FindSnapshotByOperationId(operationId) is { } cached)
                CompleteTransferWaiter(cached);
            if (completion.Task.IsCompleted)
                return await completion.Task.ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var refreshed = await RefreshTaskSnapshotByIdAsync(operationId, cancellationToken).ConfigureAwait(false);
                    CompleteTransferWaiter(refreshed);
                }
                catch (TransferAgentException exception) when (exception.Code == "task_not_found")
                {
                    throw new InvalidOperationException("下载任务已不在本机传输队列中，无法自动打开。请重新双击文件。", exception);
                }
                catch (Exception exception) when (IsAgentIpcFailure(exception))
                {
                    // A lost pipe reply does not undo an enqueue. Keep the waiter alive;
                    // task_state events and the next durable snapshot can still complete it.
                    App.Record(exception, "RustTransferAgent.WaitForTransfer.Refresh");
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("等待传输完成超时，任务仍保留在传输队列中。可在传输页查看当前状态。");
                try
                {
                    return await completion.Task.WaitAsync(
                        remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException) when (DateTimeOffset.UtcNow < deadline) { }
            }
        }
        finally
        {
            lock (_transferWaiters)
            {
                if (_transferWaiters.TryGetValue(operationId, out var waiters))
                {
                    waiters.Remove(completion);
                    if (waiters.Count == 0) _transferWaiters.Remove(operationId);
                }
            }
        }
    }

    private async Task<TransferAgentTaskSnapshot> RefreshTaskSnapshotByIdAsync(string operationId,
        CancellationToken cancellationToken)
    {
        JsonElement payload;
        try { payload = await _agent.GetTaskAsync(operationId, cancellationToken).ConfigureAwait(false); }
        catch (TransferAgentException exception) when (exception.Code == "unknown_command")
        {
            // Support a still-running older agent during an in-place client update.
            await RefreshTasksAsync(cancellationToken).ConfigureAwait(false);
            return FindSnapshotByOperationId(operationId) ??
                throw new InvalidOperationException("当前传输代理版本较旧，无法按编号确认此任务状态。请重启沐影盘后重试。", exception);
        }

        var response = JsonSerializer.Deserialize<TransferAgentTaskResponse>(payload.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
        var snapshot = response?.Task ?? throw new InvalidDataException("传输代理没有返回指定任务的状态。");
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _disposed) return snapshot;
        if (dispatcher.CheckAccess()) ApplySnapshots([snapshot]);
        else await dispatcher.InvokeAsync(() => ApplySnapshots([snapshot])).Task.WaitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<FolderUploadEnqueueResult> AddFolderUploadAsync(
        string folderName, string folderGroup, string scopeId, string? projectId,
        IReadOnlyList<(string LocalPath, string LogicalPath)> files,
        IReadOnlyList<string> directories, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? operationIds = null)
    {
        if (!_acceptTransferActions) return new(0, "正在安全退出，暂时不能开始新的传输。", []);
        var queuedCount = 0;
        var queuedOperationIds = new List<string>();
        try
        {
            if (directories.Count > 4096)
                return new(0, "此文件夹包含的目录过多，无法安全加入传输队列。", []);
            if (operationIds is not null && operationIds.Count != files.Count)
                return new(0, "文件操作编号与清单数量不匹配。", []);

            var items = files.Select((file, index) =>
            {
                var info = new FileInfo(file.LocalPath);
                if (!info.Exists) throw new FileNotFoundException("所选文件已不存在。", file.LocalPath);
                var operationId = operationIds?[index] ?? Guid.NewGuid().ToString("D");
                if (!Guid.TryParseExact(operationId, "D", out _))
                    throw new InvalidOperationException("传输操作编号无效。");
                return new TransferAgentUploadBatchItem(
                    operationId,
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
                            return new(0, "单个文件或目录清单超过传输代理的单帧限制，请缩短路径后重试。", queuedOperationIds);
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
                    return new(0, "此文件夹的目录清单超过传输代理的单帧限制，请拆分后上传。", queuedOperationIds);
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
                queuedOperationIds.AddRange(request.Items.Select(item => item.OperationId));
                if (response.TryGetProperty("errors", out var errorArray) && errorArray.ValueKind == JsonValueKind.Array)
                {
                    errors.AddRange(errorArray.EnumerateArray()
                        .Select(item => item.TryGetProperty("message", out var message) ? message.GetString() : null)
                        .Where(message => !string.IsNullOrWhiteSpace(message))
                        .Select(message => message!));
                }
            }
            await RefreshTasksAsync().ConfigureAwait(false);
            return new(queuedCount, errors.Count == 0 ? null : string.Join("\n", errors), queuedOperationIds);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or TransferAgentException or TimeoutException)
        {
            OperationErrorMessage = exception.Message;
            var message = queuedCount > 0
                ? $"已加入 {queuedCount} 项；后续批次未能加入。{Environment.NewLine}{exception.Message}"
                : exception.Message;
            return new(queuedCount, message, queuedOperationIds);
        }
    }

    public async Task<bool> AddDownloadAsync(FileDownloadTicketResponse ticket, string localDestination,
        string displayName, string? folderGroup = null, CancellationToken cancellationToken = default) =>
        await QueueDownloadAsync(ticket, localDestination, displayName, folderGroup, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<string?> QueueDownloadAsync(FileDownloadTicketResponse ticket, string localDestination,
        string displayName, string? folderGroup = null, CancellationToken cancellationToken = default,
        string? operationId = null, string source = "wpf", string? scopeId = null, string? projectId = null)
    {
        if (!_acceptTransferActions) return null;
        try
        {
            var resolvedOperationId = operationId ?? Guid.NewGuid().ToString("D");
            if (!Guid.TryParseExact(resolvedOperationId, "D", out var parsedOperationId) ||
                parsedOperationId.ToString("D") != resolvedOperationId)
                throw new InvalidOperationException("传输操作编号无效。");
            _ = await _agent.RequestAsync("enqueue_download", new
            {
                operation_id = resolvedOperationId,
                file_id = ticket.FileId,
                revision_id = ticket.RevisionId,
                object_id = ticket.ObjectId,
                source,
                scope_id = scopeId,
                project_id = projectId,
                destination_path = Path.GetFullPath(localDestination),
                size = ticket.Size,
                sha256 = ticket.Sha256,
                transfer_url = ticket.TransferUrl,
                ticket = ticket.Ticket,
                display_name = displayName,
                folder_group = folderGroup
            }, cancellationToken).ConfigureAwait(false);
            return resolvedOperationId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or TransferAgentException or TimeoutException)
        {
            if (exception is IOException or TimeoutException)
                App.Record(exception, "RustTransferAgent.EnqueueDownload");
            OperationErrorMessage = exception.Message;
            return null;
        }
    }

    public Task<bool> RetryOperationAsync(string operationId) =>
        SendOperationCommandAsync(operationId, "resume", requireRetryable: true);

    public void ReportOperationError(string message)
    {
        if (!string.IsNullOrWhiteSpace(message)) SetOperationError(message);
    }

    public Task<bool> ResumeOperationAsync(string operationId) =>
        SendOperationCommandAsync(operationId, "resume", requireRetryable: false);

    public Task<bool> CancelOperationAsync(string operationId) =>
        SendOperationCommandAsync(operationId, "cancel", requireRetryable: false);

    private async Task<bool> SendOperationCommandAsync(string operationId, string command, bool requireRetryable)
    {
        var succeeded = false;
        await RunLifecycleCommandAsync(async () =>
        {
            var snapshot = FindSnapshotByOperationId(operationId);
            if (snapshot is null || (requireRetryable && !snapshot.Retryable) ||
                (!requireRetryable && !TransferTaskStateProjection.CanCancel(snapshot.State, snapshot.CommitStarted)))
                return;
            await SendTaskCommandAsync(command, snapshot.TaskId).ConfigureAwait(false);
            succeeded = true;
        }, requireRetryable ? "重试传输失败，请检查文件后重试。" : "取消传输失败，请稍后重试。").ConfigureAwait(false);
        return succeeded;
    }

    public async Task<bool> PauseAllAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var acquired = false;
        try
        {
            await _lifecycleGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            // Session restore no longer preloads the transfer queue. Snapshot it here before
            // deciding which tasks need a shutdown checkpoint, or queued tasks would be
            // invisible to the normal sign-out/close path.
            await RefreshTasksAsync(timeout.Token).ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) throw new InvalidOperationException("无法访问传输任务界面，未能安全暂停任务。");
            var taskIds = await dispatcher.InvokeAsync(
                () => Tasks.Where(task => task.CanPause)
                    .SelectMany(TaskIds).Distinct(StringComparer.Ordinal).ToArray(),
                System.Windows.Threading.DispatcherPriority.DataBind, timeout.Token).Task
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            foreach (var id in taskIds)
            {
                try
                {
                    _ = await _agent.RequestAsync("pause_for_shutdown", new { task_id = id }, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (TransferAgentException exception) when (exception.Code == "task_state_changed")
                {
                    // The UI snapshot may be stale by the time the durable pause is applied.
                    // A task that completed, was cancelled, or was paused in the meantime is
                    // already safe to leave alone; only report a failure if it remains active.
                    var latest = await RefreshTaskSnapshotByIdAsync(id, timeout.Token).ConfigureAwait(false);
                    if (!IsSafeToStopForUpdate(latest))
                        throw new InvalidOperationException(
                            $"传输任务“{DisplayName(latest)}”在退出前状态发生变化，暂停请求未生效。",
                            exception);
                }
            }
            await RefreshTasksAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            App.Record(exception, "RustTransferAgent.PauseAllAsync");
            SetOperationError(exception.Message);
            return false;
        }
        finally { if (acquired) _lifecycleGate.Release(); }
    }

    public async Task<bool> LogoutAgentAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _agent.LogoutAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            App.Record(exception, "RustTransferAgent.LogoutAsync");
            SetOperationError($"本地传输代理退出登录失败（{exception.GetType().Name}）：{exception.Message}。服务器会在会话失效后拒绝后续请求。");
            return false;
        }
    }

    public async Task StopAgentAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _agent.StopAgentAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            App.Record(exception, "RustTransferAgent.StopAgentAsync");
            SetOperationError($"本地传输代理关闭失败（{exception.GetType().Name}）：{exception.Message}");
            throw;
        }
    }

    public async Task ResumeAfterFailedStopAsync(CancellationToken cancellationToken = default)
    {
        var acquired = false;
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            SetAcceptTransferActions(false);
            await _agent.ResumeAfterFailedStopAsync(cancellationToken).ConfigureAwait(false);

            string[] taskIds;
            lock (_snapshotGate) taskIds = _updatePausedTaskIds.ToArray();
            await RestoreUpdatePausedTasksAsync(taskIds, cancellationToken,
                "RustTransferAgent.ResumeUpdatePausedTask").ConfigureAwait(false);

            if (taskIds.Length > 0)
            {
                try { await RefreshTasksAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    App.Record(exception, "RustTransferAgent.ResumeUpdatePausedTask.Refresh");
                    SetOperationError($"更新取消后的任务状态刷新失败（{exception.GetType().Name}）：{exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            App.Record(exception, "RustTransferAgent.ResumeAfterFailedStop");
            SetOperationError($"更新取消后恢复传输代理失败（{exception.GetType().Name}）：{exception.Message}");
        }
        finally
        {
            if (acquired)
            {
                SetAcceptTransferActions(true);
                _lifecycleGate.Release();
            }
        }
    }

    private async Task RestoreUpdatePausedTasksAsync(IEnumerable<string> taskIds,
        CancellationToken cancellationToken, string recordSource)
    {
        foreach (var taskId in taskIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                // This transition only resumes rows still paused by update preparation;
                // it cannot undo another window's cancel or a user's manual pause.
                _ = await _agent.ResumeShutdownPausedTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
                _ = await RefreshTaskSnapshotByIdAsync(taskId, cancellationToken).ConfigureAwait(false);
                lock (_snapshotGate) _updatePausedTaskIds.Remove(taskId);
            }
            catch (Exception exception)
            {
                App.Record(exception, recordSource);
                SetOperationError($"传输任务“{taskId}”仍未确认恢复（{exception.GetType().Name}）：{exception.Message}。任务标识已保留，可重连代理后重试。");
                lock (_snapshotGate) _updatePausedTaskIds.Add(taskId);
            }
        }
    }

    private async Task RetryRestoreUpdatePausedTasksAfterReconnectAsync(
        CancellationToken cancellationToken = default)
    {
        var acquired = false;
        var actionsDisabled = false;
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            string[] taskIds;
            lock (_snapshotGate) taskIds = _updatePausedTaskIds.ToArray();
            // Update preparation keeps transfer actions disabled until handoff succeeds
            // or rolls back. Do not resume its checkpointed tasks during that window.
            if (_disposed || !_initialized || !_acceptTransferActions || taskIds.Length == 0) return;

            await SetAcceptTransferActionsAsync(false, cancellationToken).ConfigureAwait(false);
            actionsDisabled = true;
            await RestoreUpdatePausedTasksAsync(taskIds, cancellationToken,
                "RustTransferAgent.ResumeUpdatePausedTask.AfterReconnect").ConfigureAwait(false);

            try { await RefreshTasksAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception)
            {
                App.Record(exception, "RustTransferAgent.ResumeUpdatePausedTask.AfterReconnect.Refresh");
                SetOperationError($"代理重连后刷新传输任务失败（{exception.GetType().Name}）：{exception.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            App.Record(exception, "RustTransferAgent.ResumeUpdatePausedTask.AfterReconnect");
            SetOperationError($"代理重连后恢复更新暂停任务失败（{exception.GetType().Name}）：{exception.Message}");
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    if (actionsDisabled)
                        await SetAcceptTransferActionsAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    App.Record(exception, "RustTransferAgent.ResumeUpdatePausedTask.AfterReconnect.EnableActions");
                }
                finally { _lifecycleGate.Release(); }
            }
        }
    }

    private async Task SetAcceptTransferActionsAsync(bool value, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SetAcceptTransferActions(value);
            return;
        }

        await dispatcher.InvokeAsync(() => SetAcceptTransferActions(value),
            System.Windows.Threading.DispatcherPriority.DataBind, cancellationToken).Task.ConfigureAwait(false);
    }

    public async Task<bool> PrepareForUpdateAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        var acquired = false;
        var pauseAttemptedForThisAttempt = new HashSet<string>(StringComparer.Ordinal);
        var pendingSummary = string.Empty;
        UpdatePreparationErrorMessage = string.Empty;
        try
        {
            await _lifecycleGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            SetAcceptTransferActions(false);

            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var liveSnapshots = await RefreshLiveTaskSnapshotsAsync(timeout.Token, forUpdate: true).ConfigureAwait(false);
                var pending = liveSnapshots.Where(snapshot => !IsSafeToStopForUpdate(snapshot)).ToArray();
                if (pending.Length == 0)
                {
                    lock (_snapshotGate)
                        foreach (var taskId in pauseAttemptedForThisAttempt) _updatePausedTaskIds.Add(taskId);
                    return true;
                }

                pendingSummary = DescribePendingUpdateTasks(pending);
                var unpausable = pending.Where(snapshot =>
                    !CanPauseForUpdate(snapshot) && !IsCommitInProgress(snapshot)).ToArray();
                if (unpausable.Length > 0)
                    throw new InvalidOperationException(
                        $"传输代理返回了无法安全暂停的任务状态。{DescribePendingUpdateTasks(unpausable)}");

                var pauseable = pending.Where(CanPauseForUpdate).ToArray();
                foreach (var snapshot in pauseable)
                {
                    var taskId = TaskId(snapshot);
                    pauseAttemptedForThisAttempt.Add(taskId);
                    try
                    {
                        _ = await _agent.RequestAsync("pause_for_shutdown", new { task_id = taskId }, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (TransferAgentException exception) when (exception.Code == "task_state_changed")
                    {
                        // A folder child can finish between list_tasks and pause. Re-read the
                        // durable queue and accept a safe terminal state instead of treating the
                        // expected race as a checkpoint failure.
                        var latest = await RefreshTaskSnapshotByIdAsync(taskId, timeout.Token).ConfigureAwait(false);
                        if (IsSafeToStopForUpdate(latest) || IsCommitInProgress(latest))
                        {
                            pauseAttemptedForThisAttempt.Remove(taskId);
                            continue;
                        }
                        throw new InvalidOperationException(
                            $"传输任务“{DisplayName(snapshot)}”当前为“{TransferTaskStateProjection.Label(latest.State)}”，暂停请求未生效。",
                            exception);
                    }
                }

                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException($"等待传输任务暂停或完成超时。{pendingSummary}");

                // Tasks staged for a commit cannot be paused safely. Let their commit finish,
                // then verify their durable state on the next list_tasks.
                await Task.Delay(150, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && timeout.IsCancellationRequested)
                exception = new TimeoutException($"等待传输任务安全暂停或提交完成超时。{pendingSummary}", exception);
            var reportedException = exception is TransferAgentException { Code: "unknown_command" }
                ? new InvalidOperationException(
                    "当前运行的 Rust 传输代理版本不支持更新安全检查。请完全退出沐影盘并重新打开后再试；仍失败时请先重启电脑，避免旧代理进程继续占用。",
                    exception)
                : exception;
            App.Record(reportedException, "RustTransferAgent.PrepareForUpdate");
            UpdatePreparationErrorMessage = reportedException.Message;
            SetOperationError($"更新准备失败：{reportedException.Message}");

            // The app stays open when preparation fails. Restore tasks paused by this
            // attempt so a failed update check does not leave them unexpectedly stopped.
            using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            lock (_snapshotGate)
                foreach (var taskId in pauseAttemptedForThisAttempt) _updatePausedTaskIds.Add(taskId);
            await RestoreUpdatePausedTasksAsync(pauseAttemptedForThisAttempt, rollbackTimeout.Token,
                "RustTransferAgent.PrepareForUpdate.ResumeAfterFailure").ConfigureAwait(false);

            SetAcceptTransferActions(true);
            return false;
        }
        finally { if (acquired) _lifecycleGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _agentInitializationCancellation.Cancel();
        _agent.EventReceived -= HandleAgentEvent;
        _agent.ConnectionFaulted -= HandleAgentConnectionFaulted;
        Tasks.CollectionChanged -= HandleTasksChanged;
        await _agentInitializationGate.WaitAsync().ConfigureAwait(false);
        try { await _agent.DisposeAsync().ConfigureAwait(false); }
        finally { _agentInitializationGate.Release(); }
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
        var taskIds = task.IsFolderTransfer
            ? TaskSnapshots
                .Where(snapshot => snapshot.FolderGroup == task.Id &&
                    TransferTaskStateProjection.CanCancel(snapshot.State, snapshot.CommitStarted))
                .Select(snapshot => string.IsNullOrWhiteSpace(snapshot.TaskId) ? snapshot.OperationId : snapshot.TaskId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [task.Id];
        var failures = new List<string>();
        foreach (var id in taskIds)
        {
            try { await SendTaskCommandAsync("cancel", id).ConfigureAwait(false); }
            catch (Exception exception)
            {
                failures.Add($"{id}: {exception.Message}");
                if (IsAgentIpcFailure(exception))
                    ReportAgentIpcFailure("取消传输", exception);
            }
        }
        if (failures.Count > 0)
            SetOperationError($"部分传输任务未能确认取消：{string.Join("；", failures)}。可点击“重连代理”刷新状态后重试。");
    }

    private IEnumerable<string> TaskIds(TransferTask task) =>
        _groups.TryGetValue(task.Id, out var children) ? children.Keys.ToArray() : [task.Id];

    private async Task SendTaskCommandAsync(string command, string taskId)
    {
        JsonElement? reply = null;
        try
        {
            reply = await _agent.RequestAsync(command, new { task_id = taskId }).ConfigureAwait(false);
        }
        catch (Exception exception) when (command == "cancel" && IsAgentIpcFailure(exception))
        {
            App.Record(exception, "RustTransferAgent.Cancel.Request");
            await RefreshTasksAsync().ConfigureAwait(false);
            // The server may have applied a cancellation before its pipe reply was
            // lost. Reconcile first, then retry once only if the durable snapshot
            // says the task is active, or to retry remote cleanup for a locally
            // cancelled task whose reply was lost.
            try
            {
                reply = await _agent.RequestAsync(command, new { task_id = taskId }).ConfigureAwait(false);
            }
            catch (Exception retryException) when (IsAgentIpcFailure(retryException))
            {
                App.Record(retryException, "RustTransferAgent.Cancel.Retry");
                try
                {
                    await RefreshTasksAsync().ConfigureAwait(false);
                }
                catch (Exception refreshException)
                {
                    ReportAgentIpcFailure("确认取消状态", refreshException);
                }
                if (IsTaskCancelled(taskId))
                {
                    ReportAgentIpcFailure("确认取消的远端清理结果", retryException);
                    return;
                }
                throw new IOException($"取消请求已发送，但代理未能确认任务状态：{retryException.Message}", retryException);
            }
        }

        try { await RefreshTasksAsync().ConfigureAwait(false); }
        catch (Exception exception) when (command == "cancel" && IsAgentIpcFailure(exception))
        {
            // Keep a successful cancel response successful even if the follow-up
            // snapshot refresh is interrupted. The reconnect action can reconcile it.
            ReportAgentIpcFailure("刷新取消后的任务状态", exception);
        }

        if (command == "cancel" && reply is { } cancelReply &&
            cancelReply.TryGetProperty("remote_cleanup_succeeded", out var cleanupSucceeded) &&
            cleanupSucceeded.ValueKind == JsonValueKind.False &&
            cancelReply.TryGetProperty("remote_cleanup_error", out var cleanupError) &&
            cleanupError.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cleanupError.GetString()))
        {
            SetOperationError($"任务已在本机取消，但服务器清理未完成：{cleanupError.GetString()}");
        }
    }

    private bool IsTaskCancelled(string taskId)
    {
        lock (_snapshotGate)
            return _snapshots.TryGetValue(taskId, out var snapshot) &&
                snapshot.State is "cancelled" or "canceled";
    }

    private static bool IsAgentIpcFailure(Exception exception) =>
        exception is IOException or TimeoutException or ObjectDisposedException;

    private void ReportAgentIpcFailure(string operation, Exception exception)
    {
        App.Record(exception, $"RustTransferAgent.{operation}");
        SetAgentNeedsReconnect(true);
        SetOperationError($"{operation}失败（{exception.GetType().Name}）：{exception.Message}。可点击“重连代理”刷新任务状态。");
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
            SetOperationError(string.IsNullOrWhiteSpace(exception.Message) ? fallbackMessage : exception.Message);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task RefreshTasksAsync(CancellationToken cancellationToken = default)
    {
        _ = await RefreshLiveTaskSnapshotsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TransferAgentTaskSnapshot>> RefreshLiveTaskSnapshotsAsync(
        CancellationToken cancellationToken = default, bool forUpdate = false)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonElement payload;
            try
            {
                payload = forUpdate
                    ? await _agent.ListTasksForUpdateAsync(cancellationToken).ConfigureAwait(false)
                    : await _agent.ListTasksAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsAgentIpcFailure(exception))
            {
                ReportAgentIpcFailure("刷新传输任务", exception);
                throw;
            }
            var response = JsonSerializer.Deserialize<TransferAgentTaskListResponse>(payload.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true })
                ?? new TransferAgentTaskListResponse();
            if (forUpdate && !response.Complete)
                throw new InvalidOperationException(
                    $"当前有 {response.Total} 项活动或提交结果待确认的传输任务，超过安全检查上限；请先等待或处理部分任务，再更新。");
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || _disposed) return response.Tasks;
            if (dispatcher.CheckAccess()) ApplySnapshots(response.Tasks);
            else await dispatcher.InvokeAsync(() => ApplySnapshots(response.Tasks)).Task.WaitAsync(cancellationToken);
            return response.Tasks;
        }
        finally { _refreshGate.Release(); }
    }

    private static string TaskId(TransferAgentTaskSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.TaskId) ? snapshot.OperationId : snapshot.TaskId;

    private static bool IsSafeToStopForUpdate(TransferAgentTaskSnapshot snapshot) =>
        !snapshot.CommitStarted &&
        (string.Equals(snapshot.State, "paused", StringComparison.OrdinalIgnoreCase) ||
         TransferTaskStateProjection.IsTerminal(snapshot.State));

    private static bool IsCommitInProgress(TransferAgentTaskSnapshot snapshot) =>
        snapshot.CommitStarted || snapshot.State is "staged" or "committing";

    private static bool CanPauseForUpdate(TransferAgentTaskSnapshot snapshot) =>
        !snapshot.CommitStarted && snapshot.State is
            "queued" or "hashing" or "waiting_stable" or "transferring" or "verifying" or
            "waiting_network" or "offline";

    private static string DisplayName(TransferAgentTaskSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.DisplayName) ? snapshot.LogicalPath ?? TaskId(snapshot) : snapshot.DisplayName;

    private static string DescribePendingUpdateTasks(IEnumerable<TransferAgentTaskSnapshot> snapshots)
    {
        var pending = snapshots.Take(3)
            .Select(snapshot => $"“{DisplayName(snapshot)}”（{(snapshot.CommitStarted ? "提交结果待确认" : TransferTaskStateProjection.Label(snapshot.State))}）")
            .ToArray();
        return pending.Length == 0 ? string.Empty : $"待处理任务：{string.Join("、", pending)}。";
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
            if (snapshot.FolderGroup is { Length: > 0 } folderGroup &&
                TransferTaskStateProjection.HasFolderGroup(folderGroup))
            {
                if (!_groups.TryGetValue(folderGroup, out var group))
                    _groups[folderGroup] = group = new Dictionary<string, TransferAgentTaskSnapshot>(StringComparer.Ordinal);
                group[id] = snapshot;
                UpdateGroup(folderGroup, snapshot.DisplayName);
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
            if (!_transferWaiters.TryGetValue(snapshot.OperationId, out var waiters)) return;
            if (snapshot.State is "failed" or "conflict")
                foreach (var waiter in waiters)
                    waiter.TrySetException(new InvalidOperationException(snapshot.Message ?? "文件传输失败。"));
            else if (snapshot.State is "cancelled" or "canceled")
                foreach (var waiter in waiters)
                    waiter.TrySetException(new OperationCanceledException(snapshot.Message ?? "文件传输已取消。"));
            else if (snapshot.State == "completed" && !string.IsNullOrWhiteSpace(snapshot.FileId))
                foreach (var waiter in waiters)
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
        var canCancel = snapshots.Any(task => TransferTaskStateProjection.CanCancel(task.State, task.CommitStarted));
        var wasCompleted = parent.State == TransferState.Completed;
        parent.UpdateManual(total, done, state, error, canRetry, canCancel);
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
        if (args.EventName == "connected")
        {
            HandleAgentConnectionRecovered();
            _ = RetryRestoreUpdatePausedTasksAfterReconnectAsync();
        }
        if (args.EventName is "connected" or "scan_warning")
        {
            _ = Task.Run(async () =>
            {
                try { await RefreshTasksAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    if (!IsAgentIpcFailure(exception))
                        SetOperationError($"刷新传输任务失败（{exception.GetType().Name}）：{exception.Message}");
                }
            });
            return;
        }
        if (args.EventName == "task_state")
        {
            _ = Task.Run(async () =>
            {
                try { await RefreshTasksAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    if (!IsAgentIpcFailure(exception))
                        SetOperationError($"刷新传输任务失败（{exception.GetType().Name}）：{exception.Message}");
                }
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

    private void HandleAgentConnectionFaulted(Exception exception)
    {
        App.Record(exception, "RustTransferAgent.EventConnection");
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => HandleAgentConnectionFaulted(exception));
            return;
        }
        _agentEventConnected = false;
        _agentEventConnectionFaulted = true;
        SetAgentNeedsReconnect(true);
        SetOperationError($"传输代理事件连接异常（{exception.GetType().Name}）：{exception.Message}；代理正在自动重连。可点击“重连代理”立即恢复并刷新任务。");
    }

    private void HandleAgentConnectionRecovered()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(HandleAgentConnectionRecovered);
            return;
        }
        _agentEventConnected = true;
        if (!_agentEventConnectionFaulted || !_initialized) return;
        _agentEventConnectionFaulted = false;
        SetAgentNeedsReconnect(false);
        if (OperationErrorMessage.StartsWith("传输代理事件连接异常", StringComparison.Ordinal))
            OperationErrorMessage = string.Empty;
    }

    private void SetAgentNeedsReconnect(bool value)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => SetAgentNeedsReconnect(value));
            return;
        }
        if (_agentNeedsReconnect == value) return;
        _agentNeedsReconnect = value;
        OnPropertyChanged(nameof(CanRetryAgentInitialization));
        RetryAgentInitializationCommand?.RaiseCanExecuteChanged();
    }

    private void SetAcceptTransferActions(bool value)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => SetAcceptTransferActions(value));
            return;
        }
        _acceptTransferActions = value;
        RaiseTransferCommandStates();
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
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".flv" => "video/x-flv",
        ".wmv" => "video/x-ms-wmv",
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

public sealed record FolderUploadEnqueueResult(
    int QueuedCount,
    string? ErrorMessage,
    IReadOnlyList<string> QueuedOperationIds);

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

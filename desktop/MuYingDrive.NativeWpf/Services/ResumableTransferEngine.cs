using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// Account-scoped, durable single-file transfers over normal filesystem paths (UNC paths are
/// supported). Each task writes to an exclusive sibling partial file, checkpoints at chunk
/// boundaries, and publishes with a same-directory no-overwrite rename.
/// </summary>
public sealed class ResumableTransferEngine : IAsyncDisposable
{
    private static readonly TimeSpan CheckpointLifetime = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _accountDirectory;
    private readonly int _chunkSize;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, RuntimeTask> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ResumableTransferSnapshot> _pendingSnapshots = new(StringComparer.Ordinal);
    private readonly SynchronizationContext? _eventContext;
    /// <summary>
    /// Bounds how many transfers run at once, or null to leave it unbounded as it always
    /// was. Cancelled on dispose so a worker waiting here cannot outlive the engine.
    /// </summary>
    private readonly SemaphoreSlim? _workerSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private int _snapshotDispatchScheduled;
    private bool _initialized;
    private bool _disposed;

    /// <param name="maxConcurrentWorkers">
    /// How many transfers may run at once, or null for no limit — the behaviour every
    /// existing caller gets, so downloads and single uploads are untouched. A cap matters
    /// when one action queues many files: without it, dropping a folder of two hundred in
    /// starts two hundred simultaneous SMB streams, which is slower than a handful at a
    /// time and makes the queue impossible to read.
    /// </param>
    public ResumableTransferEngine(string accountId, string? stateRoot = null, int chunkSize = 4 * 1024 * 1024,
        SynchronizationContext? eventContext = null, int? maxConcurrentWorkers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (chunkSize is < 64 * 1024 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be between 64 KiB and 64 MiB.");
        if (maxConcurrentWorkers is < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentWorkers), "至少要允许一个传输同时进行。");

        if (maxConcurrentWorkers is int limit)
            _workerSlots = new SemaphoreSlim(limit, limit);
        _chunkSize = chunkSize;
        _eventContext = eventContext ?? SynchronizationContext.Current;
        var root = Path.GetFullPath(stateRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuYingDrive", "Transfers"));
        var accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId.Normalize(NormalizationForm.FormC))));
        _accountDirectory = Path.Combine(root, accountKey);
    }

    public event EventHandler<ResumableTransferChangedEventArgs>? TransferChanged;

    /// <summary>Loads this account's checkpoints and removes only its expired, owned partials.</summary>
    public async Task<IReadOnlyList<ResumableTransferSnapshot>> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_initialized)
            {
                Directory.CreateDirectory(_accountDirectory);
                await LoadAndExpireAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
            return _tasks.Values.Select(x => x.ToSnapshot()).OrderByDescending(x => x.LastActivityUtc).ToArray();
        }
        finally { _gate.Release(); }
    }

    public Task<ResumableTransferSnapshot> StartUploadAsync(
        string localSourcePath, string destinationPath, CancellationToken cancellationToken = default,
        TransferFolderContext? folderContext = null) =>
        StartAsync(ResumableTransferDirection.Upload, localSourcePath, destinationPath, cancellationToken, folderContext);

    public Task<ResumableTransferSnapshot> StartDownloadAsync(
        string sourcePath, string localDestinationPath, CancellationToken cancellationToken = default) =>
        StartAsync(ResumableTransferDirection.Download, sourcePath, localDestinationPath, cancellationToken, folderContext: null);

    public async Task PauseAsync(string taskId, CancellationToken cancellationToken = default)
    {
        RuntimeTask task;
        bool pausedWhileQueued = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            task = GetTask(taskId);
            lock (task.Sync)
            {
                if (task.Status is not (ResumableTransferStatus.Running or ResumableTransferStatus.Queued)) return;
                task.PauseRequested = true;
                pausedWhileQueued = task.Status == ResumableTransferStatus.Queued;
                if (task.Runner is null || pausedWhileQueued)
                {
                    task.Status = ResumableTransferStatus.Paused;
                    task.LastActivityUtc = DateTimeOffset.UtcNow;
                }
            }
            if (task.Runner is null || pausedWhileQueued)
            {
                await SaveAsync(task, cancellationToken).ConfigureAwait(false);
                Publish(task);
                return;
            }
        }
        finally { _gate.Release(); }

        if (task.Runner is not null)
            await task.Runner.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResumableTransferSnapshot> ResumeAsync(string taskId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            RuntimeTask task;
            Task? finishingRunner = null;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfNotInitialized();
                task = GetTask(taskId);
                lock (task.Sync)
                {
                    if (task.Status is ResumableTransferStatus.Completed or ResumableTransferStatus.Canceled)
                        throw new InvalidOperationException("Completed or canceled transfers cannot be resumed.");
                    if (task.Status == ResumableTransferStatus.Running && !task.PauseRequested)
                        return task.ToSnapshot();
                    if (task.Runner is { IsCompleted: false }) finishingRunner = task.Runner;
                    else
                    {
                        task.Error = null;
                        task.PauseRequested = false;
                        task.Status = ResumableTransferStatus.Running;
                        task.LastActivityUtc = DateTimeOffset.UtcNow;
                    }
                }
                if (finishingRunner is null)
                {
                    await SaveAsync(task, cancellationToken).ConfigureAwait(false);
                    Publish(task);
                    StartWorker(task);
                    return task.ToSnapshot();
                }
            }
            finally { _gate.Release(); }
            await finishingRunner.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<ResumableTransferSnapshot> RetryAsync(string taskId, CancellationToken cancellationToken = default) =>
        ResumeAsync(taskId, cancellationToken);

    /// <summary>Stops a task and removes its checkpoint and its own partial file.</summary>
    public async Task CancelAsync(string taskId, CancellationToken cancellationToken = default)
    {
        RuntimeTask task;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            task = GetTask(taskId);
            bool cancelImmediately;
            lock (task.Sync)
            {
                if (task.Status is ResumableTransferStatus.Completed or ResumableTransferStatus.Canceled) return;
                task.PauseRequested = true;
                task.CancelRequested = true;
                cancelImmediately = task.Runner is null || task.Runner.IsCompleted;
            }
            if (cancelImmediately)
            {
                await CancelAndDeleteAsync(task, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        finally { _gate.Release(); }

        if (task.Runner is not null)
        {
            await task.Runner.WaitAsync(cancellationToken).ConfigureAwait(false);
            // The runner may have crossed its pause checkpoint just before cancellation was
            // requested. Once it has released its file handles, finish cancellation here.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_tasks.ContainsKey(taskId) && task.ToSnapshot().Status is not
                    (ResumableTransferStatus.Completed or ResumableTransferStatus.Canceled))
                    await CancelAndDeleteAsync(task, CancellationToken.None).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
    }

    public IReadOnlyList<ResumableTransferSnapshot> GetTasks() =>
        _tasks.Values.Select(x => x.ToSnapshot()).OrderByDescending(x => x.LastActivityUtc).ToArray();

    public async Task SetFolderExpectedChildCountAsync(string groupId, int expectedChildCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (expectedChildCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedChildCount));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotInitialized();
            var checkpoint = _tasks.Values
                .Where(task => string.Equals(task.FolderContext?.GroupId, groupId, StringComparison.Ordinal))
                .OrderBy(task => task.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (checkpoint is not null)
            {
                lock (checkpoint.Sync)
                {
                    if (checkpoint.FolderContext is { } context)
                        checkpoint.FolderContext = context with { ExpectedChildCount = expectedChildCount };
                }
                await SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private Task<ResumableTransferSnapshot> StartAsync(
        ResumableTransferDirection direction, string sourcePath, string destinationPath,
        CancellationToken cancellationToken, TransferFolderContext? folderContext)
    {
        // StartCoreAsync performs a few synchronous filesystem calls against the SMB path
        // before its first incomplete await. Keep those calls off the WPF dispatcher.
        return Task.Run(() => StartCoreAsync(direction, sourcePath, destinationPath, cancellationToken, folderContext),
            cancellationToken);
    }

    private async Task<ResumableTransferSnapshot> StartCoreAsync(
        ResumableTransferDirection direction, string sourcePath, string destinationPath,
        CancellationToken cancellationToken, TransferFolderContext? folderContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("Transfer source does not exist.", source);
        if (Directory.Exists(destination)) throw new IOException("The destination is a directory.");
        if (File.Exists(destination)) throw new IOException("The destination already exists; overwriting is refused.");

        var taskId = Guid.NewGuid().ToString("N");
        var partial = MakeOwnedPartialPath(destination, taskId);
        var now = DateTimeOffset.UtcNow;
        var task = new RuntimeTask
        {
            Id = taskId,
            Direction = direction,
            SourcePath = source,
            DestinationPath = destination,
            PartialPath = partial,
            FileName = Path.GetFileName(destination),
            TotalBytes = info.Length,
            SourceLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            BytesTransferred = 0,
            FolderContext = folderContext,
            Status = ResumableTransferStatus.Running,
            LastActivityUtc = now
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("The destination already exists; overwriting is refused.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            task.BytesTransferred = 0;
        _tasks.TryAdd(taskId, task);
            await SaveAsync(task, cancellationToken).ConfigureAwait(false);
            Publish(task);
            StartWorker(task);
            return task.ToSnapshot();
        }
        catch
        {
            _tasks.TryRemove(taskId, out _);
            TryDeleteOwnedPartial(partial, destination, taskId);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StartWorker(RuntimeTask task)
    {
        lock (task.Sync)
        {
            if (task.Runner is { IsCompleted: false }) return;
            task.Runner = Task.Run(() => RunTransferAsync(task));
        }
    }

    /// <summary>
    /// Waits for a worker slot when the engine is capped, reporting the wait as queued.
    /// </summary>
    /// <returns>False when the engine is shutting down and this task must not start.</returns>
    /// <remarks>
    /// The task is marked <see cref="ResumableTransferStatus.Queued"/> while it waits and
    /// put back to Running once it has a slot, so the transfer page shows a queue rather
    /// than many rows all claiming to be in progress. Pause is still honoured: the worker
    /// body checks for it at each chunk, so a task paused while queued stops as soon as it
    /// is admitted.
    /// </remarks>
    private async Task<bool> AcquireWorkerSlotAsync(RuntimeTask task)
    {
        if (_workerSlots is null) return true;

        lock (task.Sync)
        {
            if (task.Status == ResumableTransferStatus.Running)
            {
                task.Status = ResumableTransferStatus.Queued;
                task.LastActivityUtc = DateTimeOffset.UtcNow;
            }
        }
        Publish(task);

        try
        {
            await _workerSlots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (_disposed)
        {
            _workerSlots.Release();
            return false;
        }

        var releasePausedSlot = false;
        lock (task.Sync)
        {
            if (task.Status == ResumableTransferStatus.Queued && !task.PauseRequested)
            {
                task.Status = ResumableTransferStatus.Running;
                task.LastActivityUtc = DateTimeOffset.UtcNow;
            }
            else if (task.Status == ResumableTransferStatus.Paused || task.PauseRequested)
            {
                task.Status = ResumableTransferStatus.Paused;
                task.LastActivityUtc = DateTimeOffset.UtcNow;
                releasePausedSlot = true;
            }
        }
        if (releasePausedSlot)
        {
            _workerSlots.Release();
            await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
            Publish(task);
            return false;
        }
        Publish(task);
        return true;
    }

    private async Task RunTransferAsync(RuntimeTask task)
    {
        if (!await AcquireWorkerSlotAsync(task).ConfigureAwait(false)) return;
        FileStream? source = null;
        FileStream? partial = null;
        try
        {
            ValidateSource(task);
            var destinationExists = File.Exists(task.DestinationPath) || Directory.Exists(task.DestinationPath);
            var partialExists = File.Exists(task.PartialPath);
            if (destinationExists && !partialExists)
            {
                await RecoverPublishedDestinationAsync(task).ConfigureAwait(false);
                return;
            }
            if (destinationExists) throw new IOException("The destination already exists; overwriting is refused.");
            var partialInfo = new FileInfo(task.PartialPath);
            if (!partialInfo.Exists) throw new IOException("The saved partial file is missing; this transfer cannot resume.");
            if (partialInfo.Length > task.TotalBytes) throw new IOException("The partial file is larger than its recorded source.");
            bool repairCheckpoint;
            long checkpointedBytes;
            lock (task.Sync)
            {
                checkpointedBytes = task.BytesTransferred;
            }
            var recoveredLength = partialInfo.Length;
            if (recoveredLength > checkpointedBytes)
            {
                // Bytes written after the last atomically committed journal record are
                // deliberately discarded. The journal is the resume boundary; this avoids
                // trusting a torn or externally modified tail after an unclean shutdown.
                await using var repairStream = new FileStream(task.PartialPath, FileMode.Open, FileAccess.Write,
                    FileShare.None, 1, FileOptions.Asynchronous);
                repairStream.SetLength(checkpointedBytes);
                await repairStream.FlushAsync().ConfigureAwait(false);
                repairStream.Flush(flushToDisk: true);
                recoveredLength = checkpointedBytes;
            }
            lock (task.Sync)
            {
                repairCheckpoint = task.BytesTransferred != recoveredLength;
                task.BytesTransferred = recoveredLength;
                if (repairCheckpoint) task.LastActivityUtc = DateTimeOffset.UtcNow;
            }
            // The data flush can reach disk before the checkpoint rename. Reconcile that crash
            // window to the actual partial length before another chunk is attempted.
            if (repairCheckpoint) await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
            Publish(task);

            source = new FileStream(task.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, _chunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            partial = new FileStream(task.PartialPath, FileMode.Open, FileAccess.Write, FileShare.None, _chunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            source.Seek(task.BytesTransferred, SeekOrigin.Begin);
            partial.Seek(0, SeekOrigin.End);
            var buffer = new byte[_chunkSize];

            while (GetBytesTransferred(task) < task.TotalBytes)
            {
                if (task.CancelRequested) throw new OperationCanceledException("Transfer canceled.");
                if (task.PauseRequested) { await MarkPausedAsync(task).ConfigureAwait(false); return; }
                var wanted = (int)Math.Min(buffer.Length, task.TotalBytes - GetBytesTransferred(task));
                var read = await source.ReadAsync(buffer.AsMemory(0, wanted)).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The source ended before the recorded file size.");
                await partial.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                await partial.FlushAsync().ConfigureAwait(false);
                partial.Flush(flushToDisk: true);
                lock (task.Sync)
                {
                    task.BytesTransferred += read;
                    task.LastActivityUtc = DateTimeOffset.UtcNow;
                }
                ValidateSource(task);
                await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
                Publish(task);
            }

            if (task.CancelRequested) throw new OperationCanceledException("Transfer canceled.");
            if (task.PauseRequested) { await MarkPausedAsync(task).ConfigureAwait(false); return; }
            ValidateSource(task);
            await partial.DisposeAsync().ConfigureAwait(false);
            partial = null;
            await source.DisposeAsync().ConfigureAwait(false);
            source = null;

            var partialHash = await HashFileAsync(task.PartialPath).ConfigureAwait(false);
            var sourceHash = await HashFileAsync(task.SourcePath).ConfigureAwait(false);
            ValidateSource(task);
            if (!CryptographicOperations.FixedTimeEquals(partialHash, sourceHash))
                throw new IOException("Integrity verification failed; the destination was not published.");

            var published = false;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                lock (task.Sync)
                {
                    // This lock is the cancellation/publication boundary. If cancellation wins,
                    // cleanup removes the partial; if publication wins, the final file is kept.
                    if (task.CancelRequested) throw new OperationCanceledException("Transfer canceled before publication.");
                    if (File.Exists(task.DestinationPath) || Directory.Exists(task.DestinationPath))
                        throw new IOException("The destination already exists; overwriting is refused.");

                    try
                    {
                        File.Move(task.PartialPath, task.DestinationPath, overwrite: false);
                        task.Status = ResumableTransferStatus.Completed;
                        task.Error = null;
                        task.BytesTransferred = task.TotalBytes;
                        task.LastActivityUtc = DateTimeOffset.UtcNow;
                        published = true;
                        break;
                    }
                    catch (Exception ex) when (attempt < 5 && (ex is IOException or UnauthorizedAccessException))
                    {
                        // Transient SMB oplock / Defender scanning locks
                    }
                }

                if (!published)
                {
                    await Task.Delay(150).ConfigureAwait(false);
                }
            }

            await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
            Publish(task);
        }
        catch (Exception ex)
        {
            if (partial != null)
            {
                try { await partial.DisposeAsync().ConfigureAwait(false); } catch { }
                partial = null;
            }
            if (source != null)
            {
                try { await source.DisposeAsync().ConfigureAwait(false); } catch { }
                source = null;
            }

            if (task.CancelRequested)
            {
                await CancelAndDeleteAsync(task, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            lock (task.Sync)
            {
                task.Status = ResumableTransferStatus.Failed;
                task.Error = ex.Message;
                task.LastActivityUtc = DateTimeOffset.UtcNow;
            }
            try { await SaveAsync(task, CancellationToken.None).ConfigureAwait(false); } catch { }
            Publish(task);
        }
        finally
        {
            if (partial != null)
            {
                try { await partial.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (source != null)
            {
                try { await source.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            // Released whatever happened — completed, failed, paused or cancelled. A slot
            // held past the worker's death would quietly shrink the queue's capacity for
            // the rest of the session.
            _workerSlots?.Release();
        }
    }

    private async Task RecoverPublishedDestinationAsync(RuntimeTask task)
    {
        var destinationInfo = new FileInfo(task.DestinationPath);
        destinationInfo.Refresh();
        if (!destinationInfo.Exists || destinationInfo.Length != task.TotalBytes)
            throw new IOException("A destination exists but does not match the recorded transfer; it was left untouched.");

        var destinationHash = await HashFileAsync(task.DestinationPath).ConfigureAwait(false);
        var sourceHash = await HashFileAsync(task.SourcePath).ConfigureAwait(false);
        ValidateSource(task);
        if (!CryptographicOperations.FixedTimeEquals(destinationHash, sourceHash))
            throw new IOException("A destination exists but failed integrity verification; it was left untouched.");

        lock (task.Sync)
        {
            task.Status = ResumableTransferStatus.Completed;
            task.Error = null;
            task.BytesTransferred = task.TotalBytes;
            task.LastActivityUtc = DateTimeOffset.UtcNow;
        }
        await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
        Publish(task);
    }

    private async Task MarkPausedAsync(RuntimeTask task)
    {
        lock (task.Sync)
        {
            task.Status = ResumableTransferStatus.Paused;
            task.LastActivityUtc = DateTimeOffset.UtcNow;
        }
        await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
        Publish(task);
    }

    private void ValidateSource(RuntimeTask task)
    {
        var info = new FileInfo(task.SourcePath);
        info.Refresh();
        if (!info.Exists || info.Length != task.TotalBytes || info.LastWriteTimeUtc.Ticks != task.SourceLastWriteUtcTicks)
            throw new IOException("The source changed since this transfer was created; retry is blocked to protect data integrity.");
    }

    private async Task<byte[]> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, _chunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream).ConfigureAwait(false);
    }

    private async Task LoadAndExpireAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var checkpointPath in Directory.EnumerateFiles(_accountDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeTask? task;
            try
            {
                await using var stream = File.OpenRead(checkpointPath);
                task = await JsonSerializer.DeserializeAsync<RuntimeTask>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch { continue; }
            if (task is null || !Guid.TryParseExact(task.Id, "N", out _) ||
                !Enum.IsDefined(task.Direction) || !Enum.IsDefined(task.Status) || task.TotalBytes < 0 ||
                task.BytesTransferred < 0 || task.BytesTransferred > task.TotalBytes) continue;
            if (!IsCheckpointPath(task, checkpointPath) || !IsOwnedPartialPath(task.PartialPath, task.DestinationPath, task.Id)) continue;
            if (now - task.LastActivityUtc > CheckpointLifetime)
            {
                if (task.Status != ResumableTransferStatus.Completed && !TryDeleteOwnedPartial(task.PartialPath, task.DestinationPath, task.Id))
                    continue;
                TryDeleteCheckpoint(checkpointPath);
                continue;
            }
            if (task.Status is ResumableTransferStatus.Running or ResumableTransferStatus.Queued)
            {
                task.Status = ResumableTransferStatus.Paused;
                task.Error = null;
                task.LastActivityUtc = now;
                await SaveAsync(task, cancellationToken).ConfigureAwait(false);
            }
            _tasks[task.Id] = task;
        }
    }

    private async Task SaveAsync(RuntimeTask task, CancellationToken cancellationToken)
    {
        await task.PersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_accountDirectory);
            var path = CheckpointPath(task.Id);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    RuntimeTask checkpoint;
                    lock (task.Sync) checkpoint = task.CloneForPersistence();
                    await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
            }
            finally { TryDeleteFile(temp); }
        }
        finally { task.PersistenceGate.Release(); }
    }

    private async Task CancelAndDeleteAsync(RuntimeTask task, CancellationToken cancellationToken)
    {
        if (File.Exists(task.DestinationPath) || Directory.Exists(task.DestinationPath))
        {
            lock (task.Sync)
            {
                task.Status = ResumableTransferStatus.Failed;
                task.Error = "A destination now exists; cancellation cleanup was skipped to protect it.";
                task.LastActivityUtc = DateTimeOffset.UtcNow;
            }
            await SaveAsync(task, cancellationToken).ConfigureAwait(false);
            Publish(task);
            return;
        }

        if (TryDeleteOwnedPartial(task.PartialPath, task.DestinationPath, task.Id))
        {
            lock (task.Sync)
            {
                task.Status = ResumableTransferStatus.Canceled;
                task.Error = null;
                task.LastActivityUtc = DateTimeOffset.UtcNow;
            }
            _tasks.TryRemove(task.Id, out _);
            TryDeleteCheckpoint(CheckpointPath(task.Id));
            Publish(task);
        }
        else
        {
            lock (task.Sync)
            {
                task.Status = ResumableTransferStatus.Failed;
                task.Error = "Could not remove this task's partial file; it was left in place.";
                task.LastActivityUtc = DateTimeOffset.UtcNow;
            }
            await SaveAsync(task, cancellationToken).ConfigureAwait(false);
            Publish(task);
        }
    }

    private RuntimeTask GetTask(string id) => _tasks.TryGetValue(id, out var task)
        ? task
        : throw new KeyNotFoundException("Transfer task was not found for the active account.");

    private static long GetBytesTransferred(RuntimeTask task)
    {
        lock (task.Sync) return task.BytesTransferred;
    }

    private string CheckpointPath(string id) => Path.Combine(_accountDirectory, id + ".json");

    private static string MakeOwnedPartialPath(string destination, string id)
    {
        var full = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(full)!;
        return Path.Combine(directory, $".myv-{id}.part");
    }

    private static bool IsOwnedPartialPath(string partial, string destination, string id)
    {
        try
        {
            return string.Equals(Path.GetFullPath(partial), MakeOwnedPartialPath(destination, id), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private bool IsCheckpointPath(RuntimeTask task, string path) =>
        string.Equals(Path.GetFullPath(path), CheckpointPath(task.Id), StringComparison.OrdinalIgnoreCase);

    private static bool TryDeleteOwnedPartial(string partial, string destination, string id)
    {
        if (!IsOwnedPartialPath(partial, destination, id)) return false;
        try
        {
            if (File.Exists(partial)) File.Delete(partial);
            return !File.Exists(partial);
        }
        catch { return false; }
    }

    private static void TryDeleteCheckpoint(string path) => TryDeleteFile(path);

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Publish(RuntimeTask task)
    {
        try
        {
            lock (task.Sync) task.EventRevision++;
            var snapshot = task.ToSnapshot();
            if (_eventContext is null || ReferenceEquals(SynchronizationContext.Current, _eventContext))
            {
                NotifyTransferChanged(snapshot);
                return;
            }

            // A busy folder can produce many progress snapshots before the UI dispatcher
            // gets a turn. Keep only the newest snapshot per file and post one drain callback
            // instead of queuing an unbounded number of dispatcher operations.
            _pendingSnapshots[snapshot.Id] = snapshot;
            ScheduleSnapshotDispatch();
        }
        catch { /* Subscriber errors must not break disk transfer. */ }
    }

    private void ScheduleSnapshotDispatch()
    {
        if (Interlocked.CompareExchange(ref _snapshotDispatchScheduled, 1, 0) != 0) return;
        try
        {
            _eventContext!.Post(_ => DrainPendingSnapshots(), null);
        }
        catch
        {
            Interlocked.Exchange(ref _snapshotDispatchScheduled, 0);
        }
    }

    private void DrainPendingSnapshots()
    {
        const int maxSnapshotsPerDispatch = 256;
        var processed = 0;
        foreach (var id in _pendingSnapshots.Keys)
        {
            if (processed >= maxSnapshotsPerDispatch) break;
            if (_pendingSnapshots.TryRemove(id, out var snapshot))
            {
                NotifyTransferChanged(snapshot);
                processed++;
            }
        }

        Interlocked.Exchange(ref _snapshotDispatchScheduled, 0);
        if (!_pendingSnapshots.IsEmpty) ScheduleSnapshotDispatch();
    }

    private void NotifyTransferChanged(ResumableTransferSnapshot snapshot)
    {
        try { TransferChanged?.Invoke(this, new ResumableTransferChangedEventArgs(snapshot)); }
        catch { /* A UI subscriber must not crash the transfer worker or dispatcher. */ }
    }

    private void ThrowIfNotInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized) throw new InvalidOperationException("Call InitializeAsync before using the transfer engine.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ResumableTransferEngine));
    }

    public async ValueTask DisposeAsync()
    {
        Task[] runners;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            // Released first, and before the runners are awaited: a worker parked in
            // AcquireWorkerSlotAsync is not paused by PauseRequested — it is waiting on a
            // semaphore — so without this it would sit there until a slot happened to free,
            // and Task.WhenAll below would wait for it. That is a shutdown deadlock.
            _shutdown.Cancel();
            foreach (var task in _tasks.Values)
            {
                lock (task.Sync) task.PauseRequested = true;
            }
            runners = _tasks.Values.Select(x => x.Runner).Where(x => x is not null).Cast<Task>().ToArray();
        }
        finally { _gate.Release(); }
        try { await Task.WhenAll(runners).ConfigureAwait(false); } catch { }
        _shutdown.Dispose();
    }

    private sealed class RuntimeTask
    {
        [System.Text.Json.Serialization.JsonIgnore] public object Sync { get; } = new();
        [System.Text.Json.Serialization.JsonIgnore] public SemaphoreSlim PersistenceGate { get; } = new(1, 1);
        public string Id { get; set; } = "";
        public ResumableTransferDirection Direction { get; set; }
        public string SourcePath { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public string PartialPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public long TotalBytes { get; set; }
        public long SourceLastWriteUtcTicks { get; set; }
        public long BytesTransferred { get; set; }
        public TransferFolderContext? FolderContext { get; set; }
        public ResumableTransferStatus Status { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset LastActivityUtc { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public long EventRevision { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public volatile bool PauseRequested;
        [System.Text.Json.Serialization.JsonIgnore] public volatile bool CancelRequested;
        [System.Text.Json.Serialization.JsonIgnore] public Task? Runner { get; set; }

        public RuntimeTask CloneForPersistence() => new()
        {
            Id = Id, Direction = Direction, SourcePath = SourcePath, DestinationPath = DestinationPath,
            PartialPath = PartialPath, FileName = FileName, TotalBytes = TotalBytes,
            SourceLastWriteUtcTicks = SourceLastWriteUtcTicks, BytesTransferred = BytesTransferred,
            FolderContext = FolderContext,
            Status = Status, Error = Error, LastActivityUtc = LastActivityUtc
        };

        public ResumableTransferSnapshot ToSnapshot()
        {
            lock (Sync)
                return new(Id, FileName, Direction, Status, BytesTransferred, TotalBytes,
                    Path.GetDirectoryName(Direction == ResumableTransferDirection.Upload ? DestinationPath : SourcePath) ?? "",
                    TotalBytes == 0 ? 100 : Math.Clamp(BytesTransferred * 100d / TotalBytes, 0, 100),
                    Error, LastActivityUtc, FolderContext, EventRevision);
        }
    }
}

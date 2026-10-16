using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuYingDrive.NativeWpf.Services;

public sealed class TransferAgentEventArgs(string eventName, long sequence, string? taskId,
    string? operationId, JsonElement payload) : EventArgs
{
    public string EventName { get; } = eventName;
    public long Sequence { get; } = sequence;
    public string? TaskId { get; } = taskId;
    public string? OperationId { get; } = operationId;
    public JsonElement Payload { get; } = payload;
}

public sealed record TransferAgentReply(JsonElement Payload);

/// <summary>Async v1 named-pipe client for the per-user Rust transfer agent.</summary>
public sealed class RustTransferAgentClient : IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    public const int MaximumFrameBytes = 1024 * 1024;
    public const int RecommendedBatchFrameBytes = 900 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly string _pipePath;
    private readonly string _agentPath;
    private readonly string _stateNamespace;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _subscriptionCancellation;
    private Task? _subscriptionTask;
    private Process? _process;
    private volatile bool _disposed;
    private volatile bool _stopRequested;

    public RustTransferAgentClient(string stateNamespace, string? agentPath = null, string? userSid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateNamespace);
        if (stateNamespace.Length > 128 || stateNamespace.Any(char.IsControl))
            throw new ArgumentException("传输代理状态命名空间无效。", nameof(stateNamespace));
        _stateNamespace = stateNamespace;
        _agentPath = Path.GetFullPath(agentPath ?? Path.Combine(AppContext.BaseDirectory,
            "transfer", "MuYingDrive.TransferAgent.exe"));
        var sid = userSid ?? WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法确定当前 Windows 用户身份。");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "\0" + stateNamespace)))
            .ToLowerInvariant()[..24];
        _pipeName = $"MuYingDrive.TransferAgent.v1.{hash}";
        _pipePath = $@"\\.\pipe\{_pipeName}";
    }

    public event EventHandler<TransferAgentEventArgs>? EventReceived;
    public event Action<Exception>? ConnectionFaulted;

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureServerAvailableAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_stopRequested) throw new InvalidOperationException("Rust 传输代理正在关闭。");
            if (_subscriptionTask is null || _subscriptionTask.IsCompleted)
            {
                _subscriptionCancellation?.Dispose();
                _subscriptionCancellation = new CancellationTokenSource();
                var subscriptionToken = _subscriptionCancellation.Token;
                _subscriptionTask = Task.Run(() => SubscribeEventsLoopAsync(subscriptionToken));
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<JsonElement> RequestAsync(string command, object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (_stopRequested) throw new InvalidOperationException("Rust 传输代理正在关闭。");
        return await RequestOnNewConnectionAsync(command, payload, cancellationToken).ConfigureAwait(false);
    }

    public static int EstimateRequestFrameBytes(string command, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(payload);
        var envelope = new
        {
            v = ProtocolVersion,
            id = "00000000-0000-0000-0000-000000000000",
            command,
            payload
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions).Length;
    }

    public async Task ConfigureAsync(Uri apiBaseUri, string bearerToken, string clientId,
        IEnumerable<TransferAgentSyncRoot>? syncRoots, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["api_base_url"] = apiBaseUri.AbsoluteUri.TrimEnd('/'),
            ["bearer_token"] = bearerToken,
            ["client_id"] = clientId
        };
        if (syncRoots is not null) payload["sync_roots"] = syncRoots.ToArray();
        _ = await RequestAsync("configure", payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> ListTasksAsync(CancellationToken cancellationToken = default) =>
        await RequestAsync("list_tasks", new { }, cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement> ListTasksForUpdateAsync(CancellationToken cancellationToken = default) =>
        await RequestAsync("list_tasks_for_update", new { }, cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement> GetTaskAsync(string operationId, CancellationToken cancellationToken = default) =>
        await RequestAsync("get_task", new { task_id = operationId }, cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement> ResumeShutdownPausedTaskAsync(string operationId, CancellationToken cancellationToken = default) =>
        await RequestAsync("resume_shutdown_paused", new { task_id = operationId }, cancellationToken).ConfigureAwait(false);

    public async Task StopWatchingAsync(CancellationToken cancellationToken = default) =>
        _ = await RequestAsync("stop_watching", new { }, cancellationToken).ConfigureAwait(false);

    public async Task LogoutAsync(CancellationToken cancellationToken = default) =>
        _ = await RequestAsync("logout", new { }, cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var cancellation = _subscriptionCancellation;
        var subscription = _subscriptionTask;
        cancellation?.Cancel();
        var subscriptionStopped = subscription is null;
        if (subscription is not null)
        {
            try
            {
                await subscription.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                subscriptionStopped = true;
            }
            catch (TimeoutException)
            {
                // Cancellation should release the pipe read. If it does not, closing the
                // client window must still finish; the continuation releases the CTS later.
                _ = subscription.ContinueWith(_ => cancellation?.Dispose(),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch { subscriptionStopped = true; }
        }
        if (subscriptionStopped) cancellation?.Dispose();
        _subscriptionCancellation = null;
        _subscriptionTask = null;
        _process?.Dispose();
        _process = null;
    }

    public async Task StopAgentAsync(CancellationToken cancellationToken = default)
    {
        _stopRequested = true;
        _subscriptionCancellation?.Cancel();
        Exception? eventStopFailure = null;
        if (_subscriptionTask is not null)
        {
            try { await _subscriptionTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (TimeoutException exception)
            {
                eventStopFailure = exception;
            }
            catch (Exception exception) { eventStopFailure = exception; }
        }

        // Serialize stop against startup. Otherwise the pipe probe can race with the
        // process launch and skip shutdown while a newly started agent is still coming up.
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var serverWasAvailable = await IsPipeAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (!serverWasAvailable && _process is { HasExited: false })
            {
                var readinessDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (!serverWasAvailable && DateTime.UtcNow < readinessDeadline)
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    serverWasAvailable = await IsPipeAvailableAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var shutdownAcknowledged = false;
            Exception? shutdownFailure = null;
            var shutdownCompleted = false;
            if (serverWasAvailable)
            {
                for (var attempt = 0; attempt < 2 && !shutdownAcknowledged; attempt++)
                {
                    try
                    {
                        var reply = await RequestOnNewConnectionAsync("shutdown", new { }, cancellationToken)
                            .ConfigureAwait(false);
                        shutdownAcknowledged = reply.ValueKind == JsonValueKind.Object &&
                            reply.TryGetProperty("stopping", out var stopping) && stopping.ValueKind == JsonValueKind.True;
                        if (!shutdownAcknowledged)
                            shutdownFailure = new InvalidDataException("Rust 传输代理的关闭响应缺少 stopping=true。");
                    }
                    catch (Exception exception) when (exception is IOException or TimeoutException or
                        ObjectDisposedException or InvalidDataException)
                    {
                        shutdownFailure = exception;
                    }

                    if (!shutdownAcknowledged && attempt == 0 &&
                        await IsPipeAvailableAsync(cancellationToken).ConfigureAwait(false))
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }

            if (_process is { } process)
            {
                try
                {
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                        .ConfigureAwait(false);
                    shutdownCompleted = true;
                    if (serverWasAvailable)
                        await WaitForPipeUnavailableAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    if (process.HasExited)
                    {
                        shutdownCompleted = true;
                        if (serverWasAvailable)
                            await WaitForPipeUnavailableAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var details = new List<string>();
                        if (shutdownAcknowledged) details.Add("代理已确认收到关闭请求");
                        else if (shutdownFailure is not null)
                            details.Add($"关闭请求异常：{shutdownFailure.GetType().Name}: {shutdownFailure.Message}");
                        else details.Add("未收到有效的关闭确认");
                        if (eventStopFailure is not null)
                            details.Add($"事件通道关闭异常：{eventStopFailure.GetType().Name}: {eventStopFailure.Message}");
                        throw new IOException(
                            $"Rust 传输代理进程（PID {process.Id}）在 10 秒内未退出。{string.Join("；", details)}。未强制结束进程。",
                            shutdownFailure ?? eventStopFailure ?? new TimeoutException("等待传输代理进程退出超时。"));
                    }
                }
            }
            else if (serverWasAvailable)
            {
                await WaitForPipeUnavailableAsync(cancellationToken).ConfigureAwait(false);
                shutdownCompleted = true;
            }

            if (shutdownCompleted && (eventStopFailure is not null || (!shutdownAcknowledged && shutdownFailure is not null)))
            {
                var details = new List<string>();
                if (shutdownFailure is not null)
                    details.Add($"关闭响应异常：{shutdownFailure.GetType().Name}: {shutdownFailure.Message}");
                if (eventStopFailure is not null)
                    details.Add($"事件通道关闭异常：{eventStopFailure.GetType().Name}: {eventStopFailure.Message}");
                App.Record(new IOException(
                    $"Rust 传输代理已停止，但关闭期间出现 IPC 异常：{string.Join("；", details)}。{(shutdownAcknowledged ? "收到有效关闭确认。" : "本次按进程退出结果确认关闭。")}",
                    shutdownFailure ?? eventStopFailure), "RustTransferAgent.StopAgent.ClosedWithIpcWarning");
            }

            if (eventStopFailure is not null && serverWasAvailable && !shutdownAcknowledged && !shutdownCompleted)
            {
                throw new IOException(
                    $"Rust 传输代理事件连接关闭异常（{eventStopFailure.GetType().Name}）：{eventStopFailure.Message}",
                    eventStopFailure);
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ResumeAfterFailedStopAsync(CancellationToken cancellationToken = default)
    {
        if (!_stopRequested) return;
        _stopRequested = false;
        Task? previousSubscription;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            previousSubscription = _subscriptionTask;
        }
        finally { _lifecycle.Release(); }
        if (previousSubscription is not null && !previousSubscription.IsCompleted)
            await previousSubscription.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> RequestOnNewConnectionAsync(string command, object payload,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("D");
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new { v = ProtocolVersion, id = requestId, command, payload }, JsonOptions);
        if (body.Length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("传输代理请求超出 1 MiB 限制。");

        using var pipe = await ConnectPipeAsync(cancellationToken).ConfigureAwait(false);
        await WriteFrameAsync(pipe, body, cancellationToken).ConfigureAwait(false);
        var response = await ReadFrameAsync(pipe, allowCleanEof: false, cancellationToken)
            .WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Rust 传输代理未返回响应。");
        return ParseReply(response, requestId);
    }

    private async Task EnsureServerAvailableAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_stopRequested) throw new InvalidOperationException("Rust 传输代理正在关闭。");
            if (await IsPipeAvailableAsync(cancellationToken).ConfigureAwait(false)) return;
            if (!File.Exists(_agentPath))
                throw new FileNotFoundException("未找到 Rust 传输代理，请修复或重新安装客户端。", _agentPath);

            TryStartAgent();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
            Exception? lastError = null;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var candidate = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await candidate.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (exception is TimeoutException or IOException)
                {
                    lastError = exception;
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException("Rust 传输代理未能在规定时间内启动。", lastError);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<NamedPipeClientStream> ConnectPipeAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await candidate.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
                return candidate;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                lastError = exception;
                candidate.Dispose();
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException("Rust 传输代理未能建立命名管道连接。", lastError);
    }

    private async Task SubscribeEventsLoopAsync(CancellationToken cancellationToken)
    {
        var retrySeconds = 1;
        var faultReported = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureServerAvailableAsync(cancellationToken).ConfigureAwait(false);
                using var pipe = await ConnectPipeAsync(cancellationToken).ConfigureAwait(false);
                var requestId = Guid.NewGuid().ToString("D");
                var request = JsonSerializer.SerializeToUtf8Bytes(
                    new { v = ProtocolVersion, id = requestId, command = "subscribe_events", payload = new { } },
                    JsonOptions);
                await WriteFrameAsync(pipe, request, cancellationToken).ConfigureAwait(false);
                var reply = await ReadFrameAsync(pipe, allowCleanEof: false, cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Rust 传输代理未确认事件订阅。");
                var payload = ParseReply(reply, requestId);
                if (!payload.TryGetProperty("subscribed", out var subscribed) || !subscribed.GetBoolean())
                    throw new InvalidDataException("Rust 传输代理未建立事件订阅。");

                retrySeconds = 1;
                faultReported = false;
                EventReceived?.Invoke(this, new TransferAgentEventArgs("connected", 0, null, null, EmptyObject()));
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(pipe, allowCleanEof: true, cancellationToken).ConfigureAwait(false);
                    if (frame is null) break;
                    DispatchEventMessage(frame);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
                ObjectDisposedException or TimeoutException or InvalidOperationException)
            {
                // A reconnect begins with a fresh subscribe_events request. TransferViewModel
                // refreshes the durable task snapshot after the synthetic "connected" event.
                if (!faultReported && !cancellationToken.IsCancellationRequested)
                {
                    faultReported = true;
                    ConnectionFaulted?.Invoke(exception);
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            retrySeconds = Math.Min(retrySeconds * 2, 15);
        }
    }

    private void DispatchEventMessage(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("v", out var version) || version.GetInt32() != ProtocolVersion ||
            !root.TryGetProperty("event", out var eventElement))
            throw new InvalidDataException("Rust 传输代理返回了无效事件。");

        var eventName = eventElement.GetString() ?? string.Empty;
        var sequence = root.TryGetProperty("seq", out var seqElement) ? seqElement.GetInt64() : 0;
        var taskId = root.TryGetProperty("task_id", out var taskElement) && taskElement.ValueKind == JsonValueKind.String
            ? taskElement.GetString() : null;
        var operationId = root.TryGetProperty("operation_id", out var opElement) && opElement.ValueKind == JsonValueKind.String
            ? opElement.GetString() : null;
        var payload = root.TryGetProperty("payload", out var eventPayload) ? eventPayload.Clone() : EmptyObject();
        EventReceived?.Invoke(this, new TransferAgentEventArgs(eventName, sequence, taskId, operationId, payload));
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] body, CancellationToken cancellationToken)
    {
        if (body.Length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("传输代理请求超出 1 MiB 限制。");
        var header = new byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, bool allowCleanEof,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyAsync(stream, header, allowCleanEof, cancellationToken).ConfigureAwait(false))
            return null;
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Rust 传输代理返回了超出 1 MiB 的消息。");
        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, allowCleanEof: false, cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("Rust 传输代理在消息中途断开。");
        return body;
    }

    private static JsonElement ParseReply(ReadOnlySpan<byte> body, string expectedId)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("v", out var version) || version.GetInt32() != ProtocolVersion ||
            !root.TryGetProperty("id", out var idElement) || idElement.GetString() != expectedId)
            throw new InvalidDataException("Rust 传输代理响应与请求不匹配。");

        if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement : default;
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() : null;
            var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString() : null;
            throw new TransferAgentException(code ?? "agent_error", message ?? "传输代理操作失败。");
        }

        return root.TryGetProperty("payload", out var replyPayload) ? replyPayload.Clone() : EmptyObject();
    }

    private async Task<bool> IsPipeAvailableAsync(CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            return false;
        }
    }

    private async Task WaitForPipeUnavailableAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && await IsPipeAvailableAsync(cancellationToken).ConfigureAwait(false))
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        if (await IsPipeAvailableAsync(cancellationToken).ConfigureAwait(false))
            throw new IOException("Rust 传输代理进程已退出，但命名管道仍可连接。仍有代理实例响应，无法安全完成更新。");
    }

    private void TryStartAgent()
    {
        if (_process is { HasExited: false }) return;
        try
        {
            var start = new ProcessStartInfo(_agentPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_agentPath)!
            };
            start.ArgumentList.Add("--pipe-name");
            start.ArgumentList.Add(_pipePath);
            start.ArgumentList.Add("--state-namespace");
            start.ArgumentList.Add(_stateNamespace);
            _process = Process.Start(start);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new IOException("无法启动 Rust 传输代理。", exception);
        }
        if (_process is null) throw new IOException("无法启动 Rust 传输代理。");
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer,
        bool allowCleanEof, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowCleanEof) return false;
                throw new EndOfStreamException("Rust 传输代理返回了不完整消息。");
            }
            offset += read;
        }
        return true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RustTransferAgentClient));
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

public sealed record TransferAgentSyncRoot(
    [property: JsonPropertyName("root_id")] string RootId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("logical_path_prefix")] string LogicalPathPrefix);

public sealed class TransferAgentException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

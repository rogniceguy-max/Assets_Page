using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.ViewModels;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Fetches generated WebP derivatives on demand through the API and Rust transfer agent.</summary>
public sealed class FileThumbnailProvider
{
    private static readonly TimeSpan MissingCacheDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DecodeFailureRetryDelay = TimeSpan.FromSeconds(30);
    private readonly V2ApiClient _api;
    private readonly DriveSession _session;
    private readonly TransferViewModel _transfers;
    private readonly string _clientId;
    private readonly string _cacheRoot;
    private readonly object _cacheLock = new();
    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missingUntil = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _notReadyReported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _cacheGenerations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _decodeRefreshUsed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _decodeRetryAfter = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forceRefresh = new(StringComparer.Ordinal);

    public FileThumbnailProvider(V2ApiClient api, DriveSession session, TransferViewModel transfers, string clientId)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuYingDrive", "Thumbnails");
    }

    public string GetCachePath(string fileId, string revisionId)
        => GetCachePathFor(fileId, revisionId);

    public static string GetCachePathFor(string fileId, string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        var key = CacheKey(fileId, revisionId);
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuYingDrive", "Thumbnails");
        return Path.Combine(cacheRoot, key + ".webp");
    }

    public async Task<string?> GetThumbnailPathAsync(string fileId, string revisionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileId) || string.IsNullOrWhiteSpace(revisionId)) return null;
        var key = CacheKey(fileId, revisionId);
        var path = Path.Combine(_cacheRoot, key + ".webp");
        lock (_cacheLock)
        {
            if (IsDecodeRetryBlockedLocked(key)) return null;
            if (!_forceRefresh.Contains(key) && IsValidWebp(path)) return path;
        }
        if (_missingUntil.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) return null;

        var candidate = new Lazy<Task<string?>>(
            // A virtualized view item may be recycled while another item is waiting on the
            // same thumbnail. Its cancellation must stop only that caller, never the shared
            // download used by the other visible items.
            () => DownloadAsync(fileId, revisionId, key, path, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = _inflight.GetOrAdd(key, candidate);
        var task = shared.Value;
        _ = task.ContinueWith(_ =>
                ((ICollection<KeyValuePair<string, Lazy<Task<string?>>>>)_inflight)
                    .Remove(new KeyValuePair<string, Lazy<Task<string?>>>(key, shared)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> DownloadAsync(string fileId, string revisionId, string key, string cachePath,
        CancellationToken cancellationToken)
    {
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            lock (_cacheLock)
            {
                if (IsDecodeRetryBlockedLocked(key)) return null;
                if (!_forceRefresh.Contains(key) && IsValidWebp(cachePath)) return cachePath;
            }
            if (_missingUntil.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) return null;

            Directory.CreateDirectory(_cacheRoot);
            temporaryPath = Path.Combine(_cacheRoot, $"{key}.{Guid.NewGuid():N}.download.webp");
            FileDownloadTicketResponse ticket;
            try
            {
                ticket = await _api.CreateFileThumbnailTicketAsync(_session.SessionToken, fileId, _clientId,
                    revisionId, cancellationToken).ConfigureAwait(false);
            }
            catch (V2ApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                _missingUntil[key] = DateTimeOffset.UtcNow + MissingCacheDuration;
                if (_notReadyReported.TryAdd(key, 0))
                    App.Record(new InvalidOperationException(
                        $"缩略图派生文件尚未生成（HTTP 404，错误码 {exception.ErrorCode ?? "unknown"}）。"),
                        "FileThumbnailProvider.NotReady");
                return null;
            }

            if (ticket.FileId != fileId || ticket.RevisionId != revisionId ||
                ticket.ObjectKind != "thumbnail" || ticket.Size <= 0 ||
                ticket.Size > 16 * 1024 * 1024 ||
                ticket.Sha256.Length != 64 || !ticket.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("缩略图票据与当前文件修订不匹配。");

            var queued = await _transfers.QueueDownloadAsync(ticket, temporaryPath,
                $"缩略图 {key[..12]}", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (queued is null)
                throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                    ? "缩略图未能进入下载队列。" : _transfers.OperationErrorMessage);
            _ = await _transfers.WaitForTransferAsync(queued, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length != ticket.Size ||
                !await IsMatchingWebpAsync(temporaryPath, ticket.Sha256, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("下载的缩略图未通过格式或 SHA-256 校验。");

            lock (_cacheLock)
            {
                File.Move(temporaryPath, cachePath, overwrite: true);
                _cacheGenerations[key] = _cacheGenerations.GetValueOrDefault(key) + 1;
                _forceRefresh.Remove(key);
            }
            temporaryPath = null;
            _missingUntil.TryRemove(key, out _);
            _notReadyReported.TryRemove(key, out _);
            return cachePath;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _downloadGate.Release();
        }
    }

    private static string CacheKey(string fileId, string revisionId) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fileId + "\0" + revisionId)))
            .ToLowerInvariant();

    internal readonly record struct CacheFileStamp(long Length, long LastWriteUtcTicks, long Generation);

    internal bool TryCaptureCacheFileStamp(string fileId, string revisionId, string path,
        out CacheFileStamp stamp)
    {
        stamp = default;
        if (string.IsNullOrWhiteSpace(fileId) || string.IsNullOrWhiteSpace(revisionId) ||
            !IsExpectedCachePath(fileId, revisionId, path))
            return false;

        var key = CacheKey(fileId, revisionId);
        lock (_cacheLock)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                stamp = new CacheFileStamp(info.Length, info.LastWriteTimeUtc.Ticks,
                    _cacheGenerations.GetValueOrDefault(key));
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>
    /// Evicts a cache entry only if it is still the exact file generation that failed decoding.
    /// The first decode failure permits one immediate refresh; a second failure backs off so a
    /// bad server derivative cannot trigger a download loop for every realized gallery card.
    /// </summary>
    internal bool InvalidateAfterDecodeFailure(string fileId, string revisionId, string path,
        CacheFileStamp failedStamp)
    {
        if (string.IsNullOrWhiteSpace(fileId) || string.IsNullOrWhiteSpace(revisionId) ||
            !IsExpectedCachePath(fileId, revisionId, path))
            return false;

        var key = CacheKey(fileId, revisionId);
        lock (_cacheLock)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || _cacheGenerations.GetValueOrDefault(key) != failedStamp.Generation ||
                    info.Length != failedStamp.Length || info.LastWriteTimeUtc.Ticks != failedStamp.LastWriteUtcTicks)
                    return false;

                _forceRefresh.Add(key);
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                _cacheGenerations[key] = failedStamp.Generation + 1;
                if (_decodeRefreshUsed.Add(key)) return true;

                _decodeRetryAfter[key] = DateTimeOffset.UtcNow + DecodeFailureRetryDelay;
                return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal void MarkCacheDecodedSuccessfully(string fileId, string revisionId, string path,
        CacheFileStamp decodedStamp)
    {
        if (string.IsNullOrWhiteSpace(fileId) || string.IsNullOrWhiteSpace(revisionId) ||
            !IsExpectedCachePath(fileId, revisionId, path))
            return;

        var key = CacheKey(fileId, revisionId);
        lock (_cacheLock)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || _cacheGenerations.GetValueOrDefault(key) != decodedStamp.Generation ||
                    info.Length != decodedStamp.Length || info.LastWriteTimeUtc.Ticks != decodedStamp.LastWriteUtcTicks)
                    return;

                _decodeRefreshUsed.Remove(key);
                _decodeRetryAfter.Remove(key);
                _forceRefresh.Remove(key);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private bool IsExpectedCachePath(string fileId, string revisionId, string path)
    {
        try
        {
            var expected = Path.GetFullPath(Path.Combine(_cacheRoot, CacheKey(fileId, revisionId) + ".webp"));
            return string.Equals(expected, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private bool IsDecodeRetryBlockedLocked(string key)
    {
        if (!_decodeRetryAfter.TryGetValue(key, out var retryAfter)) return false;
        if (retryAfter > DateTimeOffset.UtcNow) return true;
        _decodeRetryAfter.Remove(key);
        return false;
    }

    private static bool IsValidWebp(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 12) return false;
            Span<byte> signature = stackalloc byte[12];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return stream.Read(signature) == signature.Length && signature[..4].SequenceEqual("RIFF"u8) &&
                signature[8..12].SequenceEqual("WEBP"u8);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static async Task<bool> IsMatchingWebpAsync(string path, string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!IsValidWebp(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256));
    }
}

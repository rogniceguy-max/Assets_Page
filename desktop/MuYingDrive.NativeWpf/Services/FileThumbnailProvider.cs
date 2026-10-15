using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.ViewModels;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Fetches generated WebP derivatives on demand through the API and Rust transfer agent.</summary>
public sealed class FileThumbnailProvider
{
    private static readonly TimeSpan MissingCacheDuration = TimeSpan.FromSeconds(45);
    private readonly V2ApiClient _api;
    private readonly DriveSession _session;
    private readonly TransferViewModel _transfers;
    private readonly string _clientId;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missingUntil = new(StringComparer.Ordinal);

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
        if (IsValidWebp(path)) return path;
        if (_missingUntil.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) return null;

        var candidate = new Lazy<Task<string?>>(
            () => DownloadAsync(fileId, revisionId, key, path, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = _inflight.GetOrAdd(key, candidate);
        try
        {
            return await shared.Value.ConfigureAwait(false);
        }
        finally
        {
            if (_inflight.TryGetValue(key, out var current) && ReferenceEquals(current, shared))
                _inflight.TryRemove(key, out _);
        }
    }

    private async Task<string?> DownloadAsync(string fileId, string revisionId, string key, string cachePath,
        CancellationToken cancellationToken)
    {
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            if (IsValidWebp(cachePath)) return cachePath;
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
                return null;
            }

            if (ticket.FileId != fileId || ticket.RevisionId != revisionId ||
                (ticket.ObjectKind is not null && ticket.ObjectKind != "thumbnail") || ticket.Size <= 0 ||
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

            File.Move(temporaryPath, cachePath, overwrite: true);
            temporaryPath = null;
            _missingUntil.TryRemove(key, out _);
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

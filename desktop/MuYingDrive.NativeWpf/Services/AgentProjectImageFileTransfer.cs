using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.ViewModels;
using MuYingDrive.ProjectWorkbooks;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Routes workbook image bytes through the file API and the Rust transfer agent.</summary>
public sealed class AgentProjectImageFileTransfer : IProjectFileTransfer
{
    private readonly V2ApiClient _api;
    private readonly DriveSession _session;
    private readonly TransferViewModel _transfers;
    private readonly string _clientId;
    public event Action<ProjectTransferTaskSnapshot>? TaskUpdated;

    public AgentProjectImageFileTransfer(V2ApiClient api, DriveSession session,
        TransferViewModel transfers, string clientId)
    {
        _api = api;
        _session = session;
        _transfers = transfers;
        _clientId = clientId;
        _transfers.TaskUpdated += HandleTaskUpdated;
    }

    private static ProjectTransferTaskSnapshot ProjectSnapshot(TransferAgentTaskSnapshot snapshot) =>
        new(snapshot.OperationId, snapshot.State, snapshot.BytesDone, snapshot.BytesTotal,
            snapshot.Message, snapshot.FileId, snapshot.RevisionId, snapshot.Kind, snapshot.Source,
            snapshot.ScopeId, snapshot.ProjectId, snapshot.Retryable,
            TransferTaskStateProjection.CanCancel(snapshot.State, snapshot.CommitStarted), snapshot.UpdatedAt);

    private void HandleTaskUpdated(TransferAgentTaskSnapshot snapshot) => TaskUpdated?.Invoke(ProjectSnapshot(snapshot));

    public Task<bool> CancelAsync(string operationId) => _transfers.CancelOperationAsync(operationId);

    public ProjectTransferTaskSnapshot? GetTaskSnapshot(string operationId)
    {
        var snapshot = _transfers.TaskSnapshots.FirstOrDefault(task => task.OperationId == operationId);
        return snapshot is null ? null : ProjectSnapshot(snapshot);
    }

    public IReadOnlyList<ProjectTransferTaskSnapshot> GetTaskSnapshots() =>
        _transfers.TaskSnapshots.Select(ProjectSnapshot).ToArray();

    public async Task<ProjectFileTransferResult> UploadAsync(string projectId, string fileName, string mediaType,
        string localPath, string logicalPath, CancellationToken cancellationToken = default, string? operationId = null)
    {
        logicalPath = LogicalFileScope.Normalize(logicalPath.Replace('\\', '/'));
        var resolvedOperationId = operationId ?? Guid.NewGuid().ToString("D");
        var existing = _transfers.TaskSnapshots.FirstOrDefault(task => task.OperationId == resolvedOperationId);
        string? queuedOperationId;
        if (existing?.Retryable == true)
        {
            if (!await _transfers.RetryOperationAsync(resolvedOperationId).ConfigureAwait(false))
                throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                    ? "Rust 传输代理未确认重试。" : _transfers.OperationErrorMessage);
            queuedOperationId = resolvedOperationId;
        }
        else if (existing?.State == "paused")
        {
            if (!await _transfers.ResumeOperationAsync(resolvedOperationId).ConfigureAwait(false))
                throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                    ? "Rust 传输代理未确认继续。" : _transfers.OperationErrorMessage);
            queuedOperationId = resolvedOperationId;
        }
        else if (existing?.State is "failed" or "conflict" or "cancelled" or "canceled")
        {
            throw new IOException(existing.Message ?? "此前的 Rust 传输任务不可重试；请创建新的上传操作。");
        }
        else if (existing is not null)
        {
            queuedOperationId = resolvedOperationId;
        }
        else
        {
            queuedOperationId = await _transfers.QueueUploadAsync(localPath, $"project:{projectId}", projectId,
                logicalPath, displayName: Path.GetFileNameWithoutExtension(fileName),
                cancellationToken: cancellationToken, operationId: resolvedOperationId,
                source: "project_workbook").ConfigureAwait(false);
        }
        if (queuedOperationId is null)
            throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                ? "图片未能进入上传队列。" : _transfers.OperationErrorMessage);

        var committed = await _transfers.WaitForTransferAsync(resolvedOperationId, cancellationToken).ConfigureAwait(false);
        var fileId = committed.FileId ?? throw new InvalidDataException("上传完成响应缺少 file_id。");
        return new ProjectFileTransferResult(fileId, committed.RevisionId ?? string.Empty,
            committed.LogicalPath ?? logicalPath, committed.BytesTotal ?? new FileInfo(localPath).Length,
            mediaType, committed.Version ?? 1)
        { OperationId = resolvedOperationId };
    }

    public async Task<string> DownloadToLocalAsync(string fileId, string? revisionId, string fileName,
        CancellationToken cancellationToken = default, string? scopeId = null, string? projectId = null)
    {
        var revision = string.IsNullOrWhiteSpace(revisionId) ? "current" : revisionId;
        var ticket = await _api.CreateFileDownloadTicketAsync(_session.SessionToken, fileId, _clientId, revisionId,
            cancellationToken).ConfigureAwait(false);
        if (ticket.FileId != fileId || (!string.IsNullOrWhiteSpace(revisionId) && ticket.RevisionId != revisionId)
            || string.IsNullOrWhiteSpace(ticket.ObjectId) || ticket.Size < 0
            || ticket.Sha256.Length != 64 || !ticket.Sha256.All(Uri.IsHexDigit)
            || ticket.ObjectKind == "thumbnail")
            throw new InvalidDataException("下载凭据与请求的原文件版本不匹配。");
        byte[] expectedHash;
        try { expectedHash = Convert.FromHexString(ticket.Sha256); }
        catch (FormatException exception) { throw new InvalidDataException("下载凭据中的 SHA-256 无效。", exception); }
        if (expectedHash.Length != 32) throw new InvalidDataException("下载凭据中的 SHA-256 长度无效。");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuYingDrive", "FileCache");
        Directory.CreateDirectory(root);
        EnsureCacheDirectoryIsLocal(root);
        var safeId = string.Concat(fileId.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var safeRevision = string.Concat(revision.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName.Any(Path.GetInvalidFileNameChars().Contains)) safeName = safeId + ".bin";
        var path = Path.Combine(root, $"{safeId}-{safeRevision}-{safeName}");
        if (await MatchesExpectedHashAsync(path, expectedHash, cancellationToken).ConfigureAwait(false)) return path;

        var expectedHashText = Convert.ToHexString(expectedHash).ToLowerInvariant();
        var matchingTask = _transfers.TaskSnapshots
            .Where(task => task.Kind.Contains("download", StringComparison.OrdinalIgnoreCase)
                && task.FileId == fileId && task.RevisionId == ticket.RevisionId
                && task.Source == "project_workbook" && task.ScopeId == scopeId && task.ProjectId == projectId)
            .OrderByDescending(task => task.UpdatedAt)
            .FirstOrDefault();
        if (matchingTask is not null && matchingTask.State is
            "queued" or "waiting_stable" or "hashing" or "transferring" or "verifying" or "staged" or "committing" or "paused" or "waiting_network" or "offline")
        {
            var taskPath = RequireCachePath(root, matchingTask.DestinationPath);
            if (matchingTask.State == "paused")
            {
                if (!await _transfers.ResumeOperationAsync(matchingTask.OperationId).ConfigureAwait(false))
                    throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                        ? "暂停的下载任务无法继续。" : _transfers.OperationErrorMessage);
            }
            var activeResult = await _transfers.WaitForTransferAsync(matchingTask.OperationId, cancellationToken).ConfigureAwait(false);
            if (activeResult.State != "completed" || !await MatchesExpectedHashAsync(taskPath, expectedHash, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("下载任务结束后，本地文件未通过 SHA-256 校验。");
            return taskPath;
        }

        if (matchingTask is { Retryable: true })
        {
            var taskPath = RequireCachePath(root, matchingTask.DestinationPath);
            if (await MatchesExpectedHashAsync(taskPath, expectedHash, cancellationToken).ConfigureAwait(false))
                return taskPath;
            if (!File.Exists(taskPath))
            {
                if (!await _transfers.RetryOperationAsync(matchingTask.OperationId).ConfigureAwait(false))
                    throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                        ? "失败的下载任务无法安全重试。" : _transfers.OperationErrorMessage);
                var retried = await _transfers.WaitForTransferAsync(matchingTask.OperationId, cancellationToken).ConfigureAwait(false);
                if (retried.State != "completed" || !await MatchesExpectedHashAsync(taskPath, expectedHash, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("重试下载后，本地文件未通过 SHA-256 校验。");
                return taskPath;
            }
        }

        // Preserve any mismatching file and download to a checksum-qualified name. This keeps
        // an untrusted or stale cache entry from being opened or silently overwritten.
        var hashQualifiedName = $"{safeId}-{safeRevision}-{expectedHashText[..12]}-{safeName}";
        path = Path.Combine(root, hashQualifiedName);
        if (await MatchesExpectedHashAsync(path, expectedHash, cancellationToken).ConfigureAwait(false)) return path;
        for (var suffix = 1; File.Exists(path); suffix++)
            path = Path.Combine(root, $"{safeId}-{safeRevision}-{expectedHashText[..12]}-{suffix}-{safeName}");

        var queued = await _transfers.QueueDownloadAsync(ticket, path, Path.GetFileName(ticket.FileId) + ".image",
            cancellationToken: cancellationToken, source: "project_workbook", scopeId: scopeId, projectId: projectId).ConfigureAwait(false);
        if (queued is null)
            throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                ? "文件未能进入下载队列。" : _transfers.OperationErrorMessage);
        var completed = await _transfers.WaitForTransferAsync(queued, cancellationToken).ConfigureAwait(false);
        if (completed.State != "completed" || !await MatchesExpectedHashAsync(path, expectedHash, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("下载完成后，本地文件未通过 SHA-256 校验。");
        return path;
    }

    private static string RequireCachePath(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("已恢复的下载任务缺少本地目标路径。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(prefix, comparison))
            throw new InvalidDataException("已恢复的下载任务目标路径超出应用缓存目录，已拒绝访问。");
        return fullPath;
    }

    private static void EnsureCacheDirectoryIsLocal(string root)
    {
        var appDirectory = Directory.GetParent(Path.GetFullPath(root))?.FullName
            ?? throw new InvalidDataException("应用缓存目录无效。");
        foreach (var directory in new[] { appDirectory, Path.GetFullPath(root) })
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("应用缓存目录包含链接路径，已拒绝写入。");
        }
    }

    private static async Task<bool> MatchesExpectedHashAsync(string path, byte[] expectedHash, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("缓存文件是链接路径，已拒绝读取。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    public async Task<byte[]> DownloadImageAsync(string fileId, string? revisionId, bool thumbnail,
        CancellationToken cancellationToken = default)
    {
        if (thumbnail)
        {
            try
            {
                var derivative = await DownloadThumbnailToLocalAsync(fileId, revisionId, cancellationToken)
                    .ConfigureAwait(false);
                return await File.ReadAllBytesAsync(derivative, cancellationToken).ConfigureAwait(false);
            }
            catch (V2ApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // A derivative may not exist yet; use the original only for this explicit preview fallback.
            }
        }

        var path = await DownloadToLocalAsync(fileId, revisionId, fileId + ".image", cancellationToken)
            .ConfigureAwait(false);
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadThumbnailToLocalAsync(string fileId, string? revisionId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(revisionId))
        {
            var cached = FileThumbnailProvider.GetCachePathFor(fileId, revisionId);
            if (IsWebp(cached)) return cached;
        }

        var ticket = await _api.CreateFileThumbnailTicketAsync(_session.SessionToken, fileId, _clientId,
            revisionId, cancellationToken).ConfigureAwait(false);
        if (ticket.FileId != fileId || string.IsNullOrWhiteSpace(ticket.RevisionId) ||
            ticket.ObjectKind != "thumbnail" || string.IsNullOrWhiteSpace(ticket.ObjectId) || ticket.Size <= 0 ||
            ticket.Sha256.Length != 64 || !ticket.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("缩略图票据类型无效。");
        var path = FileThumbnailProvider.GetCachePathFor(fileId, ticket.RevisionId);
        if (IsWebp(path)) return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.download.webp";
        try
        {
            var queued = await _transfers.QueueDownloadAsync(ticket, temporaryPath,
                $"缩略图 {Path.GetFileNameWithoutExtension(path)[..12]}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (queued is null)
                throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                    ? "缩略图未能进入下载队列。" : _transfers.OperationErrorMessage);
            _ = await _transfers.WaitForTransferAsync(queued, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(temporaryPath).Length != ticket.Size ||
                !await FileThumbnailProvider.IsMatchingWebpAsync(temporaryPath, ticket.Sha256, cancellationToken)
                    .ConfigureAwait(false))
                throw new InvalidDataException("下载的缩略图格式或大小无效。");
            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsWebp(string path)
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

    public async Task DeleteAsync(string fileId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        _ = await _api.BeginFileOperationAsync(_session.SessionToken, new FileOperationRequest
        {
            OperationId = Guid.NewGuid().ToString("D"),
            Action = "delete",
            ClientId = _clientId,
            Source = "wpf",
            FileId = fileId,
            ExpectedVersion = expectedVersion
        }, cancellationToken).ConfigureAwait(false);
    }
}

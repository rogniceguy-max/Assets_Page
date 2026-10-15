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

    public AgentProjectImageFileTransfer(V2ApiClient api, DriveSession session,
        TransferViewModel transfers, string clientId)
    {
        _api = api;
        _session = session;
        _transfers = transfers;
        _clientId = clientId;
    }

    public async Task<ProjectFileTransferResult> UploadAsync(string projectId, string fileName, string mediaType,
        string localPath, string logicalPath, CancellationToken cancellationToken = default)
    {
        logicalPath = LogicalFileScope.Normalize(logicalPath.Replace('\\', '/'));
        var operationId = await _transfers.QueueUploadAsync(localPath, $"project:{projectId}", projectId,
            logicalPath, displayName: Path.GetFileNameWithoutExtension(fileName), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (operationId is null)
            throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                ? "图片未能进入上传队列。" : _transfers.OperationErrorMessage);

        var committed = await _transfers.WaitForTransferAsync(operationId, cancellationToken).ConfigureAwait(false);
        var fileId = committed.FileId ?? throw new InvalidDataException("上传完成响应缺少 file_id。");
        return new ProjectFileTransferResult(fileId, committed.RevisionId ?? string.Empty,
            committed.LogicalPath ?? logicalPath, committed.BytesTotal ?? new FileInfo(localPath).Length,
            mediaType, committed.Version ?? 1);
    }

    public async Task<string> DownloadToLocalAsync(string fileId, string? revisionId, string fileName,
        CancellationToken cancellationToken = default)
    {
        var revision = string.IsNullOrWhiteSpace(revisionId) ? "current" : revisionId;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuYingDrive", "FileCache");
        Directory.CreateDirectory(root);
        var safeId = string.Concat(fileId.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var safeRevision = string.Concat(revision.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName.Any(Path.GetInvalidFileNameChars().Contains)) safeName = safeId + ".bin";
        var path = Path.Combine(root, $"{safeId}-{safeRevision}-{safeName}");
        if (File.Exists(path)) return path;

        var ticket = await _api.CreateFileDownloadTicketAsync(_session.SessionToken, fileId, _clientId, revisionId,
            cancellationToken).ConfigureAwait(false);
        var queued = await _transfers.QueueDownloadAsync(ticket, path, Path.GetFileName(ticket.FileId) + ".image",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (queued is null)
            throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                ? "图片预览未能进入下载队列。" : _transfers.OperationErrorMessage);
        _ = await _transfers.WaitForTransferAsync(queued, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path)) throw new IOException("图片下载完成后未找到本地缓存文件。");
        return path;
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
            (ticket.ObjectKind is not null && ticket.ObjectKind != "thumbnail") || ticket.Size <= 0 ||
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

using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

public enum DriveSpaceKind
{
    Department,
    Public,
    Personal,
    OtherDepartment
}

/// <summary>
/// Lists the immediate children of the authenticated user's virtual SMB spaces.
/// Paths are always resolved beneath the configured UNC share root.
/// </summary>
/// <remarks>
/// Read-only by construction. Every question about where a path may point is answered by
/// <see cref="SmbSpaceResolver"/>, which the write operations share, so the two halves
/// cannot drift apart on what "inside the share" means. This type only turns a directory
/// into rows.
/// </remarks>
public sealed class SmbFileCatalog
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".avif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".wmv", ".mpeg", ".mpg"
    };

    private readonly SmbSpaceResolver _spaces;

    public SmbFileCatalog(SmbSpaceResolver spaces) =>
        _spaces = spaces ?? throw new ArgumentNullException(nameof(spaces));

    /// <summary>Resolves one of the four UI space selectors to its UNC directory.</summary>
    public string GetSpacePath(DriveSpaceKind kind) => _spaces.GetSpacePath(kind);

    /// <summary>Enumerates only immediate children; reparse points are omitted to prevent traversal outside the share.</summary>
    public Task<IReadOnlyList<DriveFileItem>> EnumerateAsync(DriveSpaceKind kind, CancellationToken cancellationToken = default)
    {
        return EnumerateDirectoryAsync(kind, GetSpacePath(kind), cancellationToken);
    }

    public Task<IReadOnlyList<DriveFileItem>> EnumerateDirectoryAsync(
        DriveSpaceKind kind, string directory, CancellationToken cancellationToken = default)
    {
        directory = _spaces.RequireReadablePath(kind, directory);
        var readOnly = kind == DriveSpaceKind.OtherDepartment;
        return Task.Run<IReadOnlyList<DriveFileItem>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("NAS 空间不存在或当前账号无权访问。" );

            var results = new List<DriveFileItem>();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);

                // Filter out hidden files, system files, and internal .muying_cache
                if (info.Name.StartsWith(".", StringComparison.Ordinal) ||
                    info.Name.Equals(".muying_cache", StringComparison.OrdinalIgnoreCase) ||
                    info.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                    info.Name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var modified = info.LastWriteTime;
                var size = isDirectory ? 0L : ((FileInfo)info).Length;
                var extension = isDirectory ? string.Empty : Path.GetExtension(path);
                var isImage = ImageExtensions.Contains(extension);
                var isVideo = VideoExtensions.Contains(extension);
                results.Add(new DriveFileItem
                {
                    Id = path,
                    Name = info.Name,
                    FullPath = path,
                    ThumbnailPath = isImage || isVideo ? path : string.Empty,
                    AspectRatio = isVideo ? (16.0 / 9.0) : 1.4,
                    IsVideo = isVideo,
                    IsDirectory = isDirectory,
                    IsReadOnly = readOnly,
                    SizeBytes = size,
                    FileSize = isDirectory ? "—" : FormatSize(size),
                    KindLabel = isDirectory ? "文件夹" : isImage ? "图片" : isVideo ? "视频" : "文件",
                    ModifiedTime = modified,
                    ModifiedLabel = modified.ToString("yyyy/M/d HH:mm")
                });
            }

            return results.OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }, cancellationToken);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

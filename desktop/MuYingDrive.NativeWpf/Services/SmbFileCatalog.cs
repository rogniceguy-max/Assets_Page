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

    private readonly string _shareRoot;
    private readonly V2UserSession _user;

    public SmbFileCatalog(NasClientSettings settings, V2UserSession user)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _shareRoot = Path.GetFullPath(settings.ShareRoot.TrimEnd('\\'));
        if (!_shareRoot.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("SMB 共享根目录必须是 UNC 路径。", nameof(settings));
    }

    /// <summary>Resolves one of the four UI space selectors to its UNC directory.</summary>
    public string GetSpacePath(DriveSpaceKind kind)
    {
        var ownKey = _user.GroupType switch
        {
            "film" => "film",
            "writing" => "writing",
            _ => throw new InvalidOperationException("当前账号没有有效的部门信息。")
        };

        var key = kind switch
        {
            DriveSpaceKind.Department => ownKey,
            DriveSpaceKind.Public => "public",
            DriveSpaceKind.Personal => "personal",
            DriveSpaceKind.OtherDepartment => ownKey == "film" ? "writing" : "film",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        if (!_user.Spaces.TryGetValue(key, out var virtualPath) || string.IsNullOrWhiteSpace(virtualPath))
            throw new InvalidOperationException("服务端没有返回该空间的路径。");

        return ResolveVirtualPath(virtualPath);
    }

    /// <summary>Enumerates only immediate children; reparse points are omitted to prevent traversal outside the share.</summary>
    public Task<IReadOnlyList<DriveFileItem>> EnumerateAsync(DriveSpaceKind kind, CancellationToken cancellationToken = default)
    {
        return EnumerateDirectoryAsync(kind, GetSpacePath(kind), cancellationToken);
    }

    public Task<IReadOnlyList<DriveFileItem>> EnumerateDirectoryAsync(
        DriveSpaceKind kind, string directory, CancellationToken cancellationToken = default)
    {
        var root = GetSpacePath(kind);
        directory = Path.GetFullPath(directory);
        var rootPrefix = root.TrimEnd('\\') + "\\";
        if (!directory.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目录路径超出当前空间范围。");
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
                    AspectRatio = 1.4,
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

    private string ResolveVirtualPath(string virtualPath)
    {
        if (virtualPath.Contains('\\') || virtualPath.Contains(':') || virtualPath.Any(char.IsControl))
            throw new InvalidOperationException("服务端返回了无效的空间路径。");

        var segments = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new InvalidOperationException("服务端返回了无效的空间路径。");

        var resolved = Path.GetFullPath(Path.Combine(new[] { _shareRoot }.Concat(segments).ToArray()));
        var rootPrefix = _shareRoot.EndsWith('\\') ? _shareRoot : _shareRoot + "\\";
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("空间路径超出 SMB 共享范围。");
        return resolved;
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

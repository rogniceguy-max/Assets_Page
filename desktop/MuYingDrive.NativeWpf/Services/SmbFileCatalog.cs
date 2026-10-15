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
/// <summary>
/// What a listing found, and whether it stopped before the end.
/// </summary>
/// <param name="Truncated">
/// True when a bound was reached. Carried rather than logged, because it changes what the
/// list means: "these are the pictures" and "these are the first eight hundred pictures"
/// are different claims and only the view can tell the user which one it is showing.
/// </param>
public sealed record DirectoryListing(IReadOnlyList<DriveFileItem> Items, bool Truncated);

public sealed class SmbFileCatalog
{
    // Internal rather than private: SpaceScope decides whether a file belongs in a media
    // view, and it has to classify an extension the same way this type does. Two copies of
    // this list is how a file ends up listed in one place and filtered out of another.
    internal static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".avif"
    };

    internal static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".wmv", ".mpeg", ".mpg"
    };

    private readonly SmbSpaceResolver _spaces;

    public SmbFileCatalog(SmbSpaceResolver spaces) =>
        _spaces = spaces ?? throw new ArgumentNullException(nameof(spaces));

    /// <summary>Enumerates only immediate children; reparse points are omitted to prevent traversal outside the share.</summary>
    public Task<IReadOnlyList<DriveFileItem>> EnumerateAsync(SpaceScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return EnumerateDirectoryAsync(scope, scope.Root, cancellationToken);
    }

    /// <summary>
    /// How many files a flattened view collects before it stops.
    /// </summary>
    /// <remarks>
    /// A bound rather than no bound: this walks whole trees, and a view that hangs on a
    /// directory with fifty thousand files is worse than one that says what it did not show.
    /// <see cref="DirectoryListing.Truncated"/> is what makes that sayable — a silent cap
    /// reads as "this is everything", which is a different and wrong answer.
    /// </remarks>
    public const int FlattenedLimit = 800;

    public Task<IReadOnlyList<DriveFileItem>> EnumerateDirectoryAsync(
        SpaceScope scope, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        directory = _spaces.RequireReadablePath(scope, directory);
        var readOnly = !scope.CanWrite;
        return Task.Run<IReadOnlyList<DriveFileItem>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("NAS 空间不存在或当前账号无权访问。" );

            var results = new List<DriveFileItem>();
            foreach (var path in SafeEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryDescribe(path, scope, readOnly, out var item)) results.Add(item);
            }

            return results.OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }, cancellationToken);
    }

    /// <summary>
    /// Every file in the tree under the scope's root, with the folders left out.
    /// </summary>
    /// <remarks>
    /// For the global 图片 and 视频 views. Folders are not listed because the view is already
    /// showing what is inside them, and a row you cannot usefully open is noise; the sidebar
    /// tree is where you go when you want a particular project.
    /// </remarks>
    public Task<DirectoryListing> EnumerateFlattenedAsync(
        SpaceScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var root = _spaces.RequireReadablePath(scope, scope.Root);
        var readOnly = !scope.CanWrite;
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("NAS 空间不存在或当前账号无权访问。");

            var results = new List<DriveFileItem>();
            var pending = new Stack<string>();
            pending.Push(root);
            var truncated = false;

            while (pending.Count > 0 && !truncated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in SafeEntries(pending.Pop()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (results.Count >= FlattenedLimit) { truncated = true; break; }
                    if (!TryDescribe(path, scope, readOnly, out var item)) continue;
                    if (item.IsDirectory) pending.Push(item.FullPath);
                    else results.Add(item);
                }
            }

            return new DirectoryListing(
                results.OrderByDescending(item => item.ModifiedTime).ToArray(), truncated);
        }, cancellationToken);
    }

    /// <summary>
    /// One directory's entries, or none when it cannot be read.
    /// </summary>
    /// <remarks>
    /// A subtree the signed-in account may not open is normal — the project root is shared
    /// and permissions are per-directory — and it must not fail the whole listing. A flat
    /// view that threw because one folder inside it was unreadable would be unusable.
    /// </remarks>
    private static IEnumerable<string> SafeEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>Describes one entry, or returns false when it should not be listed at all.</summary>
    private bool TryDescribe(string path, SpaceScope scope, bool readOnly, out DriveFileItem item)
    {
        item = null!;
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        // A link is not followed and not shown: reaching through one would leave the share.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return false;

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);

        // Hidden files, system files and the thumbnail cache the media worker writes.
        if (info.Name.StartsWith(".", StringComparison.Ordinal) ||
            info.Name.Equals(".muying_cache", StringComparison.OrdinalIgnoreCase) ||
            info.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            info.Name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = isDirectory ? string.Empty : Path.GetExtension(path);

        // A media view lists only its own kind, but never hides folders: walking into them
        // is the only way to reach the media they organise. A flattened view still needs
        // them here so it can descend — it just does not put them in the list.
        if (!isDirectory && !scope.IncludesFile(extension))
            return false;

        var modified = info.LastWriteTime;
        var size = isDirectory ? 0L : ((FileInfo)info).Length;
        var isImage = ImageExtensions.Contains(extension);
        var isVideo = VideoExtensions.Contains(extension);

        var logicalPath = string.Empty;
        try
        {
            var rel = Path.GetRelativePath(scope.Root, path).Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(rel) && rel != "." && !string.Equals(rel, info.Name, StringComparison.OrdinalIgnoreCase))
            {
                logicalPath = rel;
            }
        }
        catch
        {
        }

        item = new DriveFileItem
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
            ModifiedLabel = modified.ToString("yyyy/M/d HH:mm"),
            LogicalPath = logicalPath
        };
        return true;
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

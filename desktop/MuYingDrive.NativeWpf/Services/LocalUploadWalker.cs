namespace MuYingDrive.NativeWpf.Services;

/// <summary>One local file chosen for upload, and where it belongs relative to the target folder.</summary>
public sealed record LocalUploadEntry(string LocalPath, string RelativePath);

/// <summary>What a folder selection expands to before anything is queued.</summary>
/// <param name="Files">Every file found, with its path relative to the upload destination.</param>
/// <param name="Directories">
/// Every directory to create, including empty ones. Without these the folder's shape would
/// be lost — an empty subfolder exists for a reason and the transfer engine, which creates
/// parents implicitly, would never reproduce it.
/// </param>
public sealed record LocalUploadPlan(
    IReadOnlyList<LocalUploadEntry> Files,
    IReadOnlyList<string> Directories);

/// <summary>
/// Expands a chosen folder into the files to upload and the directories to create.
/// </summary>
/// <remarks>
/// Recursed by hand rather than with <c>AllDirectories</c>, because links have to be
/// recognised and skipped: following one would quietly upload content from somewhere the
/// user never selected. The top-level folder's own name becomes the first path segment, so
/// choosing <c>D:\素材</c> produces <c>素材\…</c> inside the NAS folder on screen rather
/// than spraying its contents loose.
/// </remarks>
public static class LocalUploadWalker
{
    /// <summary>
    /// A ceiling on how many items one selection may expand to. Not a transfer limit — a
    /// guard against a mistaken pick of a directory with a million files in it, which would
    /// otherwise fill memory with entries before anything reached the transfer queue.
    /// </summary>
    public const int MaxEntries = 20000;

    public static LocalUploadPlan Enumerate(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("所选文件夹不存在。");

        var top = new DirectoryInfo(root).Name;
        if (string.IsNullOrEmpty(top))
            throw new InvalidOperationException("无法确定所选文件夹的名称。");

        var files = new List<LocalUploadEntry>();
        var directories = new List<string> { top };
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((root, top));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, relative) = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);

                // Skip rather than follow: what a link points at is not what was chosen.
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                var childRelative = Path.Combine(relative, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(childRelative);
                    pending.Push((entry, childRelative));
                }
                else
                {
                    files.Add(new LocalUploadEntry(entry, childRelative));
                }

                if (files.Count + directories.Count > MaxEntries)
                    throw new InvalidOperationException(
                        $"所选的文件夹超过 {MaxEntries} 项，请分几次上传。");
            }
        }

        return new LocalUploadPlan(files, directories);
    }
}

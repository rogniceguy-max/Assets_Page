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
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Warnings);

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
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("不能上传指向其他位置的链接文件夹。");

        var top = new DirectoryInfo(root).Name;
        if (string.IsNullOrEmpty(top))
            throw new InvalidOperationException("无法确定所选文件夹的名称。");
        if (!IsSafeSegmentName(top))
            throw new InvalidOperationException("文件夹名称不适合作为个人空间路径。");

        var files = new List<LocalUploadEntry>();
        var directories = new List<string> { top };
        var warnings = new List<string>();
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((root, top));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, relative) = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current); }
            catch (Exception exception) when (IsAccessError(exception))
            {
                warnings.Add($"无法读取目录「{relative}」：{exception.Message}");
                continue;
            }

            try
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception exception) when (IsAccessError(exception))
                    {
                        warnings.Add($"无法读取「{Path.GetFileName(entry)}」：{exception.Message}");
                        continue;
                    }

                    var name = Path.GetFileName(entry);
                    if (!IsSafeSegmentName(name))
                    {
                        warnings.Add($"已跳过不安全的路径名称「{name}」。");
                        continue;
                    }

                    // Skip rather than follow: what a link points at is not what was chosen.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        warnings.Add($"已跳过链接「{Path.Combine(relative, name)}」。");
                        continue;
                    }

                    var childRelative = Path.Combine(relative, name);
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
            catch (Exception exception) when (IsAccessError(exception))
            {
                // Enumeration can fail after yielding several entries. Keep those entries and
                // continue with sibling directories so one unreadable subtree does not discard
                // every file the user selected.
                warnings.Add($"目录「{relative}」读取不完整：{exception.Message}");
            }
        }

        return new LocalUploadPlan(files, directories, warnings);
    }

    public static bool IsSafeSegmentName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 &&
        !value.Any(char.IsControl);

    private static bool IsAccessError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}

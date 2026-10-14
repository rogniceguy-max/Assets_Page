namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// The write half of the SMB share: rename, duplicate, delete, create directories.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="SmbFileCatalog"/>, which stays a reader. Both go through one
/// <see cref="SmbSpaceResolver"/> so they cannot disagree about where a path may point.
///
/// Every path here is checked twice before anything is touched: it must be inside the
/// space (so it cannot escape the share) and it must not traverse a link (so it cannot be
/// redirected out of it). Both the source **and** the destination are checked — a rename
/// whose source is legitimate and whose destination is a junction would otherwise move
/// data through the link.
///
/// The work is blocking filesystem IO against a UNC path, so each operation is wrapped in
/// <see cref="Task.Run(CancellationToken, System.Action)"/> to keep it off the UI thread,
/// the same way the catalog does its enumeration.
/// </remarks>
public sealed class SmbFileMutations(SmbSpaceResolver spaces)
{
    private readonly SmbSpaceResolver _spaces = spaces ?? throw new ArgumentNullException(nameof(spaces));

    /// <summary>
    /// The name <paramref name="desiredName"/> should take in <paramref name="directory"/>
    /// so that nothing is overwritten.
    /// </summary>
    /// <param name="claimed">
    /// Names already taken earlier in the same batch. Without this a folder containing two
    /// files called <c>report.pdf</c> resolves both to the same free name — neither exists
    /// yet — and the second fails, rather than becoming <c>report (2).pdf</c>. This is what
    /// makes a two-hundred-file upload run to completion without asking anything.
    /// </param>
    public string ResolveFreeName(
        SpaceScope scope, string directory, string desiredName, ISet<string>? claimed = null)
    {
        var safeName = SmbSpaceResolver.ValidateNameSegment(desiredName);
        if (IsFree(directory, safeName, claimed)) return safeName;

        for (var index = UniqueName.FirstSuffix; index <= UniqueName.MaxAttempts; index++)
        {
            var candidate = UniqueName.WithSuffix(safeName, index);
            if (IsFree(directory, candidate, claimed)) return candidate;
        }

        throw new InvalidOperationException("同名项目过多，无法自动命名。请先整理目标文件夹。");
    }

    /// <summary>Duplicates a file or a whole folder beside itself, never overwriting.</summary>
    /// <returns>The full path of the copy, so the caller can select or report it.</returns>
    /// <remarks>
    /// A folder copy reads and writes every byte through this client with no progress and
    /// no resume — it is not the resumable transfer engine, which handles single files and
    /// exists for cross-media moves. On a large folder it will simply take a while.
    /// </remarks>
    public Task<string> DuplicateInPlaceAsync(
        SpaceScope scope, string sourceFullPath, CancellationToken cancellationToken = default)
    {
        var source = _spaces.RequireWritablePath(scope, sourceFullPath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
                throw new FileNotFoundException("要复制的内容不存在，可能已被移动或删除。", source);

            var parent = Path.GetDirectoryName(source)
                ?? throw new InvalidOperationException("无法确定所在文件夹。");
            var destination = Path.Combine(parent, ResolveFreeName(scope, parent, Path.GetFileName(source)));
            _spaces.RequireWritablePath(scope, destination);

            if (isDirectory) CopyTree(source, destination, cancellationToken);
            else File.Copy(source, destination, overwrite: false);
            return destination;
        }, cancellationToken);
    }

    /// <summary>
    /// Renames in place. A name that is already taken is suffixed rather than refused, and
    /// renaming to the name it already has is a no-op rather than an error.
    /// </summary>
    public Task<string> RenameAsync(
        SpaceScope scope, string sourceFullPath, string newName,
        ISet<string>? claimed = null, CancellationToken cancellationToken = default)
    {
        var source = _spaces.RequireWritablePath(scope, sourceFullPath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
                throw new FileNotFoundException("要重命名的内容不存在，可能已被移动或删除。", source);

            var parent = Path.GetDirectoryName(source)
                ?? throw new InvalidOperationException("无法确定所在文件夹。");
            var destination = Path.Combine(parent, ResolveFreeName(scope, parent, newName, claimed));
            _spaces.RequireWritablePath(scope, destination);

            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return source;

            // ResolveFreeName established the destination was free; Move refuses an existing
            // one anyway, which is the last line of defence if something appeared in between.
            if (isDirectory) Directory.Move(source, destination);
            else File.Move(source, destination, overwrite: false);
            return destination;
        }, cancellationToken);
    }

    /// <summary>Deletes a file, or a folder and everything under it.</summary>
    public Task DeleteAsync(SpaceScope scope, string targetFullPath, CancellationToken cancellationToken = default)
    {
        var target = _spaces.RequireWritablePath(scope, targetFullPath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                DeleteTree(target, cancellationToken);
                return;
            }

            if (File.Exists(target))
            {
                File.Delete(target);
                return;
            }

            throw new FileNotFoundException("要删除的内容不存在，可能已被移动或删除。", target);
        }, cancellationToken);
    }

    /// <summary>Creates directories that do not exist yet, depth first, so a folder's shape survives an upload.</summary>
    /// <remarks>
    /// Done explicitly rather than left to the transfer engine's implicit parent creation:
    /// that would create a folder only when its first file is attempted, would never create
    /// an empty one, and would surface a permission problem as one failed transfer per file
    /// instead of one clear error.
    /// </remarks>
    public Task EnsureDirectoriesAsync(
        SpaceScope scope, IReadOnlyList<string> directories, CancellationToken cancellationToken = default)
    {
        var verified = directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => _spaces.RequireWritablePath(scope, directory))
            // Shallowest first: RequireWritablePath walks every existing component, and
            // creating a parent before its child keeps that walk meaningful.
            .OrderBy(directory => directory.Length)
            .ToArray();

        return Task.Run(() =>
        {
            foreach (var directory in verified)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// How many entries a folder holds, counting everything beneath it.
    /// </summary>
    /// <remarks>
    /// For the delete confirmation. Enumerated for real rather than estimated, because a
    /// dialog that says "42 items" and then removes 900 is worse than no number at all.
    /// </remarks>
    public Task<int> CountContainedAsync(
        SpaceScope scope, string path, CancellationToken cancellationToken = default)
    {
        var target = _spaces.RequireReadablePath(scope, path);
        return Task.Run(() =>
        {
            if (!Directory.Exists(target)) return 0;

            var total = 0;
            var pending = new Stack<string>();
            pending.Push(target);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    total++;
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0 &&
                        (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(entry);
                    }
                }
            }

            return total;
        }, cancellationToken);
    }

    private static bool IsFree(string directory, string name, ISet<string>? claimed) =>
        (claimed is null || !claimed.Contains(name)) &&
        !File.Exists(Path.Combine(directory, name)) &&
        !Directory.Exists(Path.Combine(directory, name));

    private static void CopyTree(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            // A link is skipped rather than copied: copying what it points at would pull
            // content in from outside the folder the user asked to duplicate.
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyTree(entry, target, cancellationToken);
            else File.Copy(entry, target, overwrite: false);
        }
    }

    /// <summary>
    /// Removes a directory tree, deepest first, without following links.
    /// </summary>
    /// <remarks>
    /// Written out rather than delegating to <c>Directory.Delete(recursive: true)</c>: the
    /// reparse-point behaviour of that overload has varied across versions, and this is a
    /// delete against production material where "probably follows the link" is not a
    /// property worth relying on. A link encountered here is removed as a link — the thing
    /// it points at is never touched.
    /// </remarks>
    private static void DeleteTree(string root, CancellationToken cancellationToken)
    {
        var discovered = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            discovered.Add(current);

            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry, recursive: false);
                    else File.Delete(entry);
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else File.Delete(entry);
            }
        }

        // Every child was discovered after its parent, so reversing guarantees a directory
        // is only removed once everything beneath it is already gone.
        for (var index = discovered.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(discovered[index], recursive: false);
        }
    }
}

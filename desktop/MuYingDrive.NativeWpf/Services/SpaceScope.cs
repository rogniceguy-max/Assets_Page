namespace MuYingDrive.NativeWpf.Services;

/// <summary>Which entries a space shows. Directories are always shown; this filters files.</summary>
public enum SpaceMediaKind
{
    All,
    Images,
    Videos
}

/// <summary>
/// One place the browse and write services may work in: an absolute UNC directory, the
/// rules that apply inside it, and nothing else.
/// </summary>
/// <remarks>
/// This exists so that nothing below the resolver has to know which department the
/// signed-in user belongs to. Before it, <see cref="SmbFileCatalog"/> and
/// <see cref="SmbFileMutations"/> took a <see cref="DriveSpaceKind"/> on every call and
/// asked the resolver to turn that into a path each time — which put the department model,
/// including the notion of an *other* department that is merely mirrored, into code whose
/// only real question was "a root and a policy".
///
/// That model is on its way out: the navigation is being rebuilt around projects, where a
/// space is a project directory rather than a department. This is the seam that keeps that
/// change from becoming a rewrite of the browser — the browser stops caring what kind of
/// space it is looking at, and only the code that names a scope has to be updated.
/// </remarks>
public sealed class SpaceScope
{
    public SpaceScope(string root, bool canWrite, SpaceMediaKind media = SpaceMediaKind.All)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        CanWrite = canWrite;
        Media = media;
    }

    /// <summary>The directory this scope resolves to, normalized.</summary>
    public string Root { get; }

    /// <summary>
    /// Whether mutations are allowed here. False for a space the user may only read, which
    /// is what the "只读" badge and every edit command's CanExecute are driven by.
    /// </summary>
    public bool CanWrite { get; }

    /// <summary>Which file kinds are listed. <see cref="SpaceMediaKind.All"/> shows everything.</summary>
    public SpaceMediaKind Media { get; }

    /// <summary>
    /// Whether a file with this extension belongs in this scope. Directories are not put
    /// through this — a media view still has to let the user walk into the folders the
    /// media is organised in.
    /// </summary>
    public bool IncludesFile(string extension) => Media switch
    {
        SpaceMediaKind.Images => SmbFileCatalog.ImageExtensions.Contains(extension),
        SpaceMediaKind.Videos => SmbFileCatalog.VideoExtensions.Contains(extension),
        _ => true
    };

    /// <summary>The same scope, showing only one kind of media.</summary>
    public SpaceScope WithMedia(SpaceMediaKind media) => new(Root, CanWrite, media);
}

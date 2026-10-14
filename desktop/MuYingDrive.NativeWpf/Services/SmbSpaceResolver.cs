using MuYingDrive.NativeWpf.Models;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// Owns the mapping from the four UI spaces to directories under the configured SMB
/// share, and the rules about what may be touched inside them.
/// </summary>
/// <remarks>
/// Split out of <see cref="SmbFileCatalog"/> when the share stopped being read-only.
/// Listing only ever needed to answer "may I read this?", and a prefix comparison was
/// enough for that. Renaming, copying and deleting need the same question asked about a
/// source and a destination at once, plus a name rule and a link rule that listing never
/// needed — and a fourth hand-rolled copy of the prefix comparison is exactly the drift
/// this codebase has already been bitten by (see V2ApiClient's note on shared certificate
/// rules). Everything that reasons about share paths goes through here now.
/// </remarks>
public sealed class SmbSpaceResolver
{
    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string _shareRoot;
    private readonly V2UserSession _user;

    public SmbSpaceResolver(NasClientSettings settings, V2UserSession user)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _shareRoot = Path.GetFullPath(settings.ShareRoot.TrimEnd('\\'));
        if (!_shareRoot.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("SMB 共享根目录必须是 UNC 路径。", nameof(settings));
    }

    /// <summary>The configured UNC share root, normalized.</summary>
    public string ShareRoot => _shareRoot;

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

    /// <summary>Whether <paramref name="path"/> sits inside the given space.</summary>
    public bool IsWithinSpace(DriveSpaceKind kind, string path) =>
        Within(GetSpacePath(kind), path);

    /// <summary>
    /// Returns <paramref name="path"/> normalized, or throws when it is not inside the
    /// given space. The cheap check, for reading.
    /// </summary>
    /// <remarks>
    /// A string comparison only. That is adequate for listing, which is why it is what
    /// listing has always used: a link inside the share is filtered out of the entries
    /// that are shown, and reaching through one would require already knowing its name.
    /// It deliberately does **not** verify that the path is link-free; see
    /// <see cref="RequireWritablePath"/> for why that is a separate question.
    /// </remarks>
    public string RequireReadablePath(DriveSpaceKind kind, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!Within(GetSpacePath(kind), full))
            throw new InvalidOperationException("路径超出当前空间范围。");
        return full;
    }

    /// <summary>
    /// Returns <paramref name="path"/> normalized, or throws when it is not a location a
    /// write may touch. Every mutation passes this for its source **and** its destination.
    /// </summary>
    /// <remarks>
    /// Adds one question the read check cannot answer: does reaching this path traverse a
    /// link? A prefix comparison says nothing about that. A junction inside the share —
    /// <c>...\film\link</c> pointing at <c>C:\Windows</c> — is filtered out of listings so
    /// it never appears, yet every prefix test on <c>link\...</c> still passes. Reading
    /// through one is bounded by what the NAS chooses to expose; *writing* through one
    /// would mean this client can modify anything the signed-in user can reach anywhere,
    /// which is not what "inside the share" was meant to promise.
    ///
    /// The asymmetry with <see cref="RequireReadablePath"/> is intentional, not an
    /// oversight: it is the difference between what the server presents and what this
    /// client is willing to change.
    /// </remarks>
    public string RequireWritablePath(DriveSpaceKind kind, string path)
    {
        var full = RequireReadablePath(kind, path);
        EnsureNoReparsePoints(full);
        return full;
    }

    /// <summary>
    /// Rejects a path any of whose components is a reparse point (a junction, symlink or
    /// mount point), so a write cannot be redirected outside the share.
    /// </summary>
    /// <remarks>
    /// There is a residual window between this check and the operation itself: nothing in
    /// the API offers an open-by-handle-with-no-follow for UNC paths, so a component could
    /// in principle be swapped for a link in between. The check is therefore done
    /// immediately before the IO rather than once at the start of a longer sequence.
    /// </remarks>
    public static void EnsureNoReparsePoints(string path)
    {
        try
        {
            UpdatePathPolicy.EnsureNoReparsePointComponents(path);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("路径中包含指向其他位置的链接，已拒绝访问。", exception);
        }
    }

    /// <summary>
    /// Validates one name segment — a rename target, or one component of an upload path —
    /// and returns it unchanged.
    /// </summary>
    /// <remarks>
    /// Windows rejects these names at the filesystem layer anyway, but it does so with a
    /// raw <see cref="IOException"/> long after the user has committed to the operation,
    /// and over SMB the failure arrives even less clearly. Checking here means the user
    /// gets told which name is wrong and why.
    /// </remarks>
    public static string ValidateNameSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("名称不能为空。");
        if (name.Length > 255)
            throw new InvalidOperationException("名称过长，最多 255 个字符。");
        if (name is "." or "..")
            throw new InvalidOperationException("名称无效。");
        if (name.Any(character => character < ' ' || InvalidNameChars.Contains(character)))
            throw new InvalidOperationException("名称不能包含 \\ / : * ? \" < > | 或控制字符。");
        if (name[^1] is '.' or ' ')
            throw new InvalidOperationException("名称不能以点或空格结尾。");

        // The extension is stripped before the reserved-name check so that "CON.xlsx" is
        // caught as well as "CON" — Windows treats both as the device.
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        if (ReservedNames.Contains(stem))
            throw new InvalidOperationException("该名称是系统保留名，请换一个。");

        return name;
    }

    private static bool Within(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd('\\');
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase);
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
}

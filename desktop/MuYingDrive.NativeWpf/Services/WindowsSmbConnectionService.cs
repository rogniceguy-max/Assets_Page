using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// Configuration for a single SMB share connection. The UNC share root must come
/// from application configuration; this type contains no NAS host defaults.
/// </summary>
/// <param name="ShareRoot">UNC share root such as <c>\\server\share</c>.</param>
public sealed record WindowsSmbConnectionOptions(string ShareRoot);

/// <summary>
/// Result returned by a Windows SMB connection operation.
/// Native error codes are preserved so callers can distinguish error 1219 without
/// parsing localized text or logging credentials.
/// </summary>
/// <param name="NativeErrorCode">Zero on success, otherwise the Win32/WNet error code.</param>
/// <param name="Failure">A client-side failure that occurred before calling WNet.</param>
public readonly record struct SmbConnectionResult(
    uint NativeErrorCode,
    SmbConnectionFailure Failure = SmbConnectionFailure.None)
{
    /// <summary>True when the requested operation succeeded.</summary>
    public bool Succeeded => NativeErrorCode == 0 && Failure == SmbConnectionFailure.None;

    /// <summary>True when no SMB credential was stored in Credential Manager.</summary>
    public bool CredentialsMissing => Failure == SmbConnectionFailure.CredentialsMissing;

    /// <summary>True for WNet error 1219, indicating an existing connection uses different credentials.</summary>
    public bool HasCredentialConflict => NativeErrorCode == 1219;

    /// <summary>Provides a localized operating-system description without including credentials.</summary>
    public string? ErrorMessage => Succeeded
        ? null
        : CredentialsMissing
            ? "No SMB credential is stored in Windows Credential Manager."
            : new Win32Exception(checked((int)NativeErrorCode)).Message;
}

/// <summary>Client-side condition reported alongside a WNet result.</summary>
public enum SmbConnectionFailure
{
    /// <summary>No client-side failure occurred.</summary>
    None,

    /// <summary>No SMB username and password were found in the configured credential entry.</summary>
    CredentialsMissing
}

/// <summary>
/// Connects to the configured UNC share through WNet. Connection credentials
/// are read from the configured Windows Credential Manager entry. SMB sessions
/// are shared by processes in the Windows logon session, so this service does
/// not cancel UNC connections that it cannot exclusively own.
/// </summary>
public sealed class WindowsSmbConnectionService
{
    private const int ResourceTypeDisk = 1;
    private const uint ErrorNotFound = 1168;

    private readonly WindowsSmbConnectionOptions _options;
    private readonly WindowsCredentialVault _credentialVault;

    /// <summary>
    /// Creates a connection service using an injected UNC share root and credential vault.
    /// </summary>
    public WindowsSmbConnectionService(
        WindowsSmbConnectionOptions options,
        WindowsCredentialVault credentialVault)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
        ValidateShareRoot(_options.ShareRoot);
    }

    /// <summary>
    /// Connects to the configured UNC share using the SMB username and password
    /// stored in the vault. If WNet reports error 1219, the existing connection
    /// remains untouched and the conflict is returned to the caller.
    /// </summary>
    public SmbConnectionResult Connect()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows SMB connections are available only on Windows.");

        if (!_credentialVault.TryReadNasPassword(out var credential) || credential is null)
            return new SmbConnectionResult(ErrorNotFound, SmbConnectionFailure.CredentialsMissing);

        var resource = new NetResource
        {
            ResourceType = ResourceTypeDisk,
            RemoteName = _options.ShareRoot
        };

        // WNetAddConnection2W does not retain the caller's managed strings. Never log
        // these arguments; in particular, do not add them to exception or result text.
        var error = WNetAddConnection2W(
            ref resource,
            credential.Password,
            credential.UserName,
            flags: 0);

        return new SmbConnectionResult(error);
    }

    private static void ValidateShareRoot(string shareRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareRoot);
        if (!shareRoot.StartsWith("\\\\", StringComparison.Ordinal)
            || shareRoot.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("The configured share root must be a UNC path.", nameof(shareRoot));
        }

        var components = shareRoot[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length < 2 || components.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("The configured UNC path must include both a server and a share name.", nameof(shareRoot));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int ResourceType;
        public int DisplayType;
        public int Usage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? RemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Provider;
    }

    [DllImport("Mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode, SetLastError = false, ExactSpelling = true)]
    private static extern uint WNetAddConnection2W(
        ref NetResource netResource,
        string password,
        string userName,
        uint flags);

}

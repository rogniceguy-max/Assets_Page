using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.ComponentModel;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// Names of the Windows Credential Manager entries used by the V2 client.
/// Supply these values from application configuration; do not put passwords or tokens in them.
/// </summary>
/// <param name="V2SessionTokenTarget">Credential Manager target for the V2 session token.</param>
/// <param name="NasPasswordTarget">Credential Manager target for the account's SMB password.</param>
public sealed record WindowsCredentialVaultOptions(
    string V2SessionTokenTarget,
    string NasPasswordTarget);

/// <summary>
/// A username and password read from a Windows Generic Credential entry.
/// The password is sensitive and must never be written to logs or diagnostics.
/// </summary>
/// <param name="UserName">The SMB username stored with the credential.</param>
/// <param name="Password">The SMB password stored in the credential blob.</param>
public sealed class NasPasswordCredential
{
    /// <summary>The SMB username stored with the credential.</summary>
    public string UserName { get; }

    /// <summary>The sensitive SMB password stored in the credential blob.</summary>
    public string Password { get; }

    /// <summary>Creates a credential value. Do not log either property.</summary>
    public NasPasswordCredential(string userName, string password)
    {
        UserName = userName;
        Password = password;
    }

    /// <summary>Returns a redacted representation to prevent accidental secret disclosure.</summary>
    public override string ToString() => "NasPasswordCredential { UserName = [redacted], Password = [redacted] }";
}

/// <summary>A V2 session token and its verified account identity.</summary>
public sealed class V2SessionCredential
{
    public string UserName { get; }
    public string Token { get; }

    public V2SessionCredential(string userName, string token)
    {
        UserName = userName;
        Token = token;
    }

    public override string ToString() => "V2SessionCredential { UserName = [redacted], Token = [redacted] }";
}

/// <summary>
/// Stores the V2 session token and SMB password in the current Windows user's
/// Credential Manager as Generic Credentials. Secrets are never logged by this class.
/// </summary>
public sealed class WindowsCredentialVault
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int MaximumCredentialBlobBytes = 2560;
    private const int ErrorNotFound = 1168;

    private readonly WindowsCredentialVaultOptions _options;

    /// <summary>
    /// Creates a vault using target names supplied by application configuration.
    /// </summary>
    public WindowsCredentialVault(WindowsCredentialVaultOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateTargetName(_options.V2SessionTokenTarget, nameof(options.V2SessionTokenTarget));
        ValidateTargetName(_options.NasPasswordTarget, nameof(options.NasPasswordTarget));
    }

    /// <summary>
    /// Saves or replaces the V2 session token under the configured target name,
    /// with the server username stored as non-secret credential metadata.
    /// </summary>
    public void SaveV2SessionToken(string accountUserName, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUserName);
        SaveSecret(_options.V2SessionTokenTarget, accountUserName, token);
    }

    /// <summary>
    /// Reads the V2 session token. Returns <see langword="false"/> when no entry exists.
    /// The returned string is sensitive and must never be logged.
    /// </summary>
    public bool TryReadV2SessionToken(out V2SessionCredential? credential)
    {
        if (TryReadSecret(_options.V2SessionTokenTarget, out var userName, out var token)
            && !string.IsNullOrWhiteSpace(userName)
            && !string.IsNullOrWhiteSpace(token))
        {
            credential = new V2SessionCredential(userName, token);
            return true;
        }

        credential = null;
        return false;
    }

    /// <summary>
    /// Deletes the configured V2 session token entry. Missing entries are ignored.
    /// </summary>
    public void DeleteV2SessionToken()
    {
        DeleteSecret(_options.V2SessionTokenTarget);
    }

    /// <summary>
    /// Saves or replaces the configured SMB password entry and its associated SMB username.
    /// The password is stored as a UTF-8 Generic Credential blob; the username is credential metadata.
    /// </summary>
    public void SaveNasPassword(string smbUserName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(smbUserName);
        SaveSecret(_options.NasPasswordTarget, smbUserName, password);
    }

    /// <summary>
    /// Reads the configured SMB username and password. Returns <see langword="false"/>
    /// when no entry exists. The returned password is sensitive and must never be logged.
    /// </summary>
    public bool TryReadNasPassword(out NasPasswordCredential? credential)
    {
        if (TryReadSecret(_options.NasPasswordTarget, out var userName, out var password)
            && !string.IsNullOrWhiteSpace(userName)
            && password is not null)
        {
            credential = new NasPasswordCredential(userName, password);
            return true;
        }

        credential = null;
        return false;
    }

    /// <summary>
    /// Deletes the configured SMB password entry. Missing entries are ignored.
    /// </summary>
    public void DeleteNasPassword()
    {
        DeleteSecret(_options.NasPasswordTarget);
    }

    private static void SaveSecret(string targetName, string? userName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        EnsureWindows();

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        if (secretBytes.Length > MaximumCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            throw new ArgumentException("The secret exceeds the Windows Credential Manager Generic Credential size limit.", nameof(secret));
        }

        IntPtr blobPointer = IntPtr.Zero;
        try
        {
            blobPointer = Marshal.AllocHGlobal(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, blobPointer, secretBytes.Length);

            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = checked((uint)secretBytes.Length),
                CredentialBlob = blobPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = userName
            };

            if (!CredWriteW(ref credential, 0))
                throw CreateNativeException("CredWriteW", Marshal.GetLastWin32Error());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            if (blobPointer != IntPtr.Zero)
            {
                Marshal.Copy(new byte[secretBytes.Length], 0, blobPointer, secretBytes.Length);
                Marshal.FreeHGlobal(blobPointer);
            }
        }
    }

    private static bool TryReadSecret(string targetName, out string? userName, out string? secret)
    {
        EnsureWindows();
        userName = null;
        secret = null;

        if (!CredReadW(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return false;

            throw CreateNativeException("CredReadW", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            userName = credential.UserName;
            if (credential.CredentialBlobSize > MaximumCredentialBlobBytes)
                throw new InvalidOperationException("The stored Windows credential blob exceeds the supported size limit.");

            var secretBytes = new byte[credential.CredentialBlobSize];
            try
            {
                if (secretBytes.Length > 0)
                {
                    if (credential.CredentialBlob == IntPtr.Zero)
                        throw new InvalidOperationException("The stored Windows credential blob is unavailable.");

                    Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
                }

                secret = Encoding.UTF8.GetString(secretBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }

            return true;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static void DeleteSecret(string targetName)
    {
        EnsureWindows();
        if (CredDeleteW(targetName, CredentialTypeGeneric, 0))
            return;

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
            throw CreateNativeException("CredDeleteW", error);
    }

    private static void ValidateTargetName(string targetName, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName, parameterName);
        if (targetName.IndexOf('\0') >= 0)
            throw new ArgumentException("Credential target names cannot contain a null character.", parameterName);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Credential Manager is available only on Windows.");
    }

    private static Win32Exception CreateNativeException(string operation, int errorCode)
    {
        // The exception includes only the API name and OS error; never include credential data.
        return new Win32Exception(errorCode, $"Windows Credential Manager operation {operation} failed.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = false, ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);
}

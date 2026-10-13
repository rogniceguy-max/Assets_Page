using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text;

namespace MuYingDrive.UpdateCore;

public static class PackageIntegrity
{
    public static async Task VerifySha256Async(string packagePath, UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(packagePath);
        if (!info.Exists || info.Length != manifest.PackageSize)
            throw new InvalidDataException("更新包大小与签名清单不一致。");
        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(manifest.Sha256)))
            throw new CryptographicException("更新包 SHA-256 校验失败。");
    }

    public static async Task ExtractVerifiedPackageAsync(string packagePath, string destination,
        UpdateManifest manifest, IProgress<long>? extractedBytes = null, CancellationToken cancellationToken = default)
    {
        await VerifySha256Async(packagePath, manifest, cancellationToken).ConfigureAwait(false);
        var publisherThumbprint = UpdateTrustAnchor.LoadCodeSigningThumbprint();
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("更新暂存目录已存在。");
        Directory.CreateDirectory(root);

        long totalExpanded = 0;
        long written = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            if (archive.Entries.Count is 0 or > 20_000)
                throw new InvalidDataException("更新包文件数量无效。");
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = ValidateEntryName(entry);
                if (!seen.Add(relative))
                    throw new InvalidDataException("更新包包含重复路径。");
                totalExpanded = checked(totalExpanded + entry.Length);
                if (totalExpanded > 4L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("更新包展开后超出允许大小。");
                var target = ResolveUnderRoot(root, relative);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                EnsureNoReparsePoints(root, Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                var buffer = new byte[128 * 1024];
                long entryBytes = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    entryBytes = checked(entryBytes + read);
                    if (entryBytes > entry.Length || written + read > totalExpanded)
                        throw new InvalidDataException("更新包文件长度与目录信息不一致。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written = checked(written + read);
                    extractedBytes?.Report(written);
                }
                if (entryBytes != entry.Length)
                    throw new InvalidDataException("更新包文件长度与目录信息不一致。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
        }

        var appExe = Path.Combine(root, "MuYingDrive.NativeWpf.exe");
        var updaterExe = Path.Combine(root, "Updater", "MuYingDrive.Updater.exe");
        VerifyAuthenticodePublisher(appExe, publisherThumbprint);
        VerifyAuthenticodePublisher(updaterExe, publisherThumbprint);
        VerifyExecutableVersion(appExe, manifest.Version);
        VerifyExecutableVersion(updaterExe, manifest.Version);
    }

    public static async Task ValidatePackageForReadyAsync(string packagePath, UpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        await VerifySha256Async(packagePath, manifest, cancellationToken).ConfigureAwait(false);
        var publisherThumbprint = UpdateTrustAnchor.LoadCodeSigningThumbprint();
        var verificationDirectory = Path.Combine(Path.GetDirectoryName(packagePath)!, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(verificationDirectory);
        var appPath = Path.Combine(verificationDirectory, "MuYingDrive.NativeWpf.exe");
        var updaterDirectory = Path.Combine(verificationDirectory, "Updater");
        Directory.CreateDirectory(updaterDirectory);
        var updaterPath = Path.Combine(updaterDirectory, "MuYingDrive.Updater.exe");

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalExpanded = 0;
            var hasApp = false;
            var hasUpdater = false;
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count is 0 or > 20_000)
                throw new InvalidDataException("更新包文件数量无效。");

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = ValidateEntryName(entry);
                if (!seen.Add(relative))
                    throw new InvalidDataException("更新包包含重复路径。");
                totalExpanded = checked(totalExpanded + entry.Length);
                if (totalExpanded > 4L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("更新包展开后超出允许大小。");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    if (entry.Length != 0) throw new InvalidDataException("更新包目录项包含数据。");
                    continue;
                }

                var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
                var target = string.Equals(normalized, "MuYingDrive.NativeWpf.exe", StringComparison.OrdinalIgnoreCase)
                    ? appPath
                    : string.Equals(normalized, "Updater/MuYingDrive.Updater.exe", StringComparison.OrdinalIgnoreCase)
                        ? updaterPath : null;
                if (target == appPath) hasApp = true;
                if (target == updaterPath) hasUpdater = true;

                await using var input = entry.Open();
                await using var output = target is null ? null : new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                var buffer = new byte[128 * 1024];
                long entryBytes = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    entryBytes = checked(entryBytes + read);
                    if (entryBytes > entry.Length)
                        throw new InvalidDataException("更新包文件长度与目录信息不一致。");
                    if (output is not null)
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (entryBytes != entry.Length)
                    throw new InvalidDataException("更新包文件长度与目录信息不一致。");
                if (output is not null)
                {
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
            }

            if (!hasApp || !hasUpdater)
                throw new InvalidDataException("更新包缺少客户端或独立更新程序。");
            VerifyAuthenticodePublisher(appPath, publisherThumbprint);
            VerifyAuthenticodePublisher(updaterPath, publisherThumbprint);
            VerifyExecutableVersion(appPath, manifest.Version);
            VerifyExecutableVersion(updaterPath, manifest.Version);
        }
        finally
        {
            try
            {
                if (File.Exists(appPath)) File.Delete(appPath);
                if (File.Exists(updaterPath)) File.Delete(updaterPath);
                if (Directory.Exists(updaterDirectory)) Directory.Delete(updaterDirectory, recursive: false);
                if (Directory.Exists(verificationDirectory)) Directory.Delete(verificationDirectory, recursive: false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void VerifyAuthenticodePublisher(string filePath, string expectedSha256Thumbprint)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("更新包只能在 Windows 上验证 Authenticode 签名。");
        if (!File.Exists(filePath))
            throw new InvalidDataException("更新包缺少必需的应用程序文件。");

        var file = new WinTrustFileInfo(filePath);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(file, filePointer, false);
        var action = WinTrustActionGenericVerifyV2;
        var data = new WinTrustData { UnionChoice = 1, FileInfo = filePointer, StateAction = 1, UiChoice = 2, ProviderFlags = 0x00000080 };
        try
        {
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            if (result != 0)
                throw new CryptographicException("更新程序的 Authenticode 签名验证失败。");
        }
        finally
        {
            data.StateAction = 2;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }

        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
        var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(thumbprint), Convert.FromHexString(expectedSha256Thumbprint)))
            throw new CryptographicException("更新程序签名者与受信任发布者不一致。");
    }

    private static void VerifyExecutableVersion(string filePath, string expectedVersion)
    {
        var productVersion = FileVersionInfo.GetVersionInfo(filePath).ProductVersion;
        if (productVersion is null || productVersion.Split('+', 2)[0] != expectedVersion)
            throw new InvalidDataException("安装包程序版本与签名发布清单不一致。");
    }

    private static string ValidateEntryName(ZipArchiveEntry entry)
    {
        var attributes = entry.ExternalAttributes;
        var unixMode = (attributes >> 16) & 0xF000;
        if (unixMode == 0xA000 || (attributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新包包含链接或重解析文件。");

        var normalized = entry.FullName.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0'))
            throw new InvalidDataException("更新包包含无效路径。");
        var isDirectory = normalized.EndsWith('/');
        var segments = normalized.Split('/');
        if (isDirectory) segments = segments[..^1];
        if (segments.Length == 0 || segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
                segment.Any(char.IsControl) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedWindowsName(segment)))
            throw new InvalidDataException("更新包包含不安全路径。");
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9');
    }

    private static string ResolveUnderRoot(string root, string relative)
    {
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包路径超出暂存目录。");
        return result;
    }

    private static void EnsureNoReparsePoints(string root, string directory)
    {
        var current = directory;
        while (current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新暂存路径包含重解析目录。");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
        public WinTrustFileInfo(string filePath) { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(); FilePath = filePath; FileHandle = IntPtr.Zero; KnownSubject = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
        public WinTrustData() { Size = (uint)Marshal.SizeOf<WinTrustData>(); PolicyCallbackData = SipClientData = FileInfo = StateData = UrlReference = SignatureSettings = IntPtr.Zero; UiChoice = RevocationChecks = UnionChoice = StateAction = ProviderFlags = UiContext = 0; }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);
}

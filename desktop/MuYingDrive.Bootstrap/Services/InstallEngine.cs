using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.Bootstrap.Services;

/// <summary>
/// A single progress step for the setup window.
/// </summary>
/// <param name="Percent">
/// How far the current stage has got, or <c>null</c> when the stage cannot report one.
/// Only the download can: extraction reads a ZIP the runtime does not narrate. A null
/// here keeps the bar indeterminate rather than showing a fabricated percentage.
/// </param>
public sealed record InstallProgress(string Title, string Explanation, string Status, double? Percent = null);

/// <summary>Outcome of an install or uninstall, including the path to launch on success.</summary>
public sealed record InstallResult(bool Succeeded, string Title, string Explanation, string? LaunchPath = null);

/// <summary>
/// First-install bootstrap for the native client.
/// </summary>
/// <remarks>
/// The client bundle is NOT embedded in this executable. It is fetched from the NAS and
/// verified against the signed release manifest before anything is written, using the
/// same trust path the updater uses. Embedding the payload made the setup a ~300 MB
/// single-file executable that hung during host startup, and it shipped an artifact
/// nothing could authenticate. Every path here is written under %LOCALAPPDATA% or HKCU
/// and the manifest requests asInvoker, so installation never prompts for elevation.
/// </remarks>
public sealed class InstallEngine
{
    private const string DefaultConfigResource = "MuYingDrive.Bootstrap.Payload.nas-client.json";
    private const string ClientExecutableName = "MuYingDrive.NativeWpf.exe";
    private const string UpdaterExecutableRelativePath = "Updater/MuYingDrive.Updater.exe";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MuYingDrive";
    private const string ShortcutFileName = "沐影盘.lnk";

    public static string InstallRoot => UpdatePathPolicy.InstallRoot;

    public static string BootstrapperDirectory => Path.Combine(UpdatePathPolicy.UserDataRoot, "Bootstrapper");

    public static string StageRoot => Path.Combine(UpdatePathPolicy.UserDataRoot, "InstallerStage");

    /// <summary>
    /// Where the previous install is parked during an upgrade. Deliberately NOT
    /// <c>Programs\MuYingDrive.previous-*</c>: that naming belongs to
    /// <c>InstallTransaction</c>, which reasons about those paths during recovery, and a
    /// foreign directory there could be mistaken for an interrupted update.
    /// </summary>
    public static string BackupRoot => Path.Combine(UpdatePathPolicy.UserDataRoot, "InstallerBackups");

    /// <summary>True when a client is already installed in the gated per-user root.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(InstallRoot, ClientExecutableName));

    public static async Task<InstallResult> InstallAsync(
        IProgress<InstallProgress> progress,
        bool createDesktopShortcut,
        string? configOverridePath,
        bool forceConfig,
        string? localPackagePath,
        string? localManifestPath,
        CancellationToken cancellationToken)
    {
        var configuration = LoadConfiguration(configOverridePath);

        string? workDirectory = null;
        IDisposable? guard = null;
        try
        {
            progress.Report(new InstallProgress("正在安装沐影盘", "正在准备安装，请稍候。", "准备中"));

            // The locks are per Windows user rather than per install directory, so any
            // running client blocks this; report the wait instead of appearing to hang.
            var waitingReported = false;
            guard = await UpdateExecutionGuard.AcquireUpdaterLockAsync(cancellationToken, () =>
            {
                if (waitingReported) return;
                waitingReported = true;
                progress.Report(new InstallProgress(
                    "正在安装沐影盘", "请先关闭正在运行的沐影盘，安装会自动继续。", "等待沐影盘退出"));
            }).ConfigureAwait(false);
            await UpdateExecutionGuard.WaitForClientProcessesExitAsync(InstallRoot, cancellationToken)
                .ConfigureAwait(false);

            workDirectory = Path.Combine(StageRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            UpdatePathPolicy.EnsureNoReparsePointComponents(workDirectory);

            var (packagePath, manifest) = await ObtainPackageAsync(
                configuration, workDirectory, localPackagePath, localManifestPath, progress, cancellationToken)
                .ConfigureAwait(false);

            // Verifies the package SHA-256 against the signed manifest, rejects unsafe ZIP
            // entries, and asserts the executables report the manifest version.
            progress.Report(new InstallProgress("正在安装沐影盘", "正在校验并解压客户端。", "正在校验"));
            var extracted = Path.Combine(workDirectory, "client");
            await PackageIntegrity.ExtractVerifiedPackageAsync(packagePath, extracted, manifest, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            VerifyStagedClient(extracted);

            var installParent = Path.GetDirectoryName(InstallRoot)
                ?? throw new InvalidOperationException("无法确定安装目录。");
            Directory.CreateDirectory(installParent);
            UpdatePathPolicy.EnsureNoReparsePointComponents(installParent);

            var replaced = false;
            if (Directory.Exists(InstallRoot))
            {
                if (!IsInstalled)
                {
                    // Something else owns this path. Refuse rather than delete it.
                    throw new InvalidOperationException(
                        $"安装目录已存在但不是沐影盘程序：{InstallRoot}。请先手动处理该目录。");
                }

                progress.Report(new InstallProgress("正在安装沐影盘", "正在切换程序版本。", "正在切换版本"));
                var backup = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMddHHmmssfff"));
                Directory.CreateDirectory(BackupRoot);
                Directory.Move(InstallRoot, backup);
                PruneBackups();
                replaced = true;
            }

            try
            {
                Directory.Move(extracted, InstallRoot);
            }
            catch
            {
                if (replaced && !Directory.Exists(InstallRoot))
                {
                    // Restore the previous version so a failed upgrade does not leave the
                    // machine without a working client.
                    var newest = NewestBackup();
                    if (newest is not null) Directory.Move(newest, InstallRoot);
                }
                throw;
            }

            progress.Report(new InstallProgress("正在安装沐影盘", "正在写入客户端配置。", "正在配置"));
            SeedConfiguration(configuration, configOverridePath, forceConfig);

            progress.Report(new InstallProgress("正在安装沐影盘", "正在创建快捷方式。", "正在创建快捷方式"));
            CreateShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ShortcutFileName));
            if (createDesktopShortcut)
            {
                CreateShortcut(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName));
            }

            CopyBootstrapper();
            WriteUninstallEntry(manifest.Version);

            return new InstallResult(true, "安装完成",
                $"沐影盘 {manifest.Version} 已安装完成，可以开始使用。",
                Path.Combine(InstallRoot, ClientExecutableName));
        }
        finally
        {
            guard?.Dispose();
            TryDeleteDirectory(workDirectory);
        }
    }

    public static async Task<InstallResult> UninstallAsync(bool purge, CancellationToken cancellationToken)
    {
        IDisposable? guard = null;
        try
        {
            guard = await UpdateExecutionGuard.AcquireUpdaterLockAsync(cancellationToken).ConfigureAwait(false);
            await UpdateExecutionGuard.WaitForClientProcessesExitAsync(InstallRoot, cancellationToken)
                .ConfigureAwait(false);

            TryDeleteDirectory(InstallRoot);
            // Cached update packages are program data, not user data.
            TryDeleteDirectory(UpdatePathPolicy.UpdateRoot);
            if (purge) TryDeleteDirectory(UpdatePathPolicy.UserDataRoot);

            RemoveShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ShortcutFileName));
            RemoveShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName));
            RemoveUninstallEntry();

            return new InstallResult(true, "已卸载沐影盘", purge
                ? "程序与本机配置均已删除。"
                : "程序已删除；登录凭据与客户端配置已保留。");
        }
        finally
        {
            guard?.Dispose();
        }
    }

    /// <summary>
    /// Returns a verified package plus its signed manifest, either from local files (for
    /// an offline install) or fetched from the NAS over the pinned channel.
    /// </summary>
    private static async Task<(string PackagePath, UpdateManifest Manifest)> ObtainPackageAsync(
        ClientConfiguration configuration,
        string workDirectory,
        string? localPackagePath,
        string? localManifestPath,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(localPackagePath) || !string.IsNullOrWhiteSpace(localManifestPath))
        {
            if (string.IsNullOrWhiteSpace(localPackagePath) || string.IsNullOrWhiteSpace(localManifestPath))
                throw new InvalidOperationException("离线安装必须同时提供 --package 与 --manifest。");
            if (!File.Exists(localPackagePath!))
                throw new FileNotFoundException("指定的安装包不存在。", localPackagePath);
            if (!File.Exists(localManifestPath!))
                throw new FileNotFoundException("指定的签名清单不存在。", localManifestPath);

            // The local manifest still has to carry a valid signature: an unsigned file on
            // disk must never become a way to install an unverified build.
            var verifiedLocal = UpdateManifestVerifier.Verify(File.ReadAllBytes(localManifestPath!));
            return (localPackagePath!, verifiedLocal.Value);
        }

        using var downloader = new ReleaseDownloader(configuration.ApiBaseUri, configuration.CertificateSha256);
        progress.Report(new InstallProgress("正在安装沐影盘", "正在获取最新版本信息。", "正在检查更新"));

        VerifiedUpdateManifest verified;
        try
        {
            verified = await downloader.GetStableManifestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ReleaseDownloadException or System.Security.Cryptography.CryptographicException
            or InvalidDataException)
        {
            throw new InvalidOperationException($"无法获取客户端安装包：{exception.Message}", exception);
        }

        var manifest = verified.Value;
        progress.Report(new InstallProgress("正在安装沐影盘",
            $"正在下载沐影盘 {manifest.Version}。", "正在下载", Percent: 0));

        var partialPath = Path.Combine(workDirectory, "package.partial");
        string packagePath;
        try
        {
            packagePath = await downloader.DownloadPackageAsync(
                    verified, partialPath,
                    new DownloadReporter(progress, manifest.PackageSize, manifest.Version),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ReleaseDownloadException or InvalidDataException)
        {
            // Without this the raw downloader text reaches the window unwrapped, and the
            // download stage reads differently from the manifest stage directly above it.
            throw new InvalidOperationException($"无法获取客户端安装包：{exception.Message}", exception);
        }

        return (packagePath, manifest);
    }

    /// <summary>
    /// Turns raw byte counts into the setup window's progress, throttled to whole
    /// percentages so a 142 MB download does not flood the dispatcher with updates.
    /// </summary>
    private sealed class DownloadReporter(IProgress<InstallProgress> progress, long totalBytes, string version)
        : IProgress<long>
    {
        private int _lastPercent = -1;

        public void Report(long received)
        {
            if (totalBytes <= 0) return;
            // 100L, not 100: received * 100 overflows Int32 on anything above ~21 MB.
            var percent = (int)Math.Clamp(received * 100L / totalBytes, 0L, 100L);
            if (percent == _lastPercent) return;
            _lastPercent = percent;
            progress.Report(new InstallProgress(
                "正在安装沐影盘",
                $"正在下载沐影盘 {version}（{Mebibytes(received)} / {Mebibytes(totalBytes)} MB）。",
                "正在下载",
                percent));
        }

        private static string Mebibytes(long bytes) => (bytes / 1048576.0).ToString("F1");
    }

    private static void VerifyStagedClient(string extracted)
    {
        var client = Path.Combine(extracted, ClientExecutableName);
        var updater = Path.Combine(extracted, UpdaterExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var transferAgent = Path.Combine(extracted, "transfer", "MuYingDrive.TransferAgent.exe");
        if (!File.Exists(client)) throw new InvalidDataException("客户端安装包缺少主程序。");
        if (!File.Exists(updater)) throw new InvalidDataException("客户端安装包缺少更新程序。");
        if (!File.Exists(transferAgent)) throw new InvalidDataException("客户端安装包缺少 Rust 传输代理。");
    }

    /// <summary>
    /// Seeds the client configuration on a first install, and refreshes the connection
    /// fields of an existing one on a reinstall.
    /// </summary>
    /// <remarks>
    /// The split is deliberate. <c>CredentialProfile</c> selects which Credential Manager
    /// entry holds the app session token, so it must survive a reinstall untouched. The
    /// address and pin are the one connection setting a new installer carries that the
    /// update channel cannot deliver, because <c>nas-client.json</c> is not part of any
    /// update package. A rotated server certificate that never reaches this file leaves
    /// every installed client pinning the old one, with no way out but a reinstall.
    /// <see cref="ClientConfigurationMerge"/> decides which fields those are.
    /// </remarks>
    private static void SeedConfiguration(ClientConfiguration configuration, string? configOverridePath, bool forceConfig)
    {
        var destination = UpdatePathPolicy.ClientSettingsPath;
        if (File.Exists(destination) && !forceConfig && TryRefreshConfiguration(destination, configuration)) return;
        // Falling through on failure is deliberate: a file that cannot be parsed supplies
        // no credential profile either, so overwriting it repairs the machine rather than
        // leaving a client that rejects its own configuration at every start.

        var content = !string.IsNullOrWhiteSpace(configOverridePath)
            ? File.ReadAllBytes(configOverridePath)
            : ReadResource(DefaultConfigResource);
        if (content is null) return;

        WriteConfigurationAtomically(destination, content);
    }

    /// <summary>
    /// Brings an existing configuration's connection fields up to date. Returns false when
    /// the file is unreadable or is not a JSON object, which sends the caller down the
    /// overwrite path.
    /// </summary>
    private static bool TryRefreshConfiguration(string destination, ClientConfiguration configuration)
    {
        string existing;
        try
        {
            existing = File.ReadAllText(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!ClientConfigurationMerge.TryRefreshConnectionFields(
                existing, configuration.ApiBaseUri.AbsoluteUri, configuration.CertificateSha256,
                out var updated))
        {
            return false;
        }

        // Identical text means this installer carries the address and pin the machine
        // already has — the ordinary reinstall — so the file is left exactly as it was.
        if (!string.Equals(updated, existing, StringComparison.Ordinal))
        {
            WriteConfigurationAtomically(destination, Encoding.UTF8.GetBytes(updated));
        }

        return true;
    }

    private static void WriteConfigurationAtomically(string destination, byte[] content)
    {
        Directory.CreateDirectory(UpdatePathPolicy.SettingsDirectory);
        UpdatePathPolicy.EnsureNoReparsePointComponents(UpdatePathPolicy.SettingsDirectory);
        var temporary = destination + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, destination, overwrite: true);
    }

    private static ClientConfiguration LoadConfiguration(string? configOverridePath)
    {
        byte[]? content = null;
        if (!string.IsNullOrWhiteSpace(configOverridePath))
        {
            if (!File.Exists(configOverridePath))
                throw new FileNotFoundException("指定的客户端配置不存在。", configOverridePath);
            content = File.ReadAllBytes(configOverridePath);
        }
        else
        {
            content = ReadResource(DefaultConfigResource);
        }

        if (content is null)
        {
            throw new InvalidOperationException(
                "此安装程序未内置 NAS 配置。请使用 --config 指定 nas-client.json，或从官方分发包重新获取。");
        }

        ClientConfigurationDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ClientConfigurationDto>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("客户端配置格式无效。", exception);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.ApiBaseUri) ||
            !Uri.TryCreate(dto.ApiBaseUri, UriKind.Absolute, out var api) ||
            api.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(dto.CertificateSha256))
        {
            throw new InvalidOperationException("客户端配置缺少必要的 NAS 参数。");
        }

        // Validated here so a malformed pin cannot ship inside an installer.
        _ = CertificatePin.ParseSha256Pin(dto.CertificateSha256);
        return new ClientConfiguration(api, dto.CertificateSha256,
            string.IsNullOrWhiteSpace(dto.CredentialProfile) ? "default" : dto.CredentialProfile);
    }

    private static void CreateShortcut(string shortcutPath)
    {
        var target = Path.Combine(InstallRoot, ClientExecutableName);
        // WScript.Shell is part of Windows; using it avoids a third-party dependency.
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建快捷方式：系统缺少 WScript.Shell。");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建快捷方式。");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = InstallRoot;
        shortcut.Description = "沐影盘";
        shortcut.IconLocation = target;
        shortcut.Save();
    }

    private static void RemoveShortcut(string shortcutPath) => TryDeleteFile(shortcutPath);

    /// <summary>
    /// Copies the setup executable next to the user data so the uninstall entry keeps
    /// working after an update replaces the install directory.
    /// </summary>
    private static void CopyBootstrapper()
    {
        var source = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
        var destination = Path.Combine(BootstrapperDirectory, "MuYingDrive.Setup.exe");
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return;

        Directory.CreateDirectory(BootstrapperDirectory);
        File.Copy(source, destination, overwrite: true);
    }

    private static void WriteUninstallEntry(string version)
    {
        var setupPath = Path.Combine(BootstrapperDirectory, "MuYingDrive.Setup.exe");
        var uninstallExecutable = File.Exists(setupPath) ? setupPath : Environment.ProcessPath ?? setupPath;

        // HKCU only: a per-user install must not need administrator rights to remove.
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, writable: true);
        if (key is null) return;
        key.SetValue("DisplayName", "沐影盘");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "MuYingDrive");
        key.SetValue("InstallLocation", InstallRoot);
        key.SetValue("UninstallString", $"\"{uninstallExecutable}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateInstallSize(), RegistryValueKind.DWord);
    }

    private static void RemoveUninstallEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int EstimateInstallSize()
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(InstallRoot, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
            return (int)Math.Min(total / 1024, int.MaxValue);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void PruneBackups()
    {
        try
        {
            // Keep the newest backup so a failed upgrade still has something to restore.
            foreach (var directory in Directory.EnumerateDirectories(BackupRoot)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).Skip(1))
            {
                TryDeleteDirectory(directory);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? NewestBackup()
    {
        try
        {
            return Directory.EnumerateDirectories(BackupRoot)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Shape of the client's nas-client.json, validated before use.</summary>
    private sealed record ClientConfigurationDto
    {
        public string? ApiBaseUri { get; init; }
        public string? CertificateSha256 { get; init; }
        public string? CredentialProfile { get; init; }
    }

    /// <summary>The validated configuration the installer downloads with and seeds.</summary>
    private sealed record ClientConfiguration(
        Uri ApiBaseUri, string CertificateSha256, string CredentialProfile);
}

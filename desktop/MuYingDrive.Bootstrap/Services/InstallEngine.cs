using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.Bootstrap.Services;

/// <summary>A single progress step for the setup window.</summary>
public sealed record InstallProgress(string Title, string Explanation, string Status);

/// <summary>Outcome of an install or uninstall, including the path to launch on success.</summary>
public sealed record InstallResult(bool Succeeded, string Title, string Explanation, string? LaunchPath = null);

/// <summary>
/// First-install bootstrap for the native client. Deploys the embedded self-contained
/// payload into the per-user install root the updater is gated on, seeds the NAS
/// configuration, and registers an uninstaller.
/// </summary>
/// <remarks>
/// Every path is written under %LOCALAPPDATA% or HKCU, and the manifest requests
/// asInvoker, so installation never prompts for elevation. User data that must survive
/// an update — the Credential Manager entries and
/// <c>%LOCALAPPDATA%\MuYingDrive\Settings\nas-client.json</c> — lives outside the
/// install root and is never rewritten here.
/// </remarks>
public sealed class InstallEngine
{
    private const string PayloadResource = "MuYingDrive.Bootstrap.Payload.client-payload.zip";
    private const string SettingsResource = "MuYingDrive.Bootstrap.Payload.nas-client.json";
    private const string ClientExecutableName = "MuYingDrive.NativeWpf.exe";
    private const string ClientProcessName = "MuYingDrive.NativeWpf";
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
        CancellationToken cancellationToken)
    {
        var payload = ReadResource(PayloadResource)
            ?? throw new InvalidOperationException("此安装程序不包含安装载荷，请从官方分发包重新获取。");

        string? stage = null;
        IDisposable? guard = null;
        try
        {
            progress.Report(new InstallProgress("正在安装沐影盘", "正在准备安装，请稍候。", "准备中"));

            // Take the same exclusive locks the updater uses so a running client blocks
            // the install instead of being replaced underneath itself.
            guard = await UpdateExecutionGuard.AcquireUpdaterLockAsync(cancellationToken).ConfigureAwait(false);
            await UpdateExecutionGuard.WaitForClientProcessesExitAsync(InstallRoot, cancellationToken)
                .ConfigureAwait(false);

            var installParent = Path.GetDirectoryName(InstallRoot)
                ?? throw new InvalidOperationException("无法确定安装目录。");
            Directory.CreateDirectory(installParent);
            UpdatePathPolicy.EnsureNoReparsePointComponents(installParent);

            progress.Report(new InstallProgress("正在安装沐影盘", "正在解压程序文件。", "正在准备文件"));
            stage = Path.Combine(StageRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            ExtractPayload(payload, stage);

            progress.Report(new InstallProgress("正在安装沐影盘", "正在校验程序文件。", "正在校验"));
            VerifyStagedClient(stage);

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
                Directory.Move(stage, InstallRoot);
                stage = null;
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
            SeedConfiguration(payload, configOverridePath, forceConfig);

            progress.Report(new InstallProgress("正在安装沐影盘", "正在创建快捷方式。", "正在创建快捷方式"));
            CreateStartMenuShortcut();
            if (createDesktopShortcut) CreateDesktopShortcut();

            CopyBootstrapper();

            var version = CurrentVersion();
            WriteUninstallEntry(version);

            return new InstallResult(true, "安装完成", "沐影盘已安装完成，可以开始使用。",
                Path.Combine(InstallRoot, ClientExecutableName));
        }
        finally
        {
            guard?.Dispose();
            TryDeleteDirectory(stage);
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
            // The cached update packages are program data, not user data.
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
    /// Extracts the embedded payload, applying the same hostile-archive defenses the
    /// updater uses: <see cref="PackageIntegrity.ValidateEntryName"/> rejects traversal,
    /// absolute paths, links and reserved names, and each expanded path is re-checked
    /// to stay under the stage directory.
    /// </summary>
    private static void ExtractPayload(byte[] payload, string stageDirectory)
    {
        using var archive = new ZipArchive(new MemoryStream(payload, writable: false), ZipArchiveMode.Read);
        var root = Path.GetFullPath(stageDirectory).TrimEnd(Path.DirectorySeparatorChar);
        long totalExpanded = 0;

        foreach (var entry in archive.Entries)
        {
            var relative = PackageIntegrity.ValidateEntryName(entry);
            totalExpanded = checked(totalExpanded + entry.Length);
            if (totalExpanded > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("安装载荷展开后超出允许大小。");

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装载荷包含越界路径。");

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static void VerifyStagedClient(string stageDirectory)
    {
        var client = Path.Combine(stageDirectory, ClientExecutableName);
        var updater = Path.Combine(stageDirectory, UpdaterExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(client)) throw new InvalidDataException("安装载荷缺少主程序。");
        if (!File.Exists(updater)) throw new InvalidDataException("安装载荷缺少更新程序。");

        // The installer and the client are published together, so a mismatch means the
        // payload was assembled from a different build. The updater compares versions
        // strictly, and the health marker is matched as a string.
        var expected = CurrentVersion();
        foreach (var executable in new[] { client, updater })
        {
            var actual = VersionStamp.NormalizeProductVersion(
                FileVersionInfo.GetVersionInfo(executable).ProductVersion);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"安装载荷版本不一致：期望 {expected}，实际 {actual ?? "(无法读取)"}。");
        }
    }

    /// <summary>
    /// Writes the NAS configuration only when the client has none.
    /// </summary>
    /// <remarks>
    /// <c>nas-client.json</c> carries the <c>CredentialProfile</c> that selects which
    /// Windows Credential Manager entries hold the session token and SMB password.
    /// Overwriting an existing file could point the client at a different profile and
    /// silently drop the user's stored login, forcing them to sign in again. It is
    /// therefore written only when missing, unless the operator forces it.
    /// </remarks>
    private static void SeedConfiguration(byte[] payloadArchive, string? configOverridePath, bool forceConfig)
    {
        var destination = UpdatePathPolicy.ClientSettingsPath;
        if (File.Exists(destination) && !forceConfig) return;

        byte[]? content = null;
        if (!string.IsNullOrWhiteSpace(configOverridePath))
        {
            if (!File.Exists(configOverridePath))
                throw new FileNotFoundException("指定的客户端配置不存在。", configOverridePath);
            content = File.ReadAllBytes(configOverridePath);
        }
        else
        {
            content = ReadResource(SettingsResource);
        }

        if (content is null)
        {
            // No baked configuration and no override: leave the machine unconfigured so
            // the client shows its "contact your administrator" message instead of
            // starting with placeholder values that would fail at every request.
            return;
        }

        // Fail here rather than shipping a file the client will reject at startup.
        using (JsonDocument.Parse(content))
        {
        }

        Directory.CreateDirectory(UpdatePathPolicy.SettingsDirectory);
        UpdatePathPolicy.EnsureNoReparsePointComponents(UpdatePathPolicy.SettingsDirectory);
        var temporary = destination + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, destination, overwrite: true);
    }

    private static void CreateStartMenuShortcut() =>
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ShortcutFileName));

    private static void CreateDesktopShortcut() =>
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName));

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
        var uninstallExecutable = File.Exists(setupPath)
            ? setupPath
            : Environment.ProcessPath ?? setupPath;

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

    private static string CurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static void PruneBackups()
    {
        try
        {
            // Keep the newest backup so a failed upgrade still has something to restore.
            var backups = Directory.EnumerateDirectories(BackupRoot)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .ToArray();
            foreach (var directory in backups) TryDeleteDirectory(directory);
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
}

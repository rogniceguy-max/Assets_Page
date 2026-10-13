using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.Updater.Services;

public sealed class InstallTransaction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<UpdateRunResult> ApplyAsync(UpdateHandoffRequest request, string stageDirectory,
        UpdateManifest manifest, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var installRoot = UpdatePathPolicy.NormalizeAndRequireInstallRoot(request.InstallRoot);
        var parent = Path.GetDirectoryName(installRoot)!;
        var id = request.UpdateId.ToLowerInvariant();
        var backup = installRoot + $".previous-{id}";
        var failed = installRoot + $".failed-{id}";
        var healthMarker = Path.Combine(UpdatePathPolicy.UpdateRoot, "health", $"{id}.ok");
        var journal = Path.Combine(UpdatePathPolicy.UpdateRoot, "transactions", $"{id}.json");
        var appExecutable = Path.Combine(installRoot, "MuYingDrive.NativeWpf.exe");
        var movedOld = false;
        var installedNew = false;

        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(stageDirectory)), parent, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("更新暂存目录与安装目录不在同一卷。");
        if (!Directory.Exists(stageDirectory) || File.Exists(installRoot) || File.Exists(backup) || Directory.Exists(backup))
            throw new IOException("安装目录状态不符合安全更新条件。");
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("安装目录不能位于重解析路径中。");
        if ((File.GetAttributes(stageDirectory) & FileAttributes.ReparsePoint) != 0 ||
            Directory.Exists(installRoot) && (File.GetAttributes(installRoot) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("更新或安装路径不能包含重解析目录。");

        PreserveNasClientSettings(installRoot);
        var fromVersion = ResolveFromVersion(request, installRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        Directory.CreateDirectory(Path.GetDirectoryName(healthMarker)!);
        if (File.Exists(healthMarker)) File.Delete(healthMarker);
        WriteJournal(journal, new TransactionJournal(id, manifest.Version, "prepared"));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(installRoot, backup);
            movedOld = true;
            WriteJournal(journal, new TransactionJournal(id, manifest.Version, "old-moved"));
            Directory.Move(stageDirectory, installRoot);
            installedNew = true;
            WriteJournal(journal, new TransactionJournal(id, manifest.Version, "new-installed"));

            progress.Report(new UpdateProgress("正在完成更新", "正在启动新版本并确认可以正常打开。旧版本会保留到确认完成。", "检查新版本启动", Cancellable: false));
            var start = new ProcessStartInfo(appExecutable) { UseShellExecute = false, WorkingDirectory = installRoot };
            start.ArgumentList.Add("--update-health-check");
            start.ArgumentList.Add(id);
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("The update health-check process did not start.");
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or System.Security.SecurityException)
            {
                var report = CreateFailureReport(id, fromVersion, manifest.Version, "health_check", "HEALTH_CHECK_PROCESS_START_FAILED");
                RollBackAfterHealthFailure(installRoot, backup, failed, movedOld, installedNew, journal, id, manifest.Version,
                    fromVersion);
                return new UpdateRunResult(false, "更新未完成", "新版本未能启动，旧版本已自动恢复，可以继续使用。",
                    Path.Combine(installRoot, "MuYingDrive.NativeWpf.exe"), report);
            }

            using (process)
            {
                string? healthFailureCode;
                try
                {
                    healthFailureCode = await WaitForHealthAsync(process, healthMarker, id, manifest.Version, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    healthFailureCode = "HEALTH_CHECK_MARKER_READ_FAILED";
                }

                if (healthFailureCode is null)
                {
                    WriteJournal(journal, new TransactionJournal(id, manifest.Version, "healthy"));
                    var failureContext = UpdateFailureReporter.IsValidVersion(fromVersion)
                        ? new UpdateFailureContext(id, fromVersion!, manifest.Version)
                        : null;
                    return new UpdateRunResult(true, "更新完成", $"已安装 {manifest.Version}，旧版本已保留以便恢复。", appExecutable,
                        FailureContext: failureContext);
                }

                if (!process.HasExited && !await StopHealthCheckProcessAsync(process).ConfigureAwait(false))
                {
                    var report = CreateFailureReport(id, fromVersion, manifest.Version, "health_check", "HEALTH_CHECK_PROCESS_STOP_FAILED");
                    try { WriteJournal(journal, new TransactionJournal(id, manifest.Version, "recovery-needed")); }
                    catch (Exception) { }
                    throw new UpdateRecoveryRequiredException("新版本启动检查没有结束。新旧版本文件均已保留，沐影盘暂未切换到新版本。")
                    {
                        FailureReport = report
                    };
                }

                var failureReport = CreateFailureReport(id, fromVersion, manifest.Version, "health_check", healthFailureCode);
                RollBackAfterHealthFailure(installRoot, backup, failed, movedOld, installedNew, journal, id, manifest.Version,
                    fromVersion);
                return new UpdateRunResult(false, "更新未完成", "新版本未能通过启动确认，旧版本已自动恢复，可以继续使用。",
                    Path.Combine(installRoot, "MuYingDrive.NativeWpf.exe"), failureReport);
            }
        }
        catch (UpdateRecoveryRequiredException) { throw; }
        catch
        {
            try { RollBack(installRoot, backup, failed, movedOld, installedNew, journal, id, manifest.Version); }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
            {
                try { WriteJournal(journal, new TransactionJournal(id, manifest.Version, "recovery-needed")); }
                catch (Exception) { }
                var report = CreateFailureReport(id, ResolveFromVersion(request, installRoot), manifest.Version,
                    "rollback", "ROLLBACK_FAILED");
                throw new UpdateRecoveryRequiredException("自动恢复未完成，无法确认旧版本是否可用。新旧版本文件均已保留，请联系管理员处理。", rollbackException)
                {
                    FailureReport = report
                };
            }
            throw;
        }
    }

    private static async Task<string?> WaitForHealthAsync(Process process, string markerPath, string id, string version,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(markerPath))
            {
                var contents = await File.ReadAllLinesAsync(markerPath, cancellationToken).ConfigureAwait(false);
                if (contents.Length < 2 || contents[0] != id || contents[1] != version)
                    return "HEALTH_CHECK_MARKER_MISMATCH";
                using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return "HEALTH_CHECK_EXIT_TIMEOUT"; }
                return process.ExitCode == 0 ? null : "HEALTH_CHECK_PROCESS_EXITED";
            }
            if (process.HasExited)
                return process.ExitCode == 0 ? "HEALTH_CHECK_MARKER_MISSING" : "HEALTH_CHECK_PROCESS_EXITED";
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return "HEALTH_CHECK_TIMEOUT";
    }

    private static async Task<bool> StopHealthCheckProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.HasExited;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or OperationCanceledException)
        {
            return false;
        }
    }

    private static void RollBackAfterHealthFailure(string installRoot, string backup, string failed, bool movedOld,
        bool installedNew, string journal, string id, string version, string? fromVersion)
    {
        try
        {
            RollBack(installRoot, backup, failed, movedOld, installedNew, journal, id, version);
        }
        catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { WriteJournal(journal, new TransactionJournal(id, version, "recovery-needed")); }
            catch (Exception) { }
            var rollbackReport = CreateFailureReport(id, fromVersion, version, "rollback", "ROLLBACK_FAILED");
            throw new UpdateRecoveryRequiredException("自动恢复未完成，无法确认旧版本是否可用。新旧版本文件均已保留，请联系管理员处理。", rollbackException)
            {
                FailureReport = rollbackReport
            };
        }
    }

    private static UpdateFailureReport? CreateFailureReport(string updateId, string? fromVersion, string targetVersion,
        string stage, string errorCode) => UpdateFailureReporter.IsValidVersion(fromVersion)
        ? UpdateFailureReport.Create(updateId, fromVersion!, targetVersion, stage, errorCode)
        : null;

    private static string? ResolveFromVersion(UpdateHandoffRequest request, string installRoot)
    {
        if (UpdateFailureReporter.IsValidVersion(request.CurrentVersion)) return request.CurrentVersion!;
        try
        {
            var filePath = Path.Combine(installRoot, "MuYingDrive.NativeWpf.exe");
            var value = FileVersionInfo.GetVersionInfo(filePath).ProductVersion ?? FileVersionInfo.GetVersionInfo(filePath).FileVersion;
            if (value is not null)
            {
                var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(3).ToArray();
                var normalized = parts.Length == 3 ? string.Join('.', parts) : string.Empty;
                if (UpdateFailureReporter.IsValidVersion(normalized)) return normalized;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        return null;
    }

    private static void RollBack(string installRoot, string backup, string failed, bool movedOld, bool installedNew,
        string journal, string id, string version)
    {
        if (movedOld && !Directory.Exists(backup))
            throw new IOException("旧版本备份不存在，拒绝移动当前安装文件。");
        if (installedNew && Directory.Exists(installRoot))
        {
            if (Directory.Exists(failed)) throw new IOException("旧更新事务的失败文件仍存在，拒绝覆盖。");
            Directory.Move(installRoot, failed);
        }
        if (movedOld && !Directory.Exists(installRoot) && Directory.Exists(backup))
            Directory.Move(backup, installRoot);
        WriteJournal(journal, new TransactionJournal(id, version, "rolled-back"));
    }

    public static bool RecoverInterruptedTransactions()
    {
        var directory = Path.Combine(UpdatePathPolicy.UpdateRoot, "transactions");
        var recoveryPending = false;
        if (Directory.Exists(directory))
        {
            UpdatePathPolicy.EnsureNoReparsePointComponents(directory);
            foreach (var journalPath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                TransactionJournal? journal;
                try
                {
                    var info = new FileInfo(journalPath);
                    if (info.Length > 4096 || (File.GetAttributes(journalPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        recoveryPending = true;
                        continue;
                    }
                    journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllBytes(journalPath), JsonOptions);
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                    recoveryPending = true;
                    continue;
                }
                if (journal is null || string.IsNullOrWhiteSpace(journal.UpdateId) || journal.UpdateId.Length != 32 ||
                    !journal.UpdateId.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(journal.Version) ||
                    string.IsNullOrWhiteSpace(journal.State) ||
                    journal.State is not ("prepared" or "old-moved" or "new-installed" or "healthy" or "rolled-back" or "recovery-needed") ||
                    !string.Equals(Path.GetFileName(journalPath), journal.UpdateId + ".json", StringComparison.OrdinalIgnoreCase))
                {
                    recoveryPending = true;
                    continue;
                }

                var installRoot = UpdatePathPolicy.InstallRoot;
                var backup = installRoot + $".previous-{journal.UpdateId}";
                var failed = installRoot + $".failed-{journal.UpdateId}";
                if (journal.State is "healthy" or "rolled-back") continue;
                if (journal.State == "old-moved" && Directory.Exists(installRoot))
                    journal = journal with { State = "new-installed" };
                if (journal.State is "prepared" or "old-moved" && !Directory.Exists(installRoot))
                {
                    if (Directory.Exists(backup))
                    {
                        Directory.Move(backup, installRoot);
                        WriteJournal(journalPath, journal with { State = "rolled-back" });
                    }
                    else
                        recoveryPending = true;
                    continue;
                }
                if (journal.State is "new-installed" or "recovery-needed")
                {
                    // Only ApplyAsync may mark a transaction healthy, after the health process
                    // has exited successfully. A marker alone cannot prove its exit status.
                    if (IsClientRunning(installRoot))
                    {
                        recoveryPending = true;
                        continue;
                    }
                    if (!Directory.Exists(backup))
                    {
                        recoveryPending = true;
                        continue;
                    }
                    if (Directory.Exists(installRoot))
                    {
                        if (Directory.Exists(failed))
                        {
                            recoveryPending = true;
                            continue;
                        }
                        Directory.Move(installRoot, failed);
                    }
                    Directory.Move(backup, installRoot);
                    WriteJournal(journalPath, journal with { State = "rolled-back" });
                }
            }
        }

        var installRootPath = UpdatePathPolicy.InstallRoot;
        if (!Directory.Exists(installRootPath)) recoveryPending = true;
        var programsDirectory = Path.GetDirectoryName(installRootPath)!;
        if (Directory.Exists(programsDirectory))
        {
            var backupDirectories = Directory.EnumerateDirectories(programsDirectory, "MuYingDrive.previous-*", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: path, Prefix: "MuYingDrive.previous-"))
                .Concat(Directory.EnumerateDirectories(programsDirectory, "MuYingDrive.failed-*", SearchOption.TopDirectoryOnly)
                    .Select(path => (Path: path, Prefix: "MuYingDrive.failed-")));
            foreach (var entry in backupDirectories)
            {
                var id = Path.GetFileName(entry.Path)[entry.Prefix.Length..];
                if (id.Length != 32 || !id.All(Uri.IsHexDigit) ||
                    (File.GetAttributes(entry.Path) & FileAttributes.ReparsePoint) != 0)
                {
                    recoveryPending = true;
                    continue;
                }

                var journalPath = Path.Combine(directory, id + ".json");
                try
                {
                    UpdatePathPolicy.EnsureNoReparsePointComponents(journalPath);
                    var info = new FileInfo(journalPath);
                    var journal = !info.Exists || info.Length > 4096
                        ? null
                        : JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllBytes(journalPath), JsonOptions);
                    var expectedState = entry.Prefix == "MuYingDrive.previous-" ? "healthy" : "rolled-back";
                    if (journal is null || !string.Equals(journal.UpdateId, id, StringComparison.OrdinalIgnoreCase) || journal.State != expectedState)
                        recoveryPending = true;
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                    recoveryPending = true;
                }
            }
        }
        return recoveryPending;
    }

    private static bool IsClientRunning(string installRoot)
    {
        foreach (var process in Process.GetProcessesByName("MuYingDrive.NativeWpf"))
        {
            using (process)
            {
                try
                {
                    var file = process.MainModule?.FileName;
                    if (file is null || string.Equals(Path.GetFullPath(file), Path.Combine(installRoot, "MuYingDrive.NativeWpf.exe"), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void WriteJournal(string path, TransactionJournal journal)
    {
        UpdatePathPolicy.EnsureNoReparsePointComponents(path);
        journal = journal with { UpdatedUtc = DateTimeOffset.UtcNow };
        var temporary = path + ".tmp";
        UpdatePathPolicy.EnsureNoReparsePointComponents(temporary);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        var eventPath = path + ".events.jsonl";
        UpdatePathPolicy.EnsureNoReparsePointComponents(eventPath);
        using (var events = new FileStream(eventPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                   4096, FileOptions.WriteThrough))
        {
            events.Write(bytes);
            events.WriteByte((byte)'\n');
            events.Flush(flushToDisk: true);
        }
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void PreserveNasClientSettings(string installRoot)
    {
        var legacyPath = Path.Combine(installRoot, "nas-client.json");
        var stablePath = UpdatePathPolicy.ClientSettingsPath;
        UpdatePathPolicy.EnsureNoReparsePointComponents(legacyPath);
        UpdatePathPolicy.EnsureNoReparsePointComponents(stablePath);
        if (File.Exists(stablePath)) return;
        if (!File.Exists(legacyPath)) return;
        var legacyInfo = new FileInfo(legacyPath);
        if (legacyInfo.Length is <= 0 or > 64 * 1024)
            throw new InvalidDataException("客户端服务配置大小无效；为保留旧安装，本次更新已停止。");

        var bytes = File.ReadAllBytes(legacyPath);
        Directory.CreateDirectory(UpdatePathPolicy.SettingsDirectory);
        UpdatePathPolicy.EnsureNoReparsePointComponents(UpdatePathPolicy.SettingsDirectory);
        var temporary = stablePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, stablePath, overwrite: false); }
            catch (IOException) when (File.Exists(stablePath)) { }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record TransactionJournal(
        [property: JsonPropertyName("update_id")] string UpdateId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("updated_utc")] DateTimeOffset UpdatedUtc = default);
}

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.Updater.Services;

public sealed record UpdateProgress(string Title, string Explanation, string Status, long? Completed = null,
    long? Total = null, bool Cancellable = true);
public sealed record UpdateRunResult(bool Succeeded, string Title, string Explanation, string? LaunchPath = null,
    UpdateFailureReport? FailureReport = null, UpdateFailureContext? FailureContext = null);

public sealed class UpdateRunner
{
    public async Task<UpdateRunResult> RunAsync(string requestPath, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        UpdateHandoffRequest request;
        IDisposable? executionLock = null;
        string? stageDirectory = null;
        string? stageUpdateId = null;
        try
        {
            request = UpdatePathPolicy.LoadAndValidateRequest(requestPath);
            progress.Report(new UpdateProgress("正在准备更新", "正在等待沐影盘完成安全退出。", "等待应用退出"));
            await WaitForClientExitAsync(request, cancellationToken).ConfigureAwait(false);
            await WaitForAllClientProcessesExitAsync(request.InstallRoot, cancellationToken).ConfigureAwait(false);
            executionLock = await UpdateExecutionGuard.AcquireUpdaterLockAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (InstallTransaction.RecoverInterruptedTransactions())
                    throw CreateRecoveryRequired(request,
                        "上一次更新仍需要处理，当前版本是否可用尚未确认。请联系管理员恢复后再重试。");
            }
            catch (UpdateRecoveryRequiredException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw CreateRecoveryRequired(request,
                    "无法安全恢复上一次更新。当前版本是否可用尚未确认，请联系管理员处理。", exception);
            }

            progress.Report(new UpdateProgress("正在验证更新文件", "正在检查发布签名、文件完整性和可信发布者。", "验证签名清单与安装包"));
            var envelope = await File.ReadAllBytesAsync(request.ManifestPath, cancellationToken).ConfigureAwait(false);
            var manifest = UpdateManifestVerifier.Verify(envelope);
            if (!string.Equals(Path.GetFileName(request.PackagePath), "package.zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包缓存路径无效。");
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(request.PackagePath)), manifest.Value.PackageId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包标识与签名清单不一致。");
            if (!UpdateManifestVerifier.IsNewer(manifest.Value.Version, GetInstalledVersion(request.InstallRoot)))
                throw new InvalidDataException("签名清单中的版本不高于当前安装版本。");

            var programsDirectory = Path.GetDirectoryName(UpdatePathPolicy.InstallRoot)!;
            Directory.CreateDirectory(programsDirectory);
            if ((File.GetAttributes(programsDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("安装目录不能位于重解析路径中。");
            stageDirectory = Path.Combine(programsDirectory, $"MuYingDrive.stage-{request.UpdateId}");
            stageUpdateId = request.UpdateId;
            DeleteStagingDirectoryIfSafe(stageDirectory, request.UpdateId);
            long totalBytes;
            using (var archive = ZipFile.OpenRead(request.PackagePath))
                totalBytes = archive.Entries.Aggregate(0L, (total, entry) => checked(total + entry.Length));
            if (totalBytes > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("更新包展开后超出允许大小。");

            long lastReportedBytes = 0;
            var extractProgress = new InlineProgress<long>(completed =>
            {
                if (completed - lastReportedBytes < 1024 * 1024 && completed != totalBytes) return;
                lastReportedBytes = completed;
                progress.Report(new UpdateProgress("正在安装新版本", "正在准备已经验证的应用文件。", "写入安装文件", completed, totalBytes));
            });
            await PackageIntegrity.ExtractVerifiedPackageAsync(request.PackagePath, stageDirectory, manifest.Value,
                extractProgress, cancellationToken).ConfigureAwait(false);

            await WaitForAllClientProcessesExitAsync(request.InstallRoot, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new UpdateProgress("正在安装新版本", "正在安全切换应用文件并保留旧版本。", "切换版本", Cancellable: false));
            var transaction = new InstallTransaction();
            var result = await transaction.ApplyAsync(request, stageDirectory, manifest.Value, progress, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateRunResult(false, "更新已取消", "更新文件尚未应用，当前版本仍保留。可以重新启动沐影盘。",
                ExistingClientPath());
        }
        catch (UpdateRecoveryRequiredException exception)
        {
            return new UpdateRunResult(false, "更新需要处理", exception.Message, FailureReport: exception.FailureReport);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Security.Cryptography.CryptographicException or InvalidOperationException or ArgumentException)
        {
            return new UpdateRunResult(false, "更新未完成", SafeErrorMessage(exception), ExistingClientPath());
        }
        catch (Exception)
        {
            return new UpdateRunResult(false, "更新未完成", "更新过程遇到问题。可重新启动沐影盘；必要时请联系管理员。", ExistingClientPath());
        }
        finally
        {
            executionLock?.Dispose();
            if (stageDirectory is not null && stageUpdateId is not null)
            {
                try { DeleteStagingDirectoryIfSafe(stageDirectory, stageUpdateId); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }
    }

    private static async Task WaitForClientExitAsync(UpdateHandoffRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(request.ClientProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != request.ClientProcessStartUtcTicks)
                return;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The original process has already exited, or Windows reused its process id.
        }
        catch (InvalidOperationException)
        {
            // The process handle is no longer available; the file transaction still rechecks paths.
        }
    }

    private static async Task WaitForAllClientProcessesExitAsync(string installRoot, CancellationToken cancellationToken)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(installRoot, "MuYingDrive.NativeWpf.exe"));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = false;
            foreach (var process in Process.GetProcessesByName("MuYingDrive.NativeWpf"))
            {
                using (process)
                {
                    try
                    {
                        var processPath = process.MainModule?.FileName;
                        if (processPath is null)
                            throw new IOException("无法确认沐影盘进程的安装路径。");
                        if (!string.Equals(Path.GetFullPath(processPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                            continue;
                        active = true;
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception exception)
                    {
                        throw new IOException("无法确认其他沐影盘进程是否已退出。", exception);
                    }
                }
            }
            if (!active) return;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetInstalledVersion(string installRoot)
    {
        var app = Path.Combine(installRoot, "MuYingDrive.NativeWpf.exe");
        if (!File.Exists(app)) throw new FileNotFoundException("当前安装中缺少沐影盘主程序。");
        var fileVersion = FileVersionInfo.GetVersionInfo(app).ProductVersion ?? FileVersionInfo.GetVersionInfo(app).FileVersion;
        if (string.IsNullOrWhiteSpace(fileVersion)) throw new InvalidDataException("无法读取当前安装版本。");
        var components = fileVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(3).ToArray();
        if (components.Length != 3 || !components.All(x => int.TryParse(x, out _)))
            throw new InvalidDataException("当前安装版本格式无效。");
        return string.Join('.', components);
    }

    private static string? ExistingClientPath()
    {
        var path = Path.Combine(UpdatePathPolicy.InstallRoot, "MuYingDrive.NativeWpf.exe");
        return File.Exists(path) ? path : null;
    }

    private static UpdateRecoveryRequiredException CreateRecoveryRequired(UpdateHandoffRequest request, string message,
        Exception? innerException = null)
    {
        try
        {
            var manifestBytes = File.ReadAllBytes(request.ManifestPath);
            var targetVersion = UpdateManifestVerifier.Verify(manifestBytes).Value.Version;
            var fromVersion = UpdateFailureReporter.IsValidVersion(request.CurrentVersion) ? request.CurrentVersion : null;
            if (fromVersion is null)
            {
                try { fromVersion = GetInstalledVersion(request.InstallRoot); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            return new UpdateRecoveryRequiredException(message, innerException)
            {
                FailureReport = fromVersion is null ? null : UpdateFailureReport.Create(request.UpdateId, fromVersion,
                    targetVersion, "recovery", "INTERRUPTED_RECOVERY_REQUIRED")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            return new UpdateRecoveryRequiredException(message, innerException);
        }
    }

    private static void DeleteStagingDirectoryIfSafe(string path, string updateId)
    {
        var expectedParent = Path.GetFullPath(Path.GetDirectoryName(UpdatePathPolicy.InstallRoot)!);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(fullPath), $"MuYingDrive.stage-{updateId}", StringComparison.OrdinalIgnoreCase) ||
            updateId.Length != 32 || !updateId.All(Uri.IsHexDigit))
            throw new UnauthorizedAccessException("更新暂存目录路径无效。");
        if (!Directory.Exists(fullPath)) return;
        UpdatePathPolicy.EnsureNoReparsePointComponents(fullPath);
        var pending = new Stack<string>();
        pending.Push(fullPath);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("更新暂存目录包含重解析文件。");
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
        Directory.Delete(fullPath, recursive: true);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string SafeErrorMessage(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "当前用户没有足够的安装目录权限。旧版本文件未被主动删除。",
        CryptographicException => "更新签名或文件完整性验证失败。旧版本仍保留。",
        InvalidDataException => "更新文件格式或版本信息无效。旧版本仍保留。",
        IOException => "更新文件无法安全读取或写入。请重试；如果无法启动沐影盘，请联系管理员。",
        _ => "更新过程遇到问题。旧版本仍保留；可查看更新日志或联系管理员。"
    };
}

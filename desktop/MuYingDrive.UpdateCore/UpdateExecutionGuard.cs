using System.Diagnostics;

namespace MuYingDrive.UpdateCore;

/// <summary>
/// Serializes client startup and update installation across processes for this user.
/// Normal clients hold a shared read/write handle on client.lock. The updater takes
/// an exclusive updater mutex and an exclusive client.lock handle. Its health-check
/// process is launched in a dedicated mode that does not acquire the client lock.
/// </summary>
public static class UpdateExecutionGuard
{
    private static string ClientLockPath
    {
        get
        {
            Directory.CreateDirectory(UpdatePathPolicy.UserDataRoot);
            var path = Path.Combine(UpdatePathPolicy.UserDataRoot, "client.lock");
            UpdatePathPolicy.EnsureNoReparsePointComponents(path);
            return path;
        }
    }

    private static string UpdaterLockPath
    {
        get
        {
            Directory.CreateDirectory(UpdatePathPolicy.UserDataRoot);
            var path = Path.Combine(UpdatePathPolicy.UserDataRoot, "updater.lock");
            UpdatePathPolicy.EnsureNoReparsePointComponents(path);
            return path;
        }
    }

    public static FileStream AcquireClientLock()
    {
        return new FileStream(ClientLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite, 1, FileOptions.None);
    }

    public static async Task<UpdaterGuard> AcquireUpdaterLockAsync(CancellationToken cancellationToken)
    {
        FileStream updaterMutex;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                updaterMutex = new FileStream(UpdaterLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
                break;
            }
            catch (IOException)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    FileStream clientGuard;
                    try
                    {
                        clientGuard = new FileStream(ClientLockPath, FileMode.Open, FileAccess.ReadWrite,
                            FileShare.None, 1, FileOptions.None);
                    }
                    catch (FileNotFoundException)
                    {
                        clientGuard = new FileStream(ClientLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                            FileShare.None, 1, FileOptions.None);
                    }
                    return new UpdaterGuard(updaterMutex, clientGuard);
                }
                catch (IOException)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            updaterMutex.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits until no process running the client from <paramref name="installRoot"/> remains.
    /// </summary>
    /// <remarks>
    /// The updater needs this before swapping the install directory, and the first-install
    /// bootstrap needs it before replacing an existing install. Both must hold the
    /// exclusive locks from <see cref="AcquireUpdaterLockAsync"/> first, so no new client
    /// can start while this waits.
    /// </remarks>
    public static async Task WaitForClientProcessesExitAsync(string installRoot, CancellationToken cancellationToken)
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

    public sealed class UpdaterGuard(FileStream updaterMutex, FileStream clientGuard) : IDisposable
    {
        public void Dispose()
        {
            try { clientGuard.Dispose(); }
            finally { updaterMutex.Dispose(); }
        }
    }
}

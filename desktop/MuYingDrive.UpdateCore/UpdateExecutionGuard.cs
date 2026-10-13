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

    public sealed class UpdaterGuard(FileStream updaterMutex, FileStream clientGuard) : IDisposable
    {
        public void Dispose()
        {
            try { clientGuard.Dispose(); }
            finally { updaterMutex.Dispose(); }
        }
    }
}

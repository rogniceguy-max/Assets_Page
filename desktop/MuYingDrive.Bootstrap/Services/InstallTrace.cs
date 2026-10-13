using MuYingDrive.UpdateCore;

namespace MuYingDrive.Bootstrap.Services;

/// <summary>
/// Append-only trace for the setup executable.
/// </summary>
/// <remarks>
/// A bootstrap that fails or hangs leaves no window and no console, so the only way to
/// diagnose a field report is a file it writes as it goes. Writes are best-effort: a
/// trace failure must never break the install.
/// </remarks>
public static class InstallTrace
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(UpdatePathPolicy.UserDataRoot);
                File.AppendAllText(
                    Path.Combine(UpdatePathPolicy.UserDataRoot, "installer-progress.log"),
                    $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fffZ}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.NativeWpf.Services;

public sealed record PreparedUpdate(VerifiedUpdateManifest Manifest, string ManifestPath, string PackagePath);

public enum UpdateCheckState
{
    Idle,
    Checking,
    Downloading,
    Ready,
    Failed
}

/// <summary>Checks the signed stable channel and pre-downloads only verified packages.</summary>
public sealed class UpdateCoordinator : IAsyncDisposable
{
    private static readonly Regex PackageIdPattern = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly V2ApiClient _api;
    private readonly string _currentVersion;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private Task? _loop;
    private PreparedUpdate? _readyUpdate;
    private UpdateCheckState _state;

    /// <summary>
    /// The ETag of the manifest seen last, so the next poll can ask the server to skip
    /// the body. Deliberately not persisted: one extra 0.5 KB request per launch is
    /// cheaper than another reparse-guarded write path on startup.
    /// </summary>
    private string? _manifestEntityTag;

    public UpdateCoordinator(V2ApiClientOptions apiOptions, string currentVersion)
    {
        _api = new V2ApiClient(apiOptions);
        _currentVersion = currentVersion;
    }

    public event EventHandler? Changed;

    public UpdateCheckState State => _state;
    public PreparedUpdate? ReadyUpdate => _readyUpdate;
    public bool IsReady => _readyUpdate is not null;

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await RestoreCachedReadyUpdateAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            // The first check runs immediately — someone who was just handed a fix should
            // not have to wait an interval to see it. Only the periodic wait is jittered.
            await CheckAndPrepareAsync(cancellationToken).ConfigureAwait(false);
            var delay = UpdateCheckSchedule.NextDelay(
                UpdateCheckSchedule.DefaultCheckInterval, Random.Shared.NextDouble());
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task CheckAndPrepareAsync(CancellationToken cancellationToken)
    {
        if (!IsSupportedInstallLocation())
            return;
        PruneUpdateHistory();
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(UpdateCheckState.Checking);
            var manifest = await _api.GetStableUpdateManifestAsync(_manifestEntityTag, cancellationToken)
                .ConfigureAwait(false);
            _manifestEntityTag = manifest.EntityTag;
            if (manifest.NotModified)
            {
                // The manifest is byte-identical to the one already judged not newer, so
                // that judgement still holds and there is nothing to verify or download.
                SetState(_readyUpdate is null ? UpdateCheckState.Idle : UpdateCheckState.Ready);
                return;
            }

            var envelopeBytes = manifest.EnvelopeBytes
                ?? throw new V2ApiException("更新服务器返回了空的更新信息。");
            var verified = UpdateManifestVerifier.Verify(envelopeBytes);
            if (!UpdateManifestVerifier.IsNewer(verified.Value.Version, _currentVersion))
            {
                SetState(_readyUpdate is null ? UpdateCheckState.Idle : UpdateCheckState.Ready);
                return;
            }

            var cacheDirectory = UpdatePathPolicy.GetPackageCacheDirectory(verified.Value.PackageId);
            UpdatePathPolicy.EnsureNoReparsePointComponents(cacheDirectory);
            Directory.CreateDirectory(cacheDirectory);
            UpdatePathPolicy.EnsureNoReparsePointComponents(cacheDirectory);
            var manifestPath = Path.Combine(cacheDirectory, "manifest.json");
            var packagePath = Path.Combine(cacheDirectory, "package.zip");
            var partialPath = Path.Combine(cacheDirectory, "package.partial");
            UpdatePathPolicy.EnsureNoReparsePointComponents(cacheDirectory);
            UpdatePathPolicy.EnsureNoReparsePointComponents(manifestPath);
            UpdatePathPolicy.EnsureNoReparsePointComponents(packagePath);
            UpdatePathPolicy.EnsureNoReparsePointComponents(partialPath);
            WriteAtomically(manifestPath, envelopeBytes);

            var packageIsReady = false;
            var alreadyValidated = _readyUpdate is not null &&
                string.Equals(_readyUpdate.Manifest.Value.PackageId, verified.Value.PackageId, StringComparison.Ordinal) &&
                _readyUpdate.Manifest.EnvelopeBytes.AsSpan().SequenceEqual(envelopeBytes);
            if (File.Exists(packagePath))
            {
                try
                {
                    await PackageIntegrity.VerifySha256Async(packagePath, verified.Value, cancellationToken).ConfigureAwait(false);
                    packageIsReady = true;
                }
                catch (Exception exception) when (exception is IOException or CryptographicException or InvalidDataException)
                {
                    File.Delete(packagePath);
                }
            }

            if (!packageIsReady)
            {
                SetState(UpdateCheckState.Downloading);
                if (File.Exists(partialPath) && new FileInfo(partialPath).Length == verified.Value.PackageSize)
                {
                    try { await PackageIntegrity.VerifySha256Async(partialPath, verified.Value, cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or CryptographicException or InvalidDataException) { File.Delete(partialPath); }
                }
                if (!File.Exists(partialPath) || new FileInfo(partialPath).Length != verified.Value.PackageSize)
                    await _api.DownloadUpdatePackageAsync(verified.Value.PackageId, verified.Value.Sha256,
                        verified.Value.PackageSize, partialPath, cancellationToken: cancellationToken).ConfigureAwait(false);
                await PackageIntegrity.VerifySha256Async(partialPath, verified.Value, cancellationToken).ConfigureAwait(false);
                File.Move(partialPath, packagePath, overwrite: true);
            }

            await PackageIntegrity.VerifySha256Async(packagePath, verified.Value, cancellationToken).ConfigureAwait(false);
            if (!alreadyValidated)
                await PackageIntegrity.ValidatePackageForReadyAsync(packagePath, verified.Value, cancellationToken).ConfigureAwait(false);
            _readyUpdate = new PreparedUpdate(verified, manifestPath, packagePath);
            PruneSupersededCache();
            SetState(UpdateCheckState.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Net.Http.HttpRequestException or V2ApiException or System.Security.Cryptography.CryptographicException or JsonException)
        {
            // An unavailable NAS, malformed manifest or missing release trust anchor must never block login.
            SetState(UpdateCheckState.Failed);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task RestoreCachedReadyUpdateAsync(CancellationToken cancellationToken)
    {
        if (!IsSupportedInstallLocation() || !Directory.Exists(UpdatePathPolicy.UpdateRoot))
            return;

        PreparedUpdate? newest = null;
        try
        {
            UpdatePathPolicy.EnsureNoReparsePointComponents(UpdatePathPolicy.UpdateRoot);
            foreach (var directory in Directory.EnumerateDirectories(UpdatePathPolicy.UpdateRoot, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageId = Path.GetFileName(directory);
                if (!PackageIdPattern.IsMatch(packageId)) continue;
                try
                {
                    UpdatePathPolicy.EnsureNoReparsePointComponents(directory);
                    var manifestPath = Path.Combine(directory, "manifest.json");
                    var packagePath = Path.Combine(directory, "package.zip");
                    UpdatePathPolicy.EnsureNoReparsePointComponents(manifestPath);
                    UpdatePathPolicy.EnsureNoReparsePointComponents(packagePath);
                    var manifestInfo = new FileInfo(manifestPath);
                    if (!manifestInfo.Exists || manifestInfo.Length is <= 0 or > 64 * 1024) continue;
                    var envelope = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                    var verified = UpdateManifestVerifier.Verify(envelope);
                    if (!string.Equals(verified.Value.PackageId, packageId, StringComparison.Ordinal) ||
                        !UpdateManifestVerifier.IsNewer(verified.Value.Version, _currentVersion))
                        continue;
                    await PackageIntegrity.ValidatePackageForReadyAsync(packagePath, verified.Value, cancellationToken).ConfigureAwait(false);
                    if (newest is null || verified.ParsedVersion > newest.Manifest.ParsedVersion)
                        newest = new PreparedUpdate(verified, manifestPath, packagePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                    System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
                {
                    // Ignore a stale or incomplete cache; only a fresh full verification can restore Ready.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        if (newest is not null)
        {
            _readyUpdate = newest;
            PruneSupersededCache();
            SetState(UpdateCheckState.Ready);
        }
    }

    /// <summary>
    /// Reclaims the disk that finished updates left behind.
    /// </summary>
    /// <remarks>
    /// Each completed update leaves the install directory it replaced
    /// (<c>Programs\MuYingDrive.previous-&lt;id&gt;</c>, ~317 MB) and the updater it ran
    /// from (<c>Updates\runner\&lt;id&gt;</c>, ~154 MB), and neither was ever removed —
    /// 4.6 GB after eight updates on one machine.
    ///
    /// It is done here rather than in the updater because the updater runs from its own
    /// runner directory, and Windows will not let a running executable delete the
    /// directory holding it. The client is never inside either one.
    ///
    /// Failures are swallowed on purpose. This reclaims disk space; no update, and no
    /// login, should ever fail because housekeeping could not run.
    /// </remarks>
    private void PruneUpdateHistory()
    {
        try
        {
            var programsDirectory = Path.GetDirectoryName(UpdatePathPolicy.InstallRoot);
            if (string.IsNullOrEmpty(programsDirectory)) return;

            var result = UpdateHistoryPruner.Prune(programsDirectory, UpdatePathPolicy.UpdateRoot);
            if (result.InstallBackupsDeleted == 0 && result.RunnerDirectoriesDeleted == 0) return;

            Trace($"pruned {result.InstallBackupsDeleted} install backup(s) and " +
                  $"{result.RunnerDirectoriesDeleted} runner director(y|ies), " +
                  $"{result.BytesReclaimed / (1024 * 1024)} MB reclaimed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Appends a line to the update housekeeping log.
    /// </summary>
    /// <remarks>
    /// Best effort, like the setup executable's trace: this deletes hundreds of megabytes
    /// at a time, so there has to be a record of when and how much, but a trace failure
    /// must never break the cleanup it is describing.
    /// </remarks>
    private static void Trace(string message)
    {
        try
        {
            Directory.CreateDirectory(UpdatePathPolicy.UserDataRoot);
            File.AppendAllText(Path.Combine(UpdatePathPolicy.UserDataRoot, "update-cleanup.log"),
                $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fffZ}  {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Deletes cached packages other than the one currently ready to install.
    /// </summary>
    /// <remarks>
    /// Every cached package is roughly 140 MB. When a release supersedes one the user
    /// never installed — which is the normal case for anyone who leaves the client open
    /// across a publish — the older download is dead weight for the life of the install.
    /// Only directories named as package ids are considered, so the runner, health and
    /// transaction directories under the same root are never touched.
    /// </remarks>
    private void PruneSupersededCache()
    {
        if (_readyUpdate is null) return;
        var keep = Path.GetDirectoryName(_readyUpdate.PackagePath);
        if (string.IsNullOrEmpty(keep)) return;

        try
        {
            if (!Directory.Exists(UpdatePathPolicy.UpdateRoot)) return;
            foreach (var directory in Directory.EnumerateDirectories(UpdatePathPolicy.UpdateRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (!PackageIdPattern.IsMatch(Path.GetFileName(directory))) continue;
                if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string GetCurrentVersion()
    {
        var versionText = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);
        return versionText is not null && Version.TryParse(versionText, out _) ? versionText : "1.0.0";
    }

    public static bool IsSupportedInstallLocation()
    {
        try
        {
            var current = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var expected = Path.GetFullPath(UpdatePathPolicy.InstallRoot).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(current, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void SetState(UpdateCheckState state)
    {
        if (_state == state) return;
        _state = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void WriteAtomically(string destination, byte[] content)
    {
        var temporary = destination + ".tmp";
        UpdatePathPolicy.EnsureNoReparsePointComponents(destination);
        UpdatePathPolicy.EnsureNoReparsePointComponents(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination, overwrite: true);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _api.Dispose();
        _checkGate.Dispose();
        _stop.Dispose();
    }
}

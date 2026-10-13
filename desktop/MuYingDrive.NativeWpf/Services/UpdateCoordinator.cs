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
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(8);
    private static readonly Regex PackageIdPattern = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly V2ApiClient _api;
    private readonly string _currentVersion;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private Task? _loop;
    private PreparedUpdate? _readyUpdate;
    private UpdateCheckState _state;

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
            await CheckAndPrepareAsync(cancellationToken).ConfigureAwait(false);
            try { await Task.Delay(CheckInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task CheckAndPrepareAsync(CancellationToken cancellationToken)
    {
        if (!IsSupportedInstallLocation())
            return;
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(UpdateCheckState.Checking);
            var envelopeBytes = await _api.GetStableUpdateManifestAsync(cancellationToken).ConfigureAwait(false);
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
            SetState(UpdateCheckState.Ready);
        }
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

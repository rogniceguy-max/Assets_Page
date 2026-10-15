using System.Security.Cryptography;
using System.Text;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.ViewModels;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Configures local episode roots for the Rust agent; WPF never watches NAS paths.</summary>
public sealed class EpisodeSyncService : ObservableObject, IDisposable
{
    private readonly TransferViewModel _transfers;
    private readonly List<TransferAgentSyncRoot> _roots = [];
    private string _statusText = "未开启同步";
    private bool _isSyncing;
    private bool _isWatching;
    private bool _disposed;

    public EpisodeSyncService(TransferViewModel transfers)
    {
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _transfers.TaskUpdated += HandleTaskUpdated;
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsSyncing { get => _isSyncing; private set => SetProperty(ref _isSyncing, value); }
    public bool IsWatching { get => _isWatching; private set => SetProperty(ref _isWatching, value); }

    public bool Configure(string projectId, string projectName, IReadOnlyList<SyncEpisodeItem> episodes,
        string localDirectory)
    {
        try
        {
            ValidateSegment(projectName, "项目名称");
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(localDirectory))
                throw new InvalidOperationException("同步配置不完整。");
            var selected = episodes?.Where(item => item.IsSelected).ToArray() ?? [];
            if (selected.Length == 0) throw new InvalidOperationException("请至少勾选一个集数。");
            var projectRoot = ResolveLocalProjectRoot(localDirectory, projectName);
            var built = new List<TransferAgentSyncRoot>(selected.Length);
            foreach (var episode in selected)
            {
                ValidateSegment(episode.Name, "集数名称");
                var episodeRoot = Path.GetFullPath(Path.Combine(projectRoot, episode.Name));
                EnsureInside(projectRoot, episodeRoot);
                Directory.CreateDirectory(episodeRoot);
                built.Add(new TransferAgentSyncRoot(
                    StableRootId(projectId, episode.Name, episodeRoot),
                    episodeRoot,
                    $"project:{projectId}",
                    projectId,
                    episode.Name));
            }
            _roots.Clear();
            _roots.AddRange(built);
            StatusText = $"已配置 {_roots.Count} 个本地同步目录";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusText = $"同步配置无效：{exception.Message}";
            return false;
        }
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_roots.Count == 0) { StatusText = "请先配置项目和集数。"; return false; }
        try
        {
            await _transfers.ConfigureSyncRootsAsync(_roots, cancellationToken).ConfigureAwait(false);
            await _transfers.ResumeShutdownPausedSyncTasksAsync(
                _roots.Select(root => root.RootId), cancellationToken).ConfigureAwait(false);
            IsWatching = true;
            StatusText = $"正在监听 {_roots.Count} 个本地目录；首次扫描已进入后台队列";
            foreach (var root in _roots)
                await _transfers.RescanSyncRootAsync(root.RootId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TransferAgentException or TimeoutException)
        {
            IsWatching = false;
            StatusText = $"无法启动本地同步代理：{exception.Message}";
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try { await _transfers.StopWatchingAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or TransferAgentException or TimeoutException)
        { StatusText = $"停止监听时代理暂不可用：{exception.Message}"; }
        IsWatching = false;
        if (!StatusText.StartsWith("停止监听", StringComparison.Ordinal)) StatusText = "未开启同步；已排队任务会继续或等待下次登录";
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (_roots.Count == 0) { StatusText = "请先配置项目和集数。"; return; }
        IsSyncing = true;
        try
        {
            if (!IsWatching) await _transfers.ConfigureSyncRootsAsync(_roots, cancellationToken).ConfigureAwait(false);
            await _transfers.ResumeShutdownPausedSyncTasksAsync(
                _roots.Select(root => root.RootId), cancellationToken).ConfigureAwait(false);
            foreach (var root in _roots)
                await _transfers.RescanSyncRootAsync(root.RootId, cancellationToken).ConfigureAwait(false);
            StatusText = "本地目录扫描已交给 Rust 代理；文件会在稳定后进入传输队列";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TransferAgentException or TimeoutException)
        {
            StatusText = $"同步扫描未能启动：{exception.Message}";
        }
        finally
        {
            IsSyncing = false;
            if (!IsWatching)
            {
                try { await _transfers.StopWatchingAsync(cancellationToken).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private void HandleTaskUpdated(TransferAgentTaskSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Source, "sync", StringComparison.OrdinalIgnoreCase)) return;
        if (snapshot.State is "failed" or "conflict") StatusText = snapshot.Message ?? "同步中有文件需要处理";
        else if (snapshot.State == "completed") StatusText = "同步文件已完成";
    }

    private static void ValidateSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || value.Length > 180)
            throw new InvalidOperationException($"{label}不能作为目录名。");
    }

    private static void EnsureInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本地同步目录超出所选根目录。");
    }

    private static string ResolveLocalProjectRoot(string localDirectory, string projectName)
    {
        var full = Path.GetFullPath(localDirectory);
        return new DirectoryInfo(full).Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)
            ? full : Path.Combine(full, projectName);
    }

    private static string StableRootId(string projectId, string episodeName, string localRoot)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot))
            .Normalize(NormalizationForm.FormC).ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(projectId + "\n" +
            episodeName.Normalize(NormalizationForm.FormC) + "\n" + normalizedPath));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transfers.TaskUpdated -= HandleTaskUpdated;
    }
}

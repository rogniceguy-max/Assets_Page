using System.IO;
using System.Text.RegularExpressions;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.ProjectWorkbooks;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// Service managing two-way synchronization between NAS project storage and local computer
/// for video editing workflows.
/// </summary>
public sealed class EpisodeSyncService : ObservableObject, IDisposable
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".m4v", ".wmv"
    };

    private readonly IProjectWorkbookService _service;
    private readonly string _shareRoot;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly HashSet<string> _suppressedDeletes = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isInternalSyncing;

    private FileSystemWatcher? _watcher;
    private Timer? _periodicTimer;
    private string _statusText = "未开启同步";
    private bool _isSyncing;
    private bool _isWatching;
    private string _currentProjectName = string.Empty;
    private string _currentLocalRoot = string.Empty;
    private List<SyncEpisodeItem> _currentEpisodes = [];

    public EpisodeSyncService(IProjectWorkbookService service, string shareRoot)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _shareRoot = shareRoot ?? string.Empty;
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        private set => SetProperty(ref _isSyncing, value);
    }

    public bool IsWatching
    {
        get => _isWatching;
        private set => SetProperty(ref _isWatching, value);
    }

    /// <summary>
    /// Starts background continuous watching and synchronization.
    /// </summary>
    public void Start(string projectName, IReadOnlyList<SyncEpisodeItem> episodes, string localDirectory)
    {
        Stop();

        try
        {
            _currentProjectName = projectName;
            _currentEpisodes = episodes.Where(e => e.IsSelected).ToList();
            _currentLocalRoot = ResolveLocalProjectRoot(localDirectory, projectName);

            if (string.IsNullOrWhiteSpace(_currentProjectName) || _currentEpisodes.Count == 0 || string.IsNullOrWhiteSpace(_currentLocalRoot))
            {
                StatusText = "未开启同步";
                return;
            }

            Directory.CreateDirectory(_currentLocalRoot);

            _watcher = new FileSystemWatcher(_currentLocalRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };

            _watcher.Created += OnLocalFileCreatedOrChanged;
            _watcher.Changed += OnLocalFileCreatedOrChanged;
            _watcher.Deleted += OnLocalFileDeleted;
            _watcher.Renamed += OnLocalFileRenamed;
            _watcher.EnableRaisingEvents = true;

            IsWatching = true;

            // Trigger immediate sync
            _ = SyncNowAsync();

            // Check NAS updates periodically every 6 seconds
            _periodicTimer = new Timer(_ =>
            {
                try
                {
                    if (IsWatching && !IsSyncing)
                    {
                        _ = SyncNowAsync(silent: true);
                    }
                }
                catch
                {
                }
            }, null, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            StatusText = $"启动同步失败: {ex.Message}";
            Stop();
        }
    }

    /// <summary>
    /// Stops continuous watching and synchronization.
    /// </summary>
    public void Stop()
    {
        IsWatching = false;
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnLocalFileCreatedOrChanged;
                _watcher.Changed -= OnLocalFileCreatedOrChanged;
                _watcher.Deleted -= OnLocalFileDeleted;
                _watcher.Renamed -= OnLocalFileRenamed;
                _watcher.Dispose();
                _watcher = null;
            }

            _periodicTimer?.Dispose();
            _periodicTimer = null;
        }
        catch
        {
        }
        finally
        {
            StatusText = "未开启同步";
        }
    }

    /// <summary>
    /// Runs a full synchronization cycle between NAS and local workspace.
    /// </summary>
    public async Task SyncNowAsync(bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(_currentProjectName) || _currentEpisodes.Count == 0 || string.IsNullOrWhiteSpace(_currentLocalRoot))
            return;

        if (!await _syncLock.WaitAsync(100))
            return;

        try
        {
            IsSyncing = true;
            _isInternalSyncing = true;
            var totalSynchronized = 0;

            foreach (var episode in _currentEpisodes)
            {
                var localEpisodeDir = Path.Combine(_currentLocalRoot, episode.Name);
                Directory.CreateDirectory(localEpisodeDir);
                var localFinalDir = Path.Combine(localEpisodeDir, "成片");
                Directory.CreateDirectory(localFinalDir);

                WorkbookDocument? doc = null;
                try
                {
                    doc = await _service.OpenAsync(episode.FileId);
                }
                catch
                {
                    // If backend workbook read fails, continue to next episode
                }

                var sceneSheet = (!string.IsNullOrWhiteSpace(episode.SheetId)
                    ? doc?.Sheets.FirstOrDefault(s => s.Id == episode.SheetId)
                    : null)
                    ?? doc?.Sheets.FirstOrDefault(s => s.Name.Equals(episode.Name, StringComparison.OrdinalIgnoreCase))
                    ?? doc?.Sheets.FirstOrDefault(s => s.Kind is "scene_beats" or "scene_review" || s.SceneRows.Count > 0);
                var sceneRows = sceneSheet?.SceneRows ?? [];

                // 1. Process each beat row in the workbook
                foreach (var row in sceneRows)
                {
                    var beatNum = NormalizeBeatNum(!string.IsNullOrWhiteSpace(row.BeatNumber) ? row.BeatNumber : row.SceneNumber);
                    var beatFolderName = $"第{beatNum}beat";
                    var localBeatDir = Path.Combine(localEpisodeDir, beatFolderName);
                    Directory.CreateDirectory(localBeatDir);

                    // A. Sync active videos from NAS to local
                    var activeVideos = row.Videos.Count > 0 ? row.Videos : (row.Video != null ? [row.Video] : []);
                    foreach (var video in activeVideos)
                    {
                        var nasPath = video.Path;
                        if (string.IsNullOrWhiteSpace(nasPath) || !File.Exists(nasPath))
                        {
                            var candidate = Path.Combine(_shareRoot, "项目", _currentProjectName, episode.Name, beatFolderName, video.Name);
                            if (File.Exists(candidate)) nasPath = candidate;
                        }

                        if (!string.IsNullOrWhiteSpace(nasPath) && File.Exists(nasPath))
                        {
                            var localFilePath = Path.Combine(localBeatDir, video.Name);
                            if (NeedsCopy(nasPath, localFilePath))
                            {
                                totalSynchronized++;
                                StatusText = $"正在同步 {totalSynchronized} 个文件...";
                                await CopyFileAsync(nasPath, localFilePath);
                            }
                        }
                    }

                    // B. Soft-delete enforcement: remove discarded videos from local disk
                    // (Discarded videos strictly do NOT show on local disk)
                    foreach (var discarded in row.DiscardedVideos)
                    {
                        var localFilePath = Path.Combine(localBeatDir, discarded.Name);
                        if (File.Exists(localFilePath))
                        {
                            lock (_suppressedDeletes)
                            {
                                _suppressedDeletes.Add(localFilePath);
                            }
                            try { File.Delete(localFilePath); } catch { }
                        }
                    }
                }

                // 2. Sync 成片 folder between NAS and local
                var nasFinalDir = Path.Combine(_shareRoot, "项目", _currentProjectName, episode.Name, "成片");
                if (Directory.Exists(nasFinalDir))
                {
                    foreach (var nasFile in Directory.EnumerateFiles(nasFinalDir))
                    {
                        var fileName = Path.GetFileName(nasFile);
                        var localFinalFile = Path.Combine(localFinalDir, fileName);
                        if (NeedsCopy(nasFile, localFinalFile))
                        {
                            totalSynchronized++;
                            StatusText = $"正在同步 {totalSynchronized} 个文件...";
                            await CopyFileAsync(nasFile, localFinalFile);
                        }
                    }
                }

                // Also push any local exports in 成片 to NAS
                if (Directory.Exists(localFinalDir))
                {
                    foreach (var localFile in Directory.EnumerateFiles(localFinalDir))
                    {
                        var fileName = Path.GetFileName(localFile);
                        Directory.CreateDirectory(nasFinalDir);
                        var nasFinalFile = Path.Combine(nasFinalDir, fileName);
                        if (NeedsCopy(localFile, nasFinalFile))
                        {
                            totalSynchronized++;
                            StatusText = $"正在同步 {totalSynchronized} 个文件...";
                            await CopyFileAsync(localFile, nasFinalFile);
                        }
                    }
                }
            }

            StatusText = "已同步最新";
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                StatusText = $"同步提示: {ex.Message}";
            }
        }
        finally
        {
            _isInternalSyncing = false;
            IsSyncing = false;
            _syncLock.Release();
        }
    }

    private async void OnLocalFileCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (!IsWatching || _isInternalSyncing || string.IsNullOrEmpty(e.FullPath)) return;
            if (!File.Exists(e.FullPath)) return;
            var ext = Path.GetExtension(e.FullPath);
            if (!VideoExtensions.Contains(ext)) return;

            var rel = Path.GetRelativePath(_currentLocalRoot, e.FullPath);
            var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return;

            var episodeName = parts[0];
            var subFolder = parts[1];
            var fileName = parts[2];

            // Ensure file is ready
            if (!await WaitForFileReadyAsync(e.FullPath)) return;
            if (!IsWatching || _isInternalSyncing) return;

            if (subFolder.Equals("成片", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_shareRoot)) return;
                var nasFinalDir = Path.Combine(_shareRoot, "项目", _currentProjectName, episodeName, "成片");
                Directory.CreateDirectory(nasFinalDir);
                var nasDest = Path.Combine(nasFinalDir, fileName);
                if (NeedsCopy(e.FullPath, nasDest))
                {
                    StatusText = "正在同步 1 个文件...";
                    await CopyFileAsync(e.FullPath, nasDest);
                    StatusText = "已同步最新";
                }
                return;
            }

            if (subFolder.EndsWith("beat", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_shareRoot)) return;
                var beatNum = NormalizeBeatNum(subFolder);
                var episode = _currentEpisodes.FirstOrDefault(ep => ep.Name.Equals(episodeName, StringComparison.OrdinalIgnoreCase));
                if (episode == null) return;

                var nasBeatDir = Path.Combine(_shareRoot, "项目", _currentProjectName, episodeName, $"第{beatNum}beat");
                Directory.CreateDirectory(nasBeatDir);
                var nasDest = Path.Combine(nasBeatDir, fileName);
                if (NeedsCopy(e.FullPath, nasDest))
                {
                    StatusText = "正在同步 1 个文件...";
                    await CopyFileAsync(e.FullPath, nasDest);

                    try
                    {
                        var doc = await _service.OpenAsync(episode.FileId);
                        var sheet = (!string.IsNullOrWhiteSpace(episode.SheetId)
                            ? doc.Sheets.FirstOrDefault(s => s.Id == episode.SheetId)
                            : null)
                            ?? doc.Sheets.FirstOrDefault(s => s.Name.Equals(episode.Name, StringComparison.OrdinalIgnoreCase))
                            ?? doc.Sheets.FirstOrDefault(s => s.Kind is "scene_beats" or "scene_review" || s.SceneRows.Count > 0);
                        if (sheet != null)
                        {
                            var row = sheet.SceneRows.FirstOrDefault(r =>
                                NormalizeBeatNum(!string.IsNullOrWhiteSpace(r.BeatNumber) ? r.BeatNumber : r.SceneNumber) == beatNum);

                            if (row == null)
                            {
                                row = await _service.CreateSceneRowAsync(doc.File.Id, sheet.Id);
                                await _service.UpdateSceneRowAsync(doc.File.Id, row.Id, new UpdateSceneRowRequest
                                {
                                    BeatNumber = beatNum,
                                    SceneNumber = beatNum
                                });
                            }

                            var videoItem = new VideoItem(Guid.NewGuid().ToString("D"), fileName, nasDest);
                            await _service.UpdateSceneRowAsync(doc.File.Id, row.Id, new UpdateSceneRowRequest
                            {
                                VideoAction = "add",
                                Video = videoItem
                            });
                        }
                    }
                    catch
                    {
                    }

                    StatusText = "已同步最新";
                }
            }
        }
        catch
        {
        }
    }

    private async void OnLocalFileDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (!IsWatching || _isInternalSyncing || string.IsNullOrEmpty(e.FullPath)) return;

            lock (_suppressedDeletes)
            {
                if (_suppressedDeletes.Remove(e.FullPath)) return;
            }

            var rel = Path.GetRelativePath(_currentLocalRoot, e.FullPath);
            var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return;

            var episodeName = parts[0];
            var subFolder = parts[1];
            var fileName = parts[2];

            // Soft deletion protection for beat videos
            if (subFolder.EndsWith("beat", StringComparison.OrdinalIgnoreCase))
            {
                var beatNum = NormalizeBeatNum(subFolder);
                var episode = _currentEpisodes.FirstOrDefault(ep => ep.Name.Equals(episodeName, StringComparison.OrdinalIgnoreCase));
                if (episode == null) return;

                try
                {
                    var doc = await _service.OpenAsync(episode.FileId);
                    var sheet = (!string.IsNullOrWhiteSpace(episode.SheetId)
                        ? doc.Sheets.FirstOrDefault(s => s.Id == episode.SheetId)
                        : null)
                        ?? doc.Sheets.FirstOrDefault(s => s.Name.Equals(episode.Name, StringComparison.OrdinalIgnoreCase))
                        ?? doc.Sheets.FirstOrDefault(s => s.Kind is "scene_beats" or "scene_review" || s.SceneRows.Count > 0);
                    if (sheet != null)
                    {
                        var row = sheet.SceneRows.FirstOrDefault(r =>
                            NormalizeBeatNum(!string.IsNullOrWhiteSpace(r.BeatNumber) ? r.BeatNumber : r.SceneNumber) == beatNum);

                        if (row != null)
                        {
                            // Soft delete via discard action, NAS physical file remains 100% intact!
                            await _service.UpdateSceneRowAsync(doc.File.Id, row.Id, new UpdateSceneRowRequest
                            {
                                VideoAction = "discard",
                                VideoName = fileName
                            });

                            StatusText = "已将误删/删除文件转入废片库，NAS原文件已保留";
                            _ = Task.Delay(3000).ContinueWith(_ =>
                            {
                                if (StatusText == "已将误删/删除文件转入废片库，NAS原文件已保留")
                                    StatusText = "已同步最新";
                            });
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private void OnLocalFileRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            if (!IsWatching || _isInternalSyncing) return;
            OnLocalFileDeleted(sender, new FileSystemEventArgs(WatcherChangeTypes.Deleted, Path.GetDirectoryName(e.OldFullPath) ?? "", Path.GetFileName(e.OldFullPath)));
            OnLocalFileCreatedOrChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(e.FullPath) ?? "", Path.GetFileName(e.FullPath)));
        }
        catch
        {
        }
    }

    private static string ResolveLocalProjectRoot(string localDirectory, string projectName)
    {
        if (string.IsNullOrWhiteSpace(localDirectory)) return string.Empty;
        var dirInfo = new DirectoryInfo(localDirectory);
        if (dirInfo.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
        {
            return dirInfo.FullName;
        }
        return Path.Combine(localDirectory, projectName);
    }

    private static string NormalizeBeatNum(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "1";
        var cleaned = Regex.Replace(input, @"[^\d]", "").Trim();
        return string.IsNullOrEmpty(cleaned) ? "1" : cleaned;
    }

    private static bool NeedsCopy(string source, string dest)
    {
        if (!File.Exists(dest)) return true;
        try
        {
            var srcInfo = new FileInfo(source);
            var dstInfo = new FileInfo(dest);
            return srcInfo.Length != dstInfo.Length || Math.Abs((srcInfo.LastWriteTimeUtc - dstInfo.LastWriteTimeUtc).TotalSeconds) > 2;
        }
        catch
        {
            return true;
        }
    }

    private static async Task CopyFileAsync(string source, string dest)
    {
        var tempDest = dest + ".syncing";
        try
        {
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            using (var srcStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true))
            using (var dstStream = new FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await srcStream.CopyToAsync(dstStream);
            }

            File.Move(tempDest, dest, overwrite: true);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
        }
        catch
        {
            try { if (File.Exists(tempDest)) File.Delete(tempDest); } catch { }
        }
    }

    private static async Task<bool> WaitForFileReadyAsync(string path, int timeoutMs = 8000)
    {
        var elapsed = 0;
        while (elapsed < timeoutMs)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (fs.Length > 0) return true;
            }
            catch (IOException)
            {
            }
            await Task.Delay(300);
            elapsed += 300;
        }
        return false;
    }

    public void Dispose()
    {
        Stop();
        _syncLock.Dispose();
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.ProjectWorkbooks;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class SyncViewModel : ObservableObject, IDisposable
{
    private readonly string _configPath;
    private readonly ProjectWorkbookPage? _workbooks;
    private readonly EpisodeSyncService? _syncService;
    private readonly TransferViewModel? _transfers;
    private readonly V2ApiClient? _api;
    private readonly DriveSession? _session;
    private readonly string? _clientId;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly SemaphoreSlim _assetImageGate = new(1, 1);

    private ProjectInfo? _selectedProject;
    private ProjectFile? _selectedWorkbook;
    private string _localDirectory = string.Empty;
    private bool _isLoadingProjects;
    private bool _isLoadingWorkbooks;
    private bool _isLoadingEpisodes;
    private string _validationMessage = string.Empty;
    private string _assetImageStatusText = "尚未检查资产图片原图";
    private string _selectedAssetImagePolicy = "仅已选图片原图";
    private int _workbookLoadGeneration;
    private int _episodeLoadGeneration;
    private int _configurationGeneration;
    private int _assetSyncGeneration;
    private bool _disposed;

    public SyncViewModel(ProjectWorkbookPage? workbooks, TransferViewModel transfers, long accountId,
        Uri apiBaseUri, V2ApiClient? api = null, DriveSession? session = null, string? clientId = null)
    {
        _configPath = FileClientIdentity.GetEmployeeSyncConfigPath(apiBaseUri, accountId);
        _workbooks = workbooks;
        _transfers = transfers;
        _api = api;
        _session = session;
        _clientId = clientId;
        _syncService = transfers is null ? null : new EpisodeSyncService(transfers);
        if (transfers is not null) transfers.TaskUpdated += HandleTransferTaskUpdated;
        if (workbooks is not null) workbooks.ViewModel.ProjectRowsChanged += HandleProjectRowsChanged;
        if (workbooks != null && _syncService is not null)
        {
            _syncService.PropertyChanged += (s, e) =>
            {
                void Update()
                {
                    if (e.PropertyName == nameof(EpisodeSyncService.StatusText))
                        OnPropertyChanged(nameof(StatusText));
                    else if (e.PropertyName == nameof(EpisodeSyncService.IsSyncing))
                    {
                        OnPropertyChanged(nameof(IsSyncing));
                        RaiseSyncCommandState();
                    }
                    else if (e.PropertyName == nameof(EpisodeSyncService.IsWatching))
                    {
                        OnPropertyChanged(nameof(IsWatching));
                        OnPropertyChanged(nameof(ToggleButtonLabel));
                        RaiseSyncCommandState();
                    }
                }

                if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                {
                    dispatcher.InvokeAsync(Update);
                }
                else
                {
                    Update();
                }
            };
        }

        Projects = [];
        Workbooks = [];
        Episodes = [];
        AssetImagePolicies = ["仅已选图片原图", "已选原图 + 备选缩略图", "已选 + 备选原图"];

        BrowseLocalDirectoryCommand = new RelayCommand(_ => BrowseLocalDirectory());
        SelectAllEpisodesCommand = new RelayCommand(_ => SelectAllEpisodes());
        DeselectAllEpisodesCommand = new RelayCommand(_ => DeselectAllEpisodes());
        ToggleSyncCommand = new RelayCommand(_ => _ = ToggleSyncAsync(), _ => _syncService is not null);
        SyncNowCommand = new AsyncRelayCommand(_ => SyncNowAsync(), _ => CanSyncNow);
        SyncAssetImagesCommand = new AsyncRelayCommand(_ => SyncAssetImagesNowAsync(),
            _ => SelectedProject is not null && !string.IsNullOrWhiteSpace(LocalDirectory) && !IsLoadingProjects);

        LoadSavedConfig();
        _ = InitializeAsync();
    }

    public ObservableCollection<ProjectInfo> Projects { get; }
    public ObservableCollection<ProjectFile> Workbooks { get; }
    public ObservableCollection<SyncEpisodeItem> Episodes { get; }
    public IReadOnlyList<string> AssetImagePolicies { get; }

    public ProjectInfo? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                Interlocked.Increment(ref _assetSyncGeneration);
                StopWatcherForConfigurationChange();
                SaveConfig();
                _ = LoadWorkbooksAsync(value?.Id);
                if (value is not null) _ = ObserveProjectForAssetUpdatesAsync(value.Id);
                RefreshAssetImageStatus();
                RaiseSyncCommandState();
            }
        }
    }

    public ProjectFile? SelectedWorkbook
    {
        get => _selectedWorkbook;
        set
        {
            if (SetProperty(ref _selectedWorkbook, value))
            {
                StopWatcherForConfigurationChange();
                SaveConfig();
                _ = LoadEpisodesAsync(value);
                RaiseSyncCommandState();
            }
        }
    }

    public string LocalDirectory
    {
        get => _localDirectory;
        set
        {
            if (SetProperty(ref _localDirectory, value))
            {
                Interlocked.Increment(ref _assetSyncGeneration);
                StopWatcherForConfigurationChange();
                SaveConfig();
                ClearValidation();
                RaiseSyncCommandState();
            }
        }
    }

    public bool IsLoadingProjects
    {
        get => _isLoadingProjects;
        private set => SetProperty(ref _isLoadingProjects, value);
    }

    public bool IsLoadingWorkbooks
    {
        get => _isLoadingWorkbooks;
        private set => SetProperty(ref _isLoadingWorkbooks, value);
    }

    public bool IsLoadingEpisodes
    {
        get => _isLoadingEpisodes;
        private set => SetProperty(ref _isLoadingEpisodes, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public string StatusText => _syncService?.StatusText ?? "未连接同步服务";

    public string AssetImageStatusText
    {
        get => _assetImageStatusText;
        private set => SetProperty(ref _assetImageStatusText, value);
    }

    public string SelectedAssetImagePolicy
    {
        get => _selectedAssetImagePolicy;
        set
        {
            if (!AssetImagePolicies.Contains(value, StringComparer.Ordinal) || !SetProperty(ref _selectedAssetImagePolicy, value)) return;
            Interlocked.Increment(ref _assetSyncGeneration);
            SaveConfig();
        }
    }

    public bool IsSyncing => _syncService?.IsSyncing ?? false;

    public bool IsWatching => _syncService?.IsWatching ?? false;

    public string ToggleButtonLabel => IsWatching ? "停止同步" : "开启同步";

    public bool CanSyncNow => _syncService is not null && !IsSyncing && !IsLoadingWorkbooks && !IsLoadingEpisodes &&
                              SelectedProject != null && SelectedWorkbook != null && Episodes.Any(e => e.IsSelected) &&
                              !string.IsNullOrWhiteSpace(LocalDirectory);

    public ICommand BrowseLocalDirectoryCommand { get; }
    public ICommand SelectAllEpisodesCommand { get; }
    public ICommand DeselectAllEpisodesCommand { get; }
    public ICommand ToggleSyncCommand { get; }
    public AsyncRelayCommand SyncNowCommand { get; }
    public AsyncRelayCommand SyncAssetImagesCommand { get; }

    public async Task InitializeAsync()
    {
        if (_workbooks == null || Projects.Count > 0) return;
        IsLoadingProjects = true;
        try
        {
            var projectList = await _workbooks.ListProjectsAsync();
            Projects.Clear();
            foreach (var p in projectList)
            {
                Projects.Add(p);
            }

            var savedId = _savedProjectId;
            if (!string.IsNullOrWhiteSpace(savedId) && Projects.FirstOrDefault(p => p.Id == savedId) is { } matched)
            {
                SelectedProject = matched;
            }
            else if (Projects.Count > 0)
            {
                SelectedProject = Projects[0];
            }
        }
        catch (Exception exception)
        {
            ValidationMessage = $"项目列表读取未完成：{exception.Message}";
        }
        finally
        {
            IsLoadingProjects = false;
        }
    }

    public async Task LoadWorkbooksAsync(string? projectId)
    {
        var generation = Interlocked.Increment(ref _workbookLoadGeneration);
        Workbooks.Clear();
        Episodes.Clear();
        SelectedWorkbook = null;
        if (_workbooks == null || string.IsNullOrWhiteSpace(projectId))
        {
            IsLoadingWorkbooks = false;
            RaiseSyncCommandState();
            return;
        }

        IsLoadingWorkbooks = true;
        RaiseSyncCommandState();
        try
        {
            var files = await _workbooks.ListProjectFilesAsync(projectId);
            if (generation != Volatile.Read(ref _workbookLoadGeneration) || SelectedProject?.Id != projectId) return;
            var beatFiles = files.Where(f =>
                f.TemplateKind == "scene_beats" ||
                f.TemplateKind == "scene_review" ||
                f.Name.Contains("节拍", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var file in beatFiles)
            {
                Workbooks.Add(file);
            }

            var savedWbId = _savedWorkbookId;
            if (!string.IsNullOrWhiteSpace(savedWbId) && Workbooks.FirstOrDefault(w => w.Id == savedWbId) is { } matched)
            {
                SelectedWorkbook = matched;
            }
            else if (Workbooks.Count > 0)
            {
                SelectedWorkbook = Workbooks[0];
            }
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _workbookLoadGeneration))
                ValidationMessage = $"工作簿列表读取未完成：{exception.Message}";
        }
        finally
        {
            if (generation == Volatile.Read(ref _workbookLoadGeneration))
                IsLoadingWorkbooks = false;
            RaiseSyncCommandState();
        }
    }

    public async Task LoadEpisodesAsync(ProjectFile? workbook)
    {
        var generation = Interlocked.Increment(ref _episodeLoadGeneration);
        Episodes.Clear();
        if (_workbooks == null || workbook == null)
        {
            IsLoadingEpisodes = false;
            RaiseSyncCommandState();
            return;
        }

        IsLoadingEpisodes = true;
        RaiseSyncCommandState();
        try
        {
            var doc = await _workbooks.ViewModel.Service.OpenAsync(workbook.Id);
            if (generation != Volatile.Read(ref _episodeLoadGeneration) || SelectedWorkbook?.Id != workbook.Id) return;
            if (doc?.Sheets != null)
            {
                var sceneSheets = doc.Sheets.Where(s =>
                    s.Kind == "scene_beats" ||
                    s.Kind == "scene_review" ||
                    s.SceneRows.Count > 0).ToList();
                foreach (var sheet in sceneSheets)
                {
                    var isSelected = _savedEpisodes.Contains(sheet.Name) || _savedEpisodes.Count == 0;
                    var item = new SyncEpisodeItem
                    {
                        Name = sheet.Name,
                        FileId = workbook.Id,
                        SheetId = sheet.Id,
                        IsSelected = isSelected
                    };
                    item.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(SyncEpisodeItem.IsSelected))
                        {
                            StopWatcherForConfigurationChange();
                            SaveConfig();
                            ClearValidation();
                            RaiseSyncCommandState();
                        }
                    };
                    Episodes.Add(item);
                }
            }

            RaiseSyncCommandState();
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _episodeLoadGeneration))
                ValidationMessage = $"集数读取未完成：{exception.Message}";
        }
        finally
        {
            if (generation == Volatile.Read(ref _episodeLoadGeneration))
                IsLoadingEpisodes = false;
            RaiseSyncCommandState();
        }
    }

    private void BrowseLocalDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择剪辑电脑本地目录",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            LocalDirectory = dialog.FolderName;
        }
    }

    private void SelectAllEpisodes()
    {
        foreach (var ep in Episodes) ep.IsSelected = true;
    }

    private void DeselectAllEpisodes()
    {
        foreach (var ep in Episodes) ep.IsSelected = false;
    }

    private async Task ToggleSyncAsync()
    {
        await _configurationGate.WaitAsync();
        try
        {
            if (_syncService == null) return;

            if (_syncService.IsWatching)
            {
                await _syncService.StopAsync();
                ValidationMessage = string.Empty;
                return;
            }

            if (SelectedProject == null)
            {
                ValidationMessage = "请先选择需要同步的项目。";
                return;
            }

            if (SelectedWorkbook == null)
            {
                ValidationMessage = "请先选择项目节拍表。";
                return;
            }

            var selectedEpisodes = Episodes.Where(e => e.IsSelected).ToList();
            if (selectedEpisodes.Count == 0)
            {
                ValidationMessage = "请至少勾选一个集数。";
                return;
            }

            if (string.IsNullOrWhiteSpace(LocalDirectory))
            {
                ValidationMessage = "请先选择剪辑电脑本地目录。";
                return;
            }

            ValidationMessage = string.Empty;
            if (!_syncService.Configure(SelectedProject.Id, SelectedProject.Name, selectedEpisodes, LocalDirectory))
            {
                ValidationMessage = _syncService.StatusText;
                return;
            }
            if (!await _syncService.StartAsync())
            {
                ValidationMessage = _syncService.StatusText;
                return;
            }
            SaveConfig();
        }
        catch (Exception ex)
        {
            ValidationMessage = $"开启同步出错: {ex.Message}";
        }
        finally { _configurationGate.Release(); }
    }

    private async Task SyncNowAsync()
    {
        await _configurationGate.WaitAsync();
        try
        {
            if (_syncService == null || SelectedProject == null || SelectedWorkbook == null) return;
            var selectedEpisodes = Episodes.Where(e => e.IsSelected).ToList();
            if (selectedEpisodes.Count == 0 || string.IsNullOrWhiteSpace(LocalDirectory))
            {
                ValidationMessage = "请确认已选择集数和本地目录。";
                return;
            }

            if (!_syncService.Configure(SelectedProject.Id, SelectedProject.Name, selectedEpisodes, LocalDirectory))
            {
                ValidationMessage = _syncService.StatusText;
                return;
            }

            ValidationMessage = string.Empty;
            await _syncService.SyncNowAsync();
            await SyncAssetImagesNowAsync();
        }
        catch (Exception ex)
        {
            ValidationMessage = $"同步出错: {ex.Message}";
        }
        finally { _configurationGate.Release(); }
    }

    private async Task SyncAssetImagesNowAsync(CancellationToken cancellationToken = default)
    {
        await _assetImageGate.WaitAsync(cancellationToken);
        try { await SyncAssetImagesCoreAsync(cancellationToken); }
        finally { _assetImageGate.Release(); }
    }

    private async Task SyncAssetImagesCoreAsync(CancellationToken cancellationToken)
    {
        var project = SelectedProject;
        if (_workbooks is null || _transfers is null || _api is null || _session is null ||
            string.IsNullOrWhiteSpace(_clientId) || project is null || string.IsNullOrWhiteSpace(LocalDirectory))
        {
            AssetImageStatusText = "资产图片原图同步不可用：请检查项目、本地目录和文件服务连接";
            return;
        }

        AssetImageStatusText = "正在读取资产行的已选/备选引用…";
        try
        {
            var generation = Volatile.Read(ref _assetSyncGeneration);
            var policy = SelectedAssetImagePolicy;
            var localDirectory = LocalDirectory;
            await _workbooks.ViewModel.ObserveProjectAsync(project.Id);
            if (generation != Volatile.Read(ref _assetSyncGeneration) || SelectedProject?.Id != project.Id) return;
            await _transfers.ResumeShutdownPausedProjectAssetDownloadsAsync(project.Id, cancellationToken);
            var workbooks = await _workbooks.ListProjectFilesAsync(project.Id, cancellationToken);
            var assetWorkbook = workbooks.FirstOrDefault(file => file.TemplateKind == "asset_review");
            if (assetWorkbook is null)
            {
                AssetImageStatusText = "该项目没有资产表；没有原图进入同步队列";
                return;
            }

            var document = await _workbooks.ViewModel.Service.OpenAsync(assetWorkbook.Id, cancellationToken);
            var references = new List<(ImageAsset Asset, bool Selected)>();
            foreach (var row in document.Sheets.SelectMany(sheet => sheet.AssetRows))
            {
                if (row.SelectedImage is { } selected) references.Add((selected, true));
                if (policy != "仅已选图片原图")
                    references.AddRange(row.CandidateImages
                        .Where(candidate => candidate.Id != row.SelectedImage?.Id)
                        .Select(candidate => (candidate, false)));
            }

            var distinct = references
                .GroupBy(item => (item.Asset.Id, item.Asset.CurrentRevisionId))
                .Select(group => (Asset: group.First().Asset, Selected: group.Any(item => item.Selected)))
                .ToArray();
            var queued = 0;
            var alreadyAvailable = 0;
            var inProgress = 0;
            var failures = new List<string>();
            using var slots = new SemaphoreSlim(3, 3);
            await Task.WhenAll(distinct.Select(async item =>
            {
                await slots.WaitAsync(cancellationToken);
                try
                {
                    if (generation != Volatile.Read(ref _assetSyncGeneration) || SelectedProject?.Id != project.Id) return;
                    var downloadOriginal = item.Selected || policy == "已选 + 备选原图";
                    var scopeId = $"project:{project.Id}:asset-{(downloadOriginal ? "original" : "thumbnail")}";
                    if (string.IsNullOrWhiteSpace(item.Asset.FileId) || string.IsNullOrWhiteSpace(item.Asset.CurrentRevisionId))
                        throw new InvalidDataException("资产引用缺少 file_id 或 revision_id，未按文件名推测并加入下载。");
                    var ticket = downloadOriginal
                        ? await _api.CreateFileDownloadTicketAsync(_session.SessionToken,
                            item.Asset.FileId, _clientId, item.Asset.CurrentRevisionId, cancellationToken)
                        : await _api.CreateFileThumbnailTicketAsync(_session.SessionToken,
                            item.Asset.FileId, _clientId, item.Asset.CurrentRevisionId, cancellationToken);

                    if (!string.Equals(ticket.FileId, item.Asset.FileId, StringComparison.Ordinal) ||
                        (item.Asset.CurrentRevisionId is { Length: > 0 } revision && !string.Equals(ticket.RevisionId, revision, StringComparison.Ordinal)) ||
                        string.IsNullOrWhiteSpace(ticket.ObjectId) || ticket.Size <= 0 || ticket.Sha256.Length != 64 || !ticket.Sha256.All(Uri.IsHexDigit) ||
                        (downloadOriginal && ticket.ObjectKind != "image") || (!downloadOriginal && ticket.ObjectKind != "thumbnail"))
                        throw new InvalidDataException("下载票据与资产行引用的文件版本或原图/缩略图类型不匹配。");

                    if (generation != Volatile.Read(ref _assetSyncGeneration) || SelectedProject?.Id != project.Id ||
                        !string.Equals(LocalDirectory, localDirectory, StringComparison.Ordinal) ||
                        !string.Equals(SelectedAssetImagePolicy, policy, StringComparison.Ordinal)) return;

                    var destination = downloadOriginal
                        ? BuildOriginalAssetPath(localDirectory, project.Id, item.Asset, ticket.RevisionId)
                        : FileThumbnailProvider.GetCachePathFor(ticket.FileId, ticket.RevisionId);
                    if (!downloadOriginal) EnsureThumbnailCachePath(destination);
                    if (File.Exists(destination))
                    {
                        if (await MatchesTicketAsync(destination, ticket.Size, ticket.Sha256, cancellationToken))
                        {
                            Interlocked.Increment(ref alreadyAvailable);
                            return;
                        }
                        throw new IOException("本地已有同路径内容，但大小或 SHA-256 与当前版本不符；为保护缓存未覆盖。");
                    }

                    var existing = _transfers.TaskSnapshots.FirstOrDefault(snapshot =>
                        snapshot.Source == "asset_image_sync" && snapshot.ScopeId == scopeId && snapshot.FileId == ticket.FileId &&
                        snapshot.RevisionId == ticket.RevisionId && !TransferTaskStateProjection.IsTerminal(snapshot.State));
                    if (existing is not null)
                    {
                        Interlocked.Increment(ref inProgress);
                        return;
                    }

                    var operationId = await _transfers.QueueDownloadAsync(ticket, destination,
                        Path.GetFileName(destination), folderGroup: "asset-image-sync", cancellationToken: cancellationToken,
                        source: "asset_image_sync", scopeId: scopeId, projectId: project.Id);
                    if (operationId is null)
                        throw new IOException(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                            ? "无法加入 Rust 原图下载队列。" : _transfers.OperationErrorMessage);
                    Interlocked.Increment(ref queued);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    InvalidOperationException or V2ApiException or System.Security.SecurityException)
                {
                    lock (failures) failures.Add($"{item.Asset.Name}：{exception.Message}");
                }
                finally { slots.Release(); }
            }));

            await _transfers.RefreshTasksAsync().ConfigureAwait(true);
            RefreshAssetImageStatus();
            if (failures.Count > 0)
                AssetImageStatusText = $"已排入 {queued} 项；已有可用缓存 {alreadyAvailable} 项；{failures.Count} 项需要处理。{failures[0]}";
            else if (queued > 0 || inProgress > 0)
                AssetImageStatusText = $"已排入 {queued} 项；{inProgress} 项已在队列中；已有可用缓存 {alreadyAvailable} 项。原图完成状态以 Rust 校验任务为准。";
            else
                AssetImageStatusText = $"当前引用的原图/缩略图已校验可用，共 {alreadyAvailable} 项。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or V2ApiException or System.Security.SecurityException or ArgumentException)
        {
            AssetImageStatusText = $"资产图片检查未完成：{exception.Message}";
        }
    }

    private void HandleTransferTaskUpdated(TransferAgentTaskSnapshot snapshot)
    {
        if (snapshot.Source == "asset_image_sync") RefreshAssetImageStatus();
    }

    private void HandleProjectRowsChanged(string projectId)
    {
        if (_disposed || !string.Equals(SelectedProject?.Id, projectId, StringComparison.Ordinal)) return;
        AssetImageStatusText = "资产行图片引用已变化，正在增量检查原图/缩略图…";
        _ = SyncAssetImagesNowAsync();
    }

    private async Task ObserveProjectForAssetUpdatesAsync(string projectId)
    {
        try { if (_workbooks is not null) await _workbooks.ViewModel.ObserveProjectAsync(projectId); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or WorkbookApiException)
        {
            if (!_disposed && SelectedProject?.Id == projectId)
                ValidationMessage = $"资产引用变更订阅暂不可用：{exception.Message}";
        }
    }

    private void RefreshAssetImageStatus()
    {
        if (_transfers is null || SelectedProject is null) return;
        var latest = _transfers.TaskSnapshots
            .Where(task => task.Source == "asset_image_sync" && task.ProjectId == SelectedProject.Id)
            .GroupBy(task => (task.FileId, task.RevisionId, task.ScopeId))
            .Select(group => group.OrderByDescending(task => task.UpdatedAt).First())
            .ToArray();
        var originals = latest.Where(task => task.ScopeId == $"project:{SelectedProject.Id}:asset-original").ToArray();
        var thumbnails = latest.Where(task => task.ScopeId == $"project:{SelectedProject.Id}:asset-thumbnail").ToArray();
        static string Status(IEnumerable<TransferAgentTaskSnapshot> items)
        {
            var all = items.ToArray();
            var inProgress = all.Count(task => !TransferTaskStateProjection.IsTerminal(task.State) && task.State != "paused");
            return $"{inProgress} 进行中 / {all.Count(task => task.State == "paused")} 已暂停 / {all.Count(task => task.State == "completed")} 已校验 / {all.Count(task => task.State == "conflict")} 冲突 / {all.Count(task => task.State == "failed")} 失败";
        }
        if (originals.Length > 0 || thumbnails.Length > 0)
            AssetImageStatusText = $"资产图片：原图 {Status(originals)}；备选缩略图 {Status(thumbnails)}";
    }

    private static string BuildOriginalAssetPath(string localDirectory, string projectId, ImageAsset asset, string revisionId)
    {
        var project = SafeFileSegment(projectId);
        var assetId = SafeFileSegment(asset.Id);
        var revision = SafeFileSegment(revisionId);
        var name = Path.GetFileName(asset.LogicalPath ?? asset.Name);
        var extension = Path.GetExtension(name);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16 || extension.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.'))
            extension = ".image";
        var safeName = SafeFileSegment(Path.GetFileNameWithoutExtension(name));
        var root = Path.GetFullPath(localDirectory);
        EnsureSyncDirectory(root);
        var assetRoot = Path.Combine(root, "AssetImages");
        Directory.CreateDirectory(assetRoot);
        EnsureSyncDirectory(assetRoot);
        var directory = Path.Combine(assetRoot, project);
        Directory.CreateDirectory(directory);
        EnsureSyncDirectory(directory);
        return Path.Combine(directory, $"{assetId}-{revision}-{safeName}{extension}");
    }

    private static void EnsureSyncDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("资产同步目录不存在。");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("资产同步路径包含链接目录，已拒绝写入。");
    }

    private static void EnsureThumbnailCachePath(string path)
    {
        var appDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuYingDrive");
        var cacheDirectory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("缩略图缓存路径无效。");
        Directory.CreateDirectory(cacheDirectory);
        foreach (var directory in new[] { appDirectory, cacheDirectory })
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("缩略图缓存路径包含链接目录或不存在。");
        }
    }

    private static string SafeFileSegment(string value)
    {
        var safe = string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || char.IsControl(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(safe) ? "asset" : safe.Length > 100 ? safe[..100] : safe;
    }

    private static async Task<bool> MatchesTicketAsync(string path, long size, string sha256, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != size || sha256.Length != 64) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("本地资产缓存文件是链接路径，已拒绝读取。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(actual).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private void StopWatcherForConfigurationChange()
    {
        if (_syncService is null) return;
        var generation = Interlocked.Increment(ref _configurationGeneration);
        _ = StopWatcherForConfigurationChangeAsync(generation);
    }

    private async Task StopWatcherForConfigurationChangeAsync(int generation)
    {
        await _configurationGate.WaitAsync();
        try
        {
            if (_disposed || generation != Volatile.Read(ref _configurationGeneration)) return;
            if (_syncService?.IsWatching == true) await _syncService.StopAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TransferAgentException or TimeoutException)
        {
            ValidationMessage = $"切换同步设置未能停止旧监听：{exception.Message}";
        }
        finally { _configurationGate.Release(); }
    }

    private void RaiseSyncCommandState()
    {
        OnPropertyChanged(nameof(CanSyncNow));
        SyncNowCommand.RaiseCanExecuteChanged();
        SyncAssetImagesCommand.RaiseCanExecuteChanged();
        (ToggleSyncCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void ClearValidation()
    {
        if (!string.IsNullOrWhiteSpace(ValidationMessage))
            ValidationMessage = string.Empty;
    }

    private string? _savedProjectId;
    private string? _savedWorkbookId;
    private readonly HashSet<string> _savedEpisodes = new(StringComparer.OrdinalIgnoreCase);

    private void LoadSavedConfig()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            var json = File.ReadAllText(_configPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("LocalDirectory", out var dirElem))
                _localDirectory = dirElem.GetString() ?? string.Empty;
            if (root.TryGetProperty("ProjectId", out var idElem))
                _savedProjectId = idElem.GetString();
            if (root.TryGetProperty("WorkbookId", out var wbElem))
                _savedWorkbookId = wbElem.GetString();
            if (root.TryGetProperty("Episodes", out var epElem) && epElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in epElem.EnumerateArray())
                {
                    if (item.GetString() is { } name) _savedEpisodes.Add(name);
                }
            }
            if (root.TryGetProperty("AssetImagePolicy", out var policyElem) && policyElem.GetString() is { } policy &&
                AssetImagePolicies.Contains(policy, StringComparer.Ordinal))
                _selectedAssetImagePolicy = policy;
        }
        catch
        {
        }
    }

    private void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var data = new
            {
                LocalDirectory = _localDirectory,
                ProjectId = SelectedProject?.Id,
                WorkbookId = SelectedWorkbook?.Id,
                Episodes = Episodes.Where(e => e.IsSelected).Select(e => e.Name).ToArray(),
                AssetImagePolicy = SelectedAssetImagePolicy
            };

            File.WriteAllText(_configPath, JsonSerializer.Serialize(data));
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_workbooks is not null) _workbooks.ViewModel.ProjectRowsChanged -= HandleProjectRowsChanged;
        if (_transfers is not null) _transfers.TaskUpdated -= HandleTransferTaskUpdated;
        _syncService?.Dispose();
    }
}

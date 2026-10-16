using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.NativeWpf.Views;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>Logical file browser. No item in this view contains or opens a NAS path.</summary>
public sealed class FilesViewModel : ObservableObject, IFileBrowser, IDisposable
{
    private readonly V2ApiClient _api;
    private readonly DriveSession _session;
    private readonly TransferViewModel _transfers;
    private readonly RustTransferAgentClient _agent;
    private readonly string _clientId;
    private readonly FileThumbnailProvider _thumbnailProvider;
    private readonly WindowsFilePickerService _filePicker = new();
    private string _searchText = string.Empty;
    private bool _isGridView = true;
    private string _spaceTitle = string.Empty;
    private IReadOnlyList<DriveFileItem> _selection = [];
    private string _currentDirectory = string.Empty;
    private bool _isLoading;
    private string _errorMessage = string.Empty;
    private string _truncationNotice = string.Empty;
    private bool _isRecycleBin;
    private string? _pendingRefreshLogicalPath;
    private bool _isDropTargetActive;
    private string _inlineUploadMessage = string.Empty;
    private readonly Dictionary<string, InlineUploadCard> _inlineUploadsByOperation = new(StringComparer.Ordinal);
    private LogicalFileScope _scope = new("all", canWrite: false, mediaKind: "image", flatten: true);
    private int _loadRequestId;
    private DispatcherTimer? _refreshTimer;

    public FilesViewModel(V2ApiClient api, DriveSession session, TransferViewModel transfers,
        RustTransferAgentClient agent, string clientId)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _thumbnailProvider = new FileThumbnailProvider(_api, _session, _transfers, _clientId);
        Files = [];
        InlineUploadCards = [];
        ItemsView = CollectionViewSource.GetDefaultView(Files);
        ItemsView.Filter = FilterItem;
        SpaceTitle = ProjectNavigation.ImageGroupName;
        _transfers.UploadCompleted += HandleUploadCompleted;
        _transfers.TaskUpdated += HandleTransferTaskUpdated;

        ToggleGridCommand = new RelayCommand(_ => SetViewMode(true));
        ToggleListCommand = new RelayCommand(_ => SetViewMode(false));
        SelectAllCommand = new RelayCommand(_ => SelectionRequested?.Invoke(SelectionRequest.SelectAll));
        ClearSelectionCommand = new RelayCommand(_ => SelectionRequested?.Invoke(SelectionRequest.Clear));
        OpenCommand = new RelayCommand<DriveFileItem>(OpenItem);
        DownloadCommand = new RelayCommand<DriveFileItem>(item => _ = DownloadItemAsync(item));
        DetailsCommand = new RelayCommand<DriveFileItem>(ShowDetails);
        HistoryCommand = new RelayCommand<DriveFileItem>(item => _ = ShowHistoryAsync(item), item => item?.FileId is not null);
        RestoreCommand = new AsyncRelayCommand(parameter => RestoreAsync(parameter as DriveFileItem), _ => CanRestore);
        ToggleRecycleBinCommand = new RelayCommand(_ => ToggleRecycleBin(), _ => CanShowRecycleBin);
        UploadCommand = new RelayCommand(_ => PickUpload(), _ => CanUpload);
        NavigateUpCommand = new RelayCommand(_ => NavigateUp(), _ => CanNavigateUp);
        UploadFolderCommand = new AsyncRelayCommand(_ => UploadFolderAsync(), _ => CanUpload);
        RetryInlineUploadCommand = new AsyncRelayCommand(parameter => RetryInlineUploadAsync(parameter as InlineUploadCard),
            parameter => parameter is InlineUploadCard card && card.CanRetry);
        CancelInlineUploadCommand = new AsyncRelayCommand(parameter => CancelInlineUploadAsync(parameter as InlineUploadCard),
            parameter => parameter is InlineUploadCard card && card.CanCancel);
        RetryInlineDownloadCommand = new AsyncRelayCommand(parameter => RetryInlineDownloadAsync(parameter as DriveFileItem),
            parameter => parameter is DriveFileItem item && item.InlineTransferCanRetry);
        CancelInlineDownloadCommand = new AsyncRelayCommand(parameter => CancelInlineDownloadAsync(parameter as DriveFileItem),
            parameter => parameter is DriveFileItem item && item.InlineTransferCanCancel);
        RenameCommand = new AsyncRelayCommand(parameter => RenameAsync(parameter as DriveFileItem), _ => CanModify);
        MoveCommand = new AsyncRelayCommand(parameter => MoveAsync(parameter as DriveFileItem), _ => CanModify);
        DeleteCommand = new AsyncRelayCommand(parameter => DeleteAsync(parameter as DriveFileItem), _ => CanModify);
        RefreshInlineUploadCardsFromSnapshots();
    }

    public event Action<SelectionRequest>? SelectionRequested;
    public ObservableCollection<DriveFileItem> Files { get; }
    public ObservableCollection<InlineUploadCard> InlineUploadCards { get; }
    public ICollectionView ItemsView { get; }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) { ItemsView.Refresh(); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyMessage)); } }
    }
    public bool IsGridView { get => _isGridView; set => SetProperty(ref _isGridView, value); }
    public string SpaceTitle
    {
        get => _spaceTitle;
        private set { if (SetProperty(ref _spaceTitle, value)) OnPropertyChanged(nameof(CurrentPathLabel)); }
    }
    public int SelectedCount => _selection.Count;
    public string SelectedLabel => SelectedCount == 1 ? "已选择 1 项" : $"已选择 {SelectedCount} 项";
    public bool HasSelection => SelectedCount > 0;
    public bool IsReadOnly => !_scope.CanWrite || _session.IsReadOnly || _isRecycleBin || _scope.ProjectId is not null;
    public bool CanUpload => !IsReadOnly && !IsLoading;
    public bool CanAcceptFileDrop => CanUpload && _scope.ProjectId is null &&
        _scope.ScopeId.StartsWith("personal:", StringComparison.Ordinal);
    public bool IsDropTargetActive
    {
        get => _isDropTargetActive;
        private set => SetProperty(ref _isDropTargetActive, value);
    }
    public string InlineUploadMessage { get => _inlineUploadMessage; private set => SetProperty(ref _inlineUploadMessage, value); }
    // Generic project file operations cannot atomically update the workbook's video
    // references. Keep project file trees read-only until the backend can make those
    // changes together; project video uploads remain available through the workbook flow.
    public bool CanModify => !IsReadOnly && !IsLoading;
    public bool CanRestore => _isRecycleBin && !IsLoading && CanWriteOrRestoreProjectFiles;
    private bool CanWriteOrRestoreProjectFiles => !_session.IsReadOnly && (_scope.CanWrite || _scope.ProjectId is not null);
    public bool CanShowRecycleBin => !_session.IsReadOnly && !_scope.Flatten && _scope.ScopeId != "all";
    public bool IsRecycleBin { get => _isRecycleBin; private set { if (SetProperty(ref _isRecycleBin, value)) { OnPropertyChanged(nameof(IsReadOnly)); OnPropertyChanged(nameof(CanUpload)); OnPropertyChanged(nameof(CanModify)); OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(RecycleBinButtonLabel)); RefreshCommandStates(); } } }
    public string RecycleBinButtonLabel => IsRecycleBin ? "返回文件" : "回收站";
    public string TruncationNotice { get => _truncationNotice; private set => SetProperty(ref _truncationNotice, value); }
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) { OnPropertyChanged(nameof(IsEmpty)); RefreshCommandStates(); } } }
    public string ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(IsEmpty)); } } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsLoading && !HasError && ItemsView.IsEmpty && InlineUploadCards.Count == 0;
    public string EmptyMessage => IsRecycleBin ? "回收站为空" : string.IsNullOrWhiteSpace(SearchText) ? "此文件夹为空" : "没有匹配的文件";
    public string LoadingMessage => "正在读取文件目录…";
    public bool CanNavigateUp => !string.IsNullOrEmpty(_currentDirectory);
    public string CurrentPathLabel => string.IsNullOrEmpty(_currentDirectory) ? SpaceTitle : $"{SpaceTitle} / {_currentDirectory}";

    public RelayCommand ToggleGridCommand { get; }
    public RelayCommand ToggleListCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand<DriveFileItem> OpenCommand { get; }
    public RelayCommand<DriveFileItem> DownloadCommand { get; }
    public RelayCommand<DriveFileItem> DetailsCommand { get; }
    public RelayCommand<DriveFileItem> HistoryCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand ToggleRecycleBinCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand NavigateUpCommand { get; }
    public AsyncRelayCommand UploadFolderCommand { get; }
    public AsyncRelayCommand RetryInlineUploadCommand { get; }
    public AsyncRelayCommand CancelInlineUploadCommand { get; }
    public AsyncRelayCommand RetryInlineDownloadCommand { get; }
    public AsyncRelayCommand CancelInlineDownloadCommand { get; }
    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand MoveCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }

    ICommand IFileBrowser.OpenCommand => OpenCommand;
    ICommand IFileBrowser.DownloadCommand => DownloadCommand;
    ICommand IFileBrowser.NavigateUpCommand => NavigateUpCommand;
    ICommand IFileBrowser.ToggleGridCommand => ToggleGridCommand;
    ICommand IFileBrowser.ToggleListCommand => ToggleListCommand;
    ICommand IFileBrowser.UploadCommand => UploadCommand;
    ICommand IFileBrowser.UploadFolderCommand => UploadFolderCommand;
    ICommand IFileBrowser.RenameCommand => RenameCommand;
    ICommand IFileBrowser.MoveCommand => MoveCommand;
    ICommand IFileBrowser.DeleteCommand => DeleteCommand;
    ICommand IFileBrowser.HistoryCommand => HistoryCommand;
    ICommand IFileBrowser.RestoreCommand => RestoreCommand;
    ICommand IFileBrowser.ToggleRecycleBinCommand => ToggleRecycleBinCommand;
    ICommand IFileBrowser.DetailsCommand => DetailsCommand;
    ICommand IFileBrowser.RetryInlineUploadCommand => RetryInlineUploadCommand;
    ICommand IFileBrowser.CancelInlineUploadCommand => CancelInlineUploadCommand;
    ICommand IFileBrowser.RetryInlineDownloadCommand => RetryInlineDownloadCommand;
    ICommand IFileBrowser.CancelInlineDownloadCommand => CancelInlineDownloadCommand;

    public async Task InitializeAsync() => await LoadCurrentDirectoryAsync().ConfigureAwait(true);

    public bool ActivateNavigation(string key)
    {
        switch (key)
        {
            case "图片": SetScope(new LogicalFileScope("all", canWrite: false, mediaKind: "image", flatten: true), ProjectNavigation.ImageGroupName); return true;
            case "视频": SetScope(new LogicalFileScope("all", canWrite: false, mediaKind: "video", flatten: true), ProjectNavigation.VideoGroupName); return true;
            case "个人空间": SetScope(new LogicalFileScope($"personal:{_session.User.Id}"), "个人空间"); return true;
            default: return false;
        }
    }

    public void OpenProject(string projectId, string projectName) =>
        SetScope(new LogicalFileScope($"project:{projectId}", projectId), projectName);

    public void OpenProjectMedia(string projectId, string projectName, SpaceMediaKind media) =>
        SetScope(new LogicalFileScope($"project:{projectId}", projectId, mediaKind: media == SpaceMediaKind.Images ? "image" : "video"),
            $"{projectName} / {(media == SpaceMediaKind.Images ? ProjectNavigation.ImageGroupName : ProjectNavigation.VideoGroupName)}");

    private void SetScope(LogicalFileScope scope, string title)
    {
        _scope = scope;
        IsDropTargetActive = false;
        SpaceTitle = title;
        _currentDirectory = scope.RootPath;
        IsRecycleBin = false;
        TruncationNotice = string.Empty;
        OnPropertyChanged(nameof(CurrentPathLabel));
        OnPropertyChanged(nameof(CanNavigateUp));
        OnPropertyChanged(nameof(IsReadOnly));
        RefreshCommandStates();
        RefreshInlineUploadCardsFromSnapshots();
        RefreshInlineUploadCardsForCurrentDirectory();
        _ = LoadCurrentDirectoryAsync();
    }

    public void UpdateSelection(IReadOnlyList<DriveFileItem> items)
    {
        _selection = items ?? [];
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(SelectedLabel)); OnPropertyChanged(nameof(HasSelection));
        DeleteCommand.RaiseCanExecuteChanged(); MoveCommand.RaiseCanExecuteChanged(); RenameCommand.RaiseCanExecuteChanged();
        HistoryCommand.RaiseCanExecuteChanged(); RestoreCommand.RaiseCanExecuteChanged();
    }

    private bool FilterItem(object item) => item is DriveFileItem file &&
        (string.IsNullOrWhiteSpace(SearchText) || file.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    private void SetViewMode(bool grid)
    {
        if (IsGridView == grid) return;
        SelectionRequested?.Invoke(SelectionRequest.Clear); UpdateSelection([]); IsGridView = grid;
    }

    private void OpenItem(DriveFileItem? item)
    {
        if (item is null) return;
        if (item.IsDirectory)
        {
            if (_scope.Flatten) return;
            _currentDirectory = LogicalFileScope.Normalize(item.LogicalPath);
            OnPropertyChanged(nameof(CurrentPathLabel)); OnPropertyChanged(nameof(CanNavigateUp)); NavigateUpCommand.RaiseCanExecuteChanged();
            RefreshInlineUploadCardsForCurrentDirectory();
            _ = LoadCurrentDirectoryAsync();
            return;
        }
        _ = OpenFileAsync(item);
    }

    private async Task OpenFileAsync(DriveFileItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FileId)) return;
        if (item.HasInlineTransfer && (item.InlineTransferCanCancel ||
            item.InlineTransferStatus is "正在准备下载" or "排队中" or "下载中" or "正在校验")) return;
        var operationId = Guid.NewGuid().ToString("D");
        try
        {
            item.BeginInlineTransfer(operationId, "正在准备下载");
            var ticket = await _api.CreateFileDownloadTicketAsync(_session.SessionToken, item.FileId, _clientId, item.RevisionId);
            if (ticket.FileId != item.FileId || (item.RevisionId is { Length: > 0 } revision && ticket.RevisionId != revision)
                || ticket.Size < 0 || ticket.Sha256.Length != 64 || !ticket.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("下载票据与当前文件版本不匹配。");

            var existing = _transfers.TaskSnapshots
                .Where(task => task.Source == "wpf" && task.Kind.Contains("download", StringComparison.OrdinalIgnoreCase)
                    && task.FileId == item.FileId && task.RevisionId == ticket.RevisionId
                    && task.ScopeId == item.ScopeId && task.ProjectId == item.ProjectId)
                .OrderByDescending(task => task.UpdatedAt)
                .FirstOrDefault();
            var existingPath = existing is null ? null : TryOpenCachePath(existing.DestinationPath);
            if (existing is not null && existingPath is not null)
            {
                if (existing.State == "completed" && await MatchesTicketAsync(existingPath, ticket.Size, ticket.Sha256))
                {
                    item.SetInlineTransferStatus("已验证的下载缓存可用。");
                    StartLocalFile(existingPath, item);
                    return;
                }
                if (IsTransferActive(existing.State) || existing.Retryable && !File.Exists(existingPath))
                {
                    if (existing.State == "paused" && !await _transfers.ResumeOperationAsync(existing.OperationId))
                        throw new IOException("已暂停的下载任务无法继续。");
                    if (existing.Retryable && !IsTransferActive(existing.State) &&
                        !await _transfers.RetryOperationAsync(existing.OperationId))
                        throw new IOException("失败的下载任务无法安全重试。");
                    item.BeginInlineTransfer(existing.OperationId, "排队中", canCancel: true);
                    await _transfers.RefreshTasksAsync().ConfigureAwait(true);
                    _ = OpenWhenDownloadedAsync(existing.OperationId, existingPath, item, ticket.Size, ticket.Sha256);
                    return;
                }
            }

            var localPath = GetOpenCachePath(item, ticket.RevisionId);
            if (await MatchesTicketAsync(localPath, ticket.Size, ticket.Sha256))
            {
                item.SetInlineTransferStatus("本地缓存已通过文件版本和 SHA-256 校验。");
                StartLocalFile(localPath, item);
                return;
            }
            if (File.Exists(localPath))
            {
                localPath = GetOpenCachePath(item, ticket.RevisionId, ticket.Sha256);
                if (await MatchesTicketAsync(localPath, ticket.Size, ticket.Sha256))
                {
                    item.SetInlineTransferStatus("本地缓存已通过文件版本和 SHA-256 校验。");
                    StartLocalFile(localPath, item);
                    return;
                }
                for (var suffix = 1; File.Exists(localPath); suffix++)
                    localPath = GetOpenCachePath(item, ticket.RevisionId, ticket.Sha256, suffix);
            }
            var operation = await _transfers.QueueDownloadAsync(ticket, localPath, item.Name,
                cancellationToken: default, operationId: operationId, source: "wpf",
                scopeId: item.ScopeId, projectId: item.ProjectId);
            if (operation is null)
            {
                item.SetInlineTransferError(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                    ? "无法将文件加入下载队列。" : _transfers.OperationErrorMessage);
                return;
            }
            item.BeginInlineTransfer(operation, "排队中", canCancel: true);
            await _transfers.RefreshTasksAsync().ConfigureAwait(true);
            _ = OpenWhenDownloadedAsync(operation, localPath, item, ticket.Size, ticket.Sha256);
        }
        catch (Exception exception) when (exception is V2ApiException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            item.SetInlineTransferError(exception.Message);
        }
    }

    private async Task OpenWhenDownloadedAsync(string operationId, string localPath, DriveFileItem item,
        long expectedSize, string expectedHash)
    {
        try
        {
            await _transfers.WaitForTransferAsync(operationId).ConfigureAwait(false);
            if (!await MatchesTicketAsync(localPath, expectedSize, expectedHash).ConfigureAwait(false))
                throw new InvalidDataException("下载任务完成，但本地文件未通过 SHA-256 校验。");
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            if (dispatcher.CheckAccess()) StartLocalFile(localPath, item);
            else await dispatcher.InvokeAsync(() => StartLocalFile(localPath, item));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            if (dispatcher.CheckAccess()) item.SetInlineTransferError(exception.Message);
            else await dispatcher.InvokeAsync(() => item.SetInlineTransferError(exception.Message));
        }
    }

    private static void StartLocalFile(string path, DriveFileItem? item = null)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { item?.SetInlineTransferError($"文件已下载，但无法通过默认程序打开：{exception.Message}"); }
    }

    private async Task DownloadItemAsync(DriveFileItem? item)
    {
        if (item is null || item.IsDirectory || string.IsNullOrWhiteSpace(item.FileId)) return;
        if (item.HasInlineTransfer && item.InlineTransferCanCancel) return;
        var destination = _filePicker.PickDownloadDestination(item.Name);
        if (string.IsNullOrWhiteSpace(destination)) return;
        var operationId = Guid.NewGuid().ToString("D");
        item.BeginInlineTransfer(operationId, "正在准备下载");
        if (File.Exists(destination) || Directory.Exists(destination))
        { item.SetInlineTransferError("本机目标位置已有同名项目，下载未覆盖任何内容。请选择其他位置后重试。"); return; }
        try
        {
            var ticket = await _api.CreateFileDownloadTicketAsync(_session.SessionToken, item.FileId, _clientId, item.RevisionId);
            var queued = await _transfers.QueueDownloadAsync(ticket, destination, item.Name,
                cancellationToken: default, operationId: operationId, source: "wpf",
                scopeId: item.ScopeId, projectId: item.ProjectId);
            if (queued is null)
            {
                item.SetInlineTransferError(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                    ? "无法将文件加入下载队列。" : _transfers.OperationErrorMessage);
                return;
            }
            item.BeginInlineTransfer(queued, "排队中", canCancel: true);
            await _transfers.RefreshTasksAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is V2ApiException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { item.SetInlineTransferError(exception.Message); }
    }

    private static async Task<bool> MatchesTicketAsync(string path, long expectedSize, string expectedHash)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length != expectedSize || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("本地文件缓存是链接路径，已拒绝打开。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(actual).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransferActive(string state) => state is "queued" or "waiting_stable" or "hashing"
        or "transferring" or "verifying" or "staged" or "committing" or "paused" or "waiting_network" or "offline";

    private static string? TryOpenCachePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuYingDrive", "OpenFiles")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            var prefix = root + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullPath.StartsWith(prefix, comparison) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private string GetOpenCachePath(DriveFileItem item, string revisionId, string? hash = null, int suffix = 0)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuYingDrive", "OpenFiles");
        Directory.CreateDirectory(root);
        var appDirectory = Directory.GetParent(Path.GetFullPath(root))?.FullName
            ?? throw new InvalidDataException("打开文件缓存目录无效。");
        if ((File.GetAttributes(appDirectory) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("打开文件缓存目录包含链接路径，已拒绝写入。");
        var safeName = string.Concat(item.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var revision = string.Concat(revisionId.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var safeFileId = string.Concat((item.FileId ?? "file").Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var hashSuffix = string.IsNullOrWhiteSpace(hash) ? string.Empty : $"-{hash[..12].ToLowerInvariant()}";
        var collisionSuffix = suffix > 0 ? $"-{suffix}" : string.Empty;
        return Path.Combine(root, $"{safeFileId}-{revision}{hashSuffix}{collisionSuffix}-{safeName}");
    }

    private async Task UploadFolderAsync()
    {
        var folder = _filePicker.PickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;
        await QueueFolderUploadsAsync(folder, _currentDirectory);
    }

    private void PickUpload()
    {
        var paths = _filePicker.PickFiles();
        if (paths.Count == 0) return;
        var prepared = new List<(InlineUploadCard Card, string Path)>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                _ = QueueFolderUploadsAsync(path, _currentDirectory);
                continue;
            }
            if (TryCreateUploadCard(path, _currentDirectory, null, out var card, out var error))
                prepared.Add((card!, path));
            else
                InlineUploadMessage = error;
        }
        _ = QueuePreparedUploadsAsync(prepared);
    }

    public void SetDropTargetActive(bool active) => IsDropTargetActive = active && CanAcceptFileDrop;

    public async Task HandleFileDropAsync(IReadOnlyList<string> paths, DriveFileItem? targetFolder)
    {
        SetDropTargetActive(false);
        if (!CanAcceptFileDrop || paths.Count == 0) return;
        if (targetFolder is not null && !targetFolder.IsDirectory) return;
        var destinationRoot = targetFolder?.LogicalPath ?? _currentDirectory;
        var prepared = new List<(InlineUploadCard Card, string Path)>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path))
            {
                _ = QueueFolderUploadsAsync(path, destinationRoot);
                continue;
            }
            if (TryCreateUploadCard(path, destinationRoot, null, out var card, out var error))
                prepared.Add((card!, path));
            else
                InlineUploadMessage = error;
        }
        await QueuePreparedUploadsAsync(prepared);
    }

    private bool TryCreateUploadCard(string path, string destinationRoot, string? folderGroup,
        out InlineUploadCard? card, out string error)
    {
        card = null;
        error = string.Empty;
        try
        {
            if (!CanAcceptFileDrop) throw new InvalidOperationException("只有可写的个人空间接受文件拖放。");
            if (!File.Exists(path)) throw new FileNotFoundException("所选文件不存在。", path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不能上传链接文件。");
            var name = Path.GetFileName(path);
            if (!LocalUploadWalker.IsSafeSegmentName(name)) throw new InvalidOperationException("文件名不适合作为个人空间路径。");
            var logicalPath = LogicalFileScope.Combine(destinationRoot, name);
            var operationId = Guid.NewGuid().ToString("D");
            card = new InlineUploadCard(operationId, name, Path.GetFullPath(path),
                _scope.ScopeId, _scope.ProjectId, logicalPath, folderGroup);
            _inlineUploadsByOperation[operationId] = card;
            InlineUploadCards.Add(card);
            OnPropertyChanged(nameof(IsEmpty));
            if (card.IsImage) _ = LoadInlinePreviewAsync(card);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            error = exception.Message;
            return false;
        }
    }

    private async Task QueuePreparedUploadsAsync(IReadOnlyList<(InlineUploadCard Card, string Path)> uploads)
    {
        if (uploads.Count == 0) return;
        InlineUploadMessage = string.Empty;
        using var concurrency = new SemaphoreSlim(4, 4);
        await Task.WhenAll(uploads.Select(async upload =>
        {
            await concurrency.WaitAsync();
            try
            {
                var operationId = await _transfers.QueueUploadAsync(upload.Path, upload.Card.ScopeId,
                    upload.Card.ProjectId, upload.Card.LogicalPath, displayName: upload.Card.DisplayName,
                    operationId: upload.Card.OperationId);
                if (operationId is null)
                    upload.Card.SetQueueError(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                        ? "无法将文件加入传输队列。" : _transfers.OperationErrorMessage);
                else
                    await _transfers.RefreshTasksAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TransferAgentException or TimeoutException)
            {
                upload.Card.SetQueueError(exception.Message);
            }
            finally { concurrency.Release(); }
        }));
    }

    private async Task QueueFolderUploadsAsync(string folder, string destinationParent, InlineUploadCard? retryCard = null)
    {
        if (!CanAcceptFileDrop) return;
        var scopeId = _scope.ScopeId;
        var projectId = _scope.ProjectId;
        if (retryCard is not null && (retryCard.ScopeId != scopeId || retryCard.ProjectId != projectId)) return;
        var root = Path.GetFullPath(folder);
        var prefix = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        if (!LocalUploadWalker.IsSafeSegmentName(prefix))
        {
            if (retryCard is not null) retryCard.SetLocalStatus("扫描失败，可重试", "文件夹名称不适合作为个人空间路径。", canRetry: true);
            else InlineUploadMessage = "文件夹名称不适合作为个人空间路径。";
            return;
        }

        var groupId = retryCard?.OperationId ?? Guid.NewGuid().ToString("D");
        var folderCard = retryCard ?? new InlineUploadCard(groupId, prefix, root,
            scopeId, projectId, destinationParent, groupId, isFolderBatch: true);
        if (retryCard is null)
        {
            _inlineUploadsByOperation[groupId] = folderCard;
            InlineUploadCards.Add(folderCard);
            OnPropertyChanged(nameof(IsEmpty));
        }
        folderCard.SetLocalStatus("正在扫描");
        InlineUploadMessage = string.Empty;
        LocalUploadPlan plan;
        try { plan = await Task.Run(() => LocalUploadWalker.Enumerate(root)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            folderCard.SetLocalStatus("扫描失败，可重试", exception.Message, canRetry: true);
            return;
        }

        if (!CanAcceptFileDrop || _scope.ScopeId != scopeId || _scope.ProjectId != projectId)
        {
            folderCard.SetLocalStatus("未进入队列，可重试", "空间已切换；未将文件加入其他空间。", canRetry: true);
            return;
        }

        var files = plan.Files.Select(entry => (entry.LocalPath,
            LogicalPath: LogicalFileScope.Combine(destinationParent,
                string.Join('/', entry.RelativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))))).ToArray();
        var directories = plan.Directories.Where(relative => !string.IsNullOrEmpty(relative))
            .Select(relative => LogicalFileScope.Combine(destinationParent,
                string.Join('/', relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))))
            .ToArray();
        var cards = new List<InlineUploadCard>(files.Length);
        var operationIds = new List<string>(files.Length);
        var preparationErrors = plan.Warnings.ToList();
        foreach (var file in files)
        {
            var path = file.LocalPath;
            if (!TryCreateUploadCard(path, LogicalFileScope.Parent(file.LogicalPath), groupId,
                    out var card, out var error))
            {
                preparationErrors.Add(error);
                continue;
            }
            cards.Add(card!);
            operationIds.Add(card!.OperationId);
        }

        var validFiles = files.Where(file => cards.Any(card => card.LocalPath == Path.GetFullPath(file.LocalPath))).ToArray();
        FolderUploadEnqueueResult result;
        try
        {
            result = await _transfers.AddFolderUploadAsync(prefix, groupId, scopeId, projectId,
                validFiles,
            directories, operationIds: operationIds);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TransferAgentException or TimeoutException)
        {
            result = new FolderUploadEnqueueResult(0, exception.Message, []);
        }
        var accepted = new HashSet<string>(result.QueuedOperationIds, StringComparer.Ordinal);
        foreach (var card in cards.Where(card => !accepted.Contains(card.OperationId)))
            card.SetQueueError(result.ErrorMessage ?? "此文件未能进入传输队列。");
        await _transfers.RefreshTasksAsync().ConfigureAwait(true);

        if (files.Length == 0 && string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            folderCard.SetLocalStatus(plan.Warnings.Count == 0 ? "空文件夹已创建" : "文件夹结构已创建",
                plan.Warnings.Count == 0 ? null : string.Join("；", plan.Warnings.Take(5)));
        }
        else if (accepted.Count > 0)
        {
            _inlineUploadsByOperation.Remove(groupId);
            InlineUploadCards.Remove(folderCard);
            OnPropertyChanged(nameof(IsEmpty));
        }
        else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            folderCard.SetLocalStatus("上传未开始，可重试", result.ErrorMessage, canRetry: true);
        }
        else if (plan.Warnings.Count > 0)
        {
            folderCard.SetLocalStatus("部分内容已跳过", string.Join("；", preparationErrors.Take(5)));
        }
        else if (validFiles.Length > 0)
        {
            folderCard.SetLocalStatus("上传未开始，可重试", "没有文件被 Rust 代理接受。", canRetry: true);
        }
        else if (preparationErrors.Count > 0)
        {
            folderCard.SetLocalStatus("部分内容已跳过", string.Join("；", preparationErrors.Take(5)));
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            InlineUploadMessage = result.ErrorMessage;
        else if (preparationErrors.Count > 0)
            InlineUploadMessage = string.Join("；", preparationErrors.Take(5));
        else
            InlineUploadMessage = string.Empty;
    }

    private async Task LoadInlinePreviewAsync(InlineUploadCard card)
    {
        try
        {
            var preview = await Task.Run(() =>
            {
                using var stream = new FileStream(card.LocalPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 480;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            });
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            if (dispatcher.CheckAccess()) card.LocalPreview = preview;
            else await dispatcher.InvokeAsync(() => card.LocalPreview = preview);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            // The placeholder remains useful if the local file cannot be decoded.
        }
    }

    private async Task RetryInlineUploadAsync(InlineUploadCard? card)
    {
        if (card is null || !card.CanRetry) return;
        if (card.IsFolderBatch)
        {
            await QueueFolderUploadsAsync(card.LocalPath, card.LogicalPath, card);
            return;
        }
        var snapshot = _transfers.TaskSnapshots.FirstOrDefault(item => item.OperationId == card.OperationId);
        if (snapshot is not null)
        {
            _ = await _transfers.RetryOperationAsync(card.OperationId);
            return;
        }

        try
        {
            var operationId = await _transfers.QueueUploadAsync(card.LocalPath, card.ScopeId,
                card.ProjectId, card.LogicalPath, displayName: card.DisplayName, operationId: card.OperationId);
            if (operationId is null)
                card.SetQueueError(string.IsNullOrWhiteSpace(_transfers.OperationErrorMessage)
                    ? "无法将文件重新加入传输队列。" : _transfers.OperationErrorMessage);
            else
                await _transfers.RefreshTasksAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TransferAgentException or TimeoutException)
        {
            card.SetQueueError(exception.Message);
        }
    }

    private async Task CancelInlineUploadAsync(InlineUploadCard? card)
    {
        if (card is null || !card.CanCancel) return;
        _ = await _transfers.CancelOperationAsync(card.OperationId);
    }

    private async Task RetryInlineDownloadAsync(DriveFileItem? item)
    {
        if (item is null || !item.InlineTransferCanRetry) return;
        if (_transfers.TaskSnapshots.Any(snapshot => snapshot.OperationId == item.InlineTransferOperationId))
            _ = await _transfers.RetryOperationAsync(item.InlineTransferOperationId!);
        else
            await DownloadItemAsync(item);
    }

    private async Task CancelInlineDownloadAsync(DriveFileItem? item)
    {
        if (item?.InlineTransferOperationId is not { Length: > 0 } operationId || !item.InlineTransferCanCancel) return;
        _ = await _transfers.CancelOperationAsync(operationId);
    }

    private void HandleTransferTaskUpdated(TransferAgentTaskSnapshot snapshot)
    {
        if (snapshot.Source == "wpf" && snapshot.Kind.Contains("upload", StringComparison.OrdinalIgnoreCase) &&
            snapshot.ScopeId is not null && snapshot.LogicalPath is not null &&
            string.Equals(snapshot.ScopeId, _scope.ScopeId, StringComparison.Ordinal) &&
            string.Equals(snapshot.ProjectId, _scope.ProjectId, StringComparison.Ordinal))
        {
            var card = GetOrCreateInlineUploadCard(snapshot);
            card.Apply(snapshot);
            RefreshInlineUploadCardsForCurrentDirectory();
            if (snapshot.State == "completed") OnPropertyChanged(nameof(IsEmpty));
        }

        if (snapshot.Source == "wpf" && snapshot.Kind.Contains("download", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(snapshot.FileId) && snapshot.ScopeId is not null)
        {
            foreach (var item in Files.Where(file => file.FileId == snapshot.FileId && file.RevisionId == snapshot.RevisionId
                         && file.ScopeId == snapshot.ScopeId && file.ProjectId == snapshot.ProjectId))
                item.ApplyInlineTransfer(snapshot);
            RetryInlineDownloadCommand.RaiseCanExecuteChanged();
            CancelInlineDownloadCommand.RaiseCanExecuteChanged();
        }
    }

    private InlineUploadCard GetOrCreateInlineUploadCard(TransferAgentTaskSnapshot snapshot)
    {
        if (_inlineUploadsByOperation.TryGetValue(snapshot.OperationId, out var existing)) return existing;
        var path = snapshot.LogicalPath ?? string.Empty;
        var sourcePath = snapshot.SourcePath ?? string.Empty;
        var card = new InlineUploadCard(snapshot.OperationId,
            string.IsNullOrWhiteSpace(snapshot.DisplayName) ? Path.GetFileName(path) : snapshot.DisplayName,
            sourcePath, snapshot.ScopeId ?? string.Empty, snapshot.ProjectId, path, snapshot.FolderGroup);
        _inlineUploadsByOperation[snapshot.OperationId] = card;
        if (card.IsImage && File.Exists(card.LocalPath)) _ = LoadInlinePreviewAsync(card);
        return card;
    }

    private void RefreshInlineUploadCardsFromSnapshots()
    {
        foreach (var snapshot in _transfers.TaskSnapshots.Where(item =>
                     item.Source == "wpf" && item.Kind.Contains("upload", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(item.ScopeId) && !string.IsNullOrWhiteSpace(item.LogicalPath) &&
                     string.Equals(item.ScopeId, _scope.ScopeId, StringComparison.Ordinal) &&
                     string.Equals(item.ProjectId, _scope.ProjectId, StringComparison.Ordinal)))
            GetOrCreateInlineUploadCard(snapshot).Apply(snapshot);
        RefreshInlineUploadCardsForCurrentDirectory();
    }

    private void RefreshInlineUploadCardsForCurrentDirectory()
    {
        InlineUploadCards.Clear();
        foreach (var card in _inlineUploadsByOperation.Values
                     .Where(card => string.Equals(card.ScopeId, _scope.ScopeId, StringComparison.Ordinal) &&
                                    string.Equals(card.ProjectId, _scope.ProjectId, StringComparison.Ordinal) &&
                                    IsWithinCurrentDirectory(card.LogicalPath))
                     .OrderByDescending(card => card.Snapshot?.UpdatedAt ?? DateTimeOffset.MinValue))
            InlineUploadCards.Add(card);
        OnPropertyChanged(nameof(IsEmpty));
        RetryInlineUploadCommand.RaiseCanExecuteChanged();
        CancelInlineUploadCommand.RaiseCanExecuteChanged();
    }

    private bool IsWithinCurrentDirectory(string logicalPath) =>
        string.IsNullOrEmpty(_currentDirectory) ||
        logicalPath.StartsWith(_currentDirectory + "/", StringComparison.Ordinal);

    private void ApplyExistingDownloadSnapshots()
    {
        foreach (var item in Files.Where(file => !string.IsNullOrWhiteSpace(file.FileId)))
        {
            var snapshot = _transfers.TaskSnapshots
                .Where(task => task.Kind.Contains("download", StringComparison.OrdinalIgnoreCase) &&
                               task.Source == "wpf" &&
                               task.FileId == item.FileId && task.RevisionId == item.RevisionId &&
                               task.ScopeId == item.ScopeId && task.ProjectId == item.ProjectId)
                .OrderByDescending(task => task.UpdatedAt)
                .FirstOrDefault();
            if (snapshot is not null) item.ApplyInlineTransfer(snapshot);
        }
        RetryInlineDownloadCommand.RaiseCanExecuteChanged();
        CancelInlineDownloadCommand.RaiseCanExecuteChanged();
    }

    private void RemoveCommittedInlineUploads()
    {
        var committedIds = InlineUploadCards
            .Where(card => card.Snapshot?.State == "completed" &&
                           !string.IsNullOrWhiteSpace(card.Snapshot.FileId) &&
                           Files.Any(file => file.FileId == card.Snapshot.FileId))
            .Select(card => card.OperationId)
            .ToArray();
        foreach (var operationId in committedIds)
        {
            _inlineUploadsByOperation.Remove(operationId);
            var card = InlineUploadCards.FirstOrDefault(item => item.OperationId == operationId);
            if (card is not null) InlineUploadCards.Remove(card);
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task RenameAsync(DriveFileItem? item)
    {
        if (item is null) return;
        var extension = item.IsDirectory ? string.Empty : Path.GetExtension(item.Name);
        var dialog = new TextInputWindow("重命名", $"为“{item.Name}”输入新名称。", item.Name, "重命名", item.Name.Length - extension.Length);
        ShowOwned(dialog);
        if (dialog.ShowDialog() != true) return;
        var newName = dialog.Value.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        if (!item.IsDirectory && string.IsNullOrEmpty(Path.GetExtension(newName))) newName += extension;
        var target = LogicalFileScope.Combine(LogicalFileScope.Parent(item.LogicalPath), newName);
        await MoveToAsync(item, target);
    }

    private async Task MoveAsync(DriveFileItem? item)
    {
        if (item is null) return;
        var dialog = new TextInputWindow("移动到", "输入目标逻辑路径（相对于当前项目或个人空间）。", item.LogicalPath, "移动");
        ShowOwned(dialog);
        if (dialog.ShowDialog() != true) return;
        await MoveToAsync(item, dialog.Value.Trim());
    }

    private async Task MoveToAsync(DriveFileItem item, string targetPath)
    {
        try
        {
            var target = LogicalFileScope.Normalize(targetPath);
            if (item.IsDirectory)
            {
                await _api.MutateDirectoryAsync(_session.SessionToken, item.DirectoryId!, new DirectoryOperationRequest
                {
                    OperationId = Guid.NewGuid().ToString("D"), ClientId = _clientId, Action = "move", ScopeId = item.ScopeId,
                    ProjectId = item.ProjectId, ExpectedVersion = item.Version,
                    TargetScopeId = _scope.ScopeId, TargetProjectId = _scope.ProjectId, TargetLogicalPath = target
                });
            }
            else
            {
                await _api.BeginFileOperationAsync(_session.SessionToken, new FileOperationRequest
                {
                    OperationId = Guid.NewGuid().ToString("D"), Action = "move", ClientId = _clientId,
                    Source = "wpf", FileId = item.FileId, ExpectedVersion = item.Version,
                    ScopeId = item.ScopeId, ProjectId = item.ProjectId,
                    TargetScopeId = _scope.ScopeId, TargetProjectId = _scope.ProjectId, TargetLogicalPath = target
                });
            }
            await LoadCurrentDirectoryAsync();
        }
        catch (V2ApiException exception) { MessageBox.Show(exception.Message, "无法移动", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private async Task DeleteAsync(DriveFileItem? clicked)
    {
        var targets = TargetsFor(clicked);
        if (targets.Count == 0) return;
        var dialog = new ConfirmDialog(targets.Count == 1 ? "确认删除" : $"确认删除这 {targets.Count} 项",
            "删除后会移入回收站，文件内容和历史版本仍可恢复。", targets.Select(item => item.Name).ToArray(), "删除");
        ShowOwned(dialog);
        if (dialog.ShowDialog() != true) return;
        var failures = new List<string>();
        foreach (var item in targets)
        {
            try
            {
                if (item.IsDirectory)
                    await _api.MutateDirectoryAsync(_session.SessionToken, item.DirectoryId!, new DirectoryOperationRequest
                    {
                        OperationId = Guid.NewGuid().ToString("D"), ClientId = _clientId, Action = "delete", ScopeId = item.ScopeId,
                        ProjectId = item.ProjectId, ExpectedVersion = item.Version
                    });
                else
                    await _api.BeginFileOperationAsync(_session.SessionToken, new FileOperationRequest
                    {
                        OperationId = Guid.NewGuid().ToString("D"), Action = "delete", ClientId = _clientId,
                        Source = "wpf", ScopeId = item.ScopeId, ProjectId = item.ProjectId,
                        FileId = item.FileId, ExpectedVersion = item.Version
                    });
            }
            catch (V2ApiException exception) { failures.Add($"{item.Name}：{exception.Message}"); }
        }
        if (failures.Count > 0) MessageBox.Show(string.Join("\n", failures.Take(10)), "部分项目未删除", MessageBoxButton.OK, MessageBoxImage.Information);
        await LoadCurrentDirectoryAsync();
    }

    private async Task RestoreAsync(DriveFileItem? item)
    {
        if (item is null) return;
        try
        {
            if (item.IsDirectory)
                await _api.MutateDirectoryAsync(_session.SessionToken, item.DirectoryId!, new DirectoryOperationRequest
                {
                    OperationId = Guid.NewGuid().ToString("D"), ClientId = _clientId, Action = "restore", ScopeId = item.ScopeId,
                    ProjectId = item.ProjectId, ExpectedVersion = item.Version
                });
            else
            {
                var revisions = await _api.ListFileRevisionsAsync(_session.SessionToken, item.FileId!);
                var revision = revisions.Items.OrderByDescending(candidate => candidate.RevisionNumber).FirstOrDefault();
                if (revision is null) throw new InvalidOperationException("没有可恢复的文件版本。");
                await _api.RestoreFileRevisionAsync(_session.SessionToken, item.FileId!, Guid.NewGuid().ToString("D"),
                    revision.RevisionId, item.Version, _clientId);
            }
            await LoadCurrentDirectoryAsync();
        }
        catch (Exception exception) when (exception is V2ApiException or InvalidOperationException)
        { MessageBox.Show(exception.Message, "无法恢复", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private async Task ShowHistoryAsync(DriveFileItem? item)
    {
        if (item?.FileId is not { Length: > 0 } fileId) return;
        try
        {
            var history = await _api.ListFileRevisionsAsync(_session.SessionToken, fileId);
            var canRestoreRevision = CanWriteOrRestoreProjectFiles;
            var window = new FileHistoryWindow(item.Name, history.Items, isReadOnly: !canRestoreRevision);
            ShowOwned(window);
            if (window.ShowDialog() == true && canRestoreRevision && window.SelectedRevision is { } revision)
            {
                await _api.RestoreFileRevisionAsync(_session.SessionToken, fileId, Guid.NewGuid().ToString("D"),
                    revision.RevisionId, item.Version, _clientId);
                await LoadCurrentDirectoryAsync();
            }
        }
        catch (V2ApiException exception) { MessageBox.Show(exception.Message, "无法读取版本历史", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void ToggleRecycleBin()
    {
        if (!CanShowRecycleBin) return;
        IsRecycleBin = !IsRecycleBin;
        OnPropertyChanged(nameof(EmptyMessage));
        _ = LoadCurrentDirectoryAsync();
    }

    private async Task LoadCurrentDirectoryAsync(bool preserveSelection = false)
    {
        var requestId = Interlocked.Increment(ref _loadRequestId);
        IsLoading = true; ErrorMessage = string.Empty; TruncationNotice = string.Empty;
        try
        {
            var items = new List<LogicalFileEntry>();
            string? cursor = null;
            var pageCount = 0;
            do
            {
                var page = await _api.ListFilesAsync(_session.SessionToken, _scope.ScopeId, _scope.ProjectId,
                    _scope.Flatten ? string.Empty : _currentDirectory, _scope.MediaKind, IsRecycleBin,
                    cursor, limit: 500);
                items.AddRange(page.Items);
                cursor = page.NextCursor;
                pageCount++;
            } while (!string.IsNullOrWhiteSpace(cursor) && pageCount < 40);
            if (!string.IsNullOrWhiteSpace(cursor)) TruncationNotice = "项目较多，当前只显示前 20,000 项。";
            if (requestId != _loadRequestId) return;

            Files.Clear();
            foreach (var entry in items)
                Files.Add(ToDriveFileItem(entry));
            ApplyExistingDownloadSnapshots();
            RemoveCommittedInlineUploads();
            ItemsView.Refresh();
            OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyMessage));
            if (!preserveSelection) { SelectionRequested?.Invoke(SelectionRequest.Clear); UpdateSelection([]); }
        }
        catch (V2ApiException exception)
        {
            if (requestId != _loadRequestId) return;
            ErrorMessage = exception.Message; Files.Clear(); OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            if (requestId != _loadRequestId) return;
            ErrorMessage = "文件服务暂时不可用，请检查网络后重试。"; Files.Clear(); OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            if (requestId == _loadRequestId) IsLoading = false;
        }
    }

    private DriveFileItem ToDriveFileItem(LogicalFileEntry entry)
    {
        var size = entry.Size ?? 0;
        var updated = entry.UpdatedAt?.LocalDateTime ?? DateTime.MinValue;
        var mediaType = entry.MediaType ?? string.Empty;
        var canRequestThumbnail = !entry.IsDirectory && !entry.DeletedAt.HasValue &&
            !string.IsNullOrWhiteSpace(entry.FileId) && !string.IsNullOrWhiteSpace(entry.CurrentRevisionId) &&
            (string.Equals(entry.MediaKind, "image", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(entry.MediaKind, "video", StringComparison.OrdinalIgnoreCase) ||
             mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
             mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
        var thumbnailPath = canRequestThumbnail
            ? _thumbnailProvider.GetCachePath(entry.FileId!, entry.CurrentRevisionId!)
            : string.Empty;
        return new DriveFileItem
        {
            Id = entry.DirectoryId ?? entry.FileId ?? entry.LogicalPath,
            Name = entry.Name,
            FullPath = entry.LogicalPath,
            LogicalPath = entry.LogicalPath,
            ThumbnailPath = thumbnailPath,
            HasThumbnail = false,
            FileSize = entry.IsDirectory ? "文件夹" : FormatSize(size),
            SizeBytes = size,
            KindLabel = entry.IsDirectory ? "文件夹" : entry.MediaType ?? entry.MediaKind ?? "other",
            ModifiedLabel = entry.DeletedAt.HasValue ? $"已删除 {entry.DeletedAt.Value.LocalDateTime:g}" : updated == DateTime.MinValue ? "" : updated.ToString("g"),
            ModifiedTime = updated,
            IsVideo = string.Equals(entry.MediaKind, "video", StringComparison.OrdinalIgnoreCase),
            IsDirectory = entry.IsDirectory,
            IsReadOnly = entry.DeletedAt.HasValue,
            FileId = entry.FileId,
            DirectoryId = entry.DirectoryId,
            ScopeId = entry.ScopeId,
            ProjectId = entry.ProjectId,
            RevisionId = entry.CurrentRevisionId,
            Version = entry.Version,
            DeletedAt = entry.DeletedAt,
            MediaType = entry.MediaType,
            FileThumbnailProvider = canRequestThumbnail ? _thumbnailProvider : null
        };
    }

    private async Task HandleUploadCompletedAsync(string logicalPath)
    {
        if (!string.Equals(LogicalFileScope.Parent(logicalPath), _currentDirectory, StringComparison.Ordinal)) return;
        await LoadCurrentDirectoryAsync(preserveSelection: true).ConfigureAwait(true);
    }

    private void HandleUploadCompleted(string logicalPath)
    {
        _refreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refreshTimer.Tick -= RefreshTimerTick;
        _refreshTimer.Tick += RefreshTimerTick;
        _refreshTimer.Stop();
        _pendingRefreshLogicalPath = logicalPath;
        _refreshTimer.Start();
    }

    private void RefreshTimerTick(object? sender, EventArgs args)
    {
        if (_refreshTimer is null) return;
        _refreshTimer.Stop();
        if (_pendingRefreshLogicalPath is { } logicalPath) _ = HandleUploadCompletedAsync(logicalPath);
        _pendingRefreshLogicalPath = null;
    }

    private void NavigateUp()
    {
        if (!CanNavigateUp) return;
        _currentDirectory = LogicalFileScope.Parent(_currentDirectory);
        OnPropertyChanged(nameof(CurrentPathLabel)); OnPropertyChanged(nameof(CanNavigateUp)); NavigateUpCommand.RaiseCanExecuteChanged();
        RefreshInlineUploadCardsForCurrentDirectory();
        _ = LoadCurrentDirectoryAsync();
    }

    private IReadOnlyList<DriveFileItem> TargetsFor(DriveFileItem? clicked) => clicked is not null
        ? _selection.Contains(clicked) ? _selection : [clicked]
        : _selection;

    private void ShowOwned(Window dialog)
    {
        if (Application.Current?.MainWindow is { IsLoaded: true } owner) dialog.Owner = owner;
    }

    private static void ShowDetails(DriveFileItem? item)
    {
        if (item is null) return;
        MessageBox.Show($"名称：{item.Name}\n类型：{item.KindLabel}\n大小：{item.FileSize}\n修改时间：{item.ModifiedLabel}\n逻辑位置：{item.LogicalPath}\n版本：{item.Version}",
            "文件信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefreshCommandStates()
    {
        UploadCommand.RaiseCanExecuteChanged(); UploadFolderCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanAcceptFileDrop));
        RenameCommand.RaiseCanExecuteChanged(); MoveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged(); HistoryCommand.RaiseCanExecuteChanged(); RestoreCommand.RaiseCanExecuteChanged();
        ToggleRecycleBinCommand.RaiseCanExecuteChanged(); NavigateUpCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUpload)); OnPropertyChanged(nameof(CanModify)); OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanShowRecycleBin));
    }

    public void Dispose()
    {
        _transfers.UploadCompleted -= HandleUploadCompleted;
        _transfers.TaskUpdated -= HandleTransferTaskUpdated;
        _refreshTimer?.Stop(); _refreshTimer = null;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

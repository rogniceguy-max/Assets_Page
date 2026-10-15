using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
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
        ItemsView = CollectionViewSource.GetDefaultView(Files);
        ItemsView.Filter = FilterItem;
        SpaceTitle = ProjectNavigation.ImageGroupName;
        _transfers.UploadCompleted += HandleUploadCompleted;

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
        RenameCommand = new AsyncRelayCommand(parameter => RenameAsync(parameter as DriveFileItem), _ => CanModify);
        MoveCommand = new AsyncRelayCommand(parameter => MoveAsync(parameter as DriveFileItem), _ => CanModify);
        DeleteCommand = new AsyncRelayCommand(parameter => DeleteAsync(parameter as DriveFileItem), _ => CanModify);
    }

    public event Action<SelectionRequest>? SelectionRequested;
    public ObservableCollection<DriveFileItem> Files { get; }
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
    public bool IsEmpty => !IsLoading && !HasError && ItemsView.IsEmpty;
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
        SpaceTitle = title;
        _currentDirectory = scope.RootPath;
        IsRecycleBin = false;
        TruncationNotice = string.Empty;
        OnPropertyChanged(nameof(CurrentPathLabel));
        OnPropertyChanged(nameof(CanNavigateUp));
        OnPropertyChanged(nameof(IsReadOnly));
        RefreshCommandStates();
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
            _ = LoadCurrentDirectoryAsync();
            return;
        }
        _ = OpenFileAsync(item);
    }

    private async Task OpenFileAsync(DriveFileItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FileId)) return;
        try
        {
            var localPath = GetOpenCachePath(item);
            if (File.Exists(localPath)) { StartLocalFile(localPath); return; }
            var ticket = await _api.CreateFileDownloadTicketAsync(_session.SessionToken, item.FileId, _clientId, item.RevisionId);
            var operation = await _transfers.QueueDownloadAsync(ticket, localPath, item.Name);
            if (operation is null)
            {
                MessageBox.Show(_transfers.OperationErrorMessage, "无法打开文件", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _ = OpenWhenDownloadedAsync(operation, localPath);
        }
        catch (Exception exception) when (exception is V2ApiException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(exception.Message, "无法打开文件", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task OpenWhenDownloadedAsync(string operationId, string localPath)
    {
        try
        {
            await _transfers.WaitForTransferAsync(operationId).ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            if (dispatcher.CheckAccess()) StartLocalFile(localPath);
            else await dispatcher.InvokeAsync(() => StartLocalFile(localPath));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // The transfer task itself remains visible in the transfer queue with its failure state.
        }
    }

    private static void StartLocalFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { MessageBox.Show("无法通过 Windows 默认应用打开已下载文件。", "无法打开文件", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private async Task DownloadItemAsync(DriveFileItem? item)
    {
        if (item is null || item.IsDirectory || string.IsNullOrWhiteSpace(item.FileId)) return;
        var destination = _filePicker.PickDownloadDestination(item.Name);
        if (string.IsNullOrWhiteSpace(destination)) return;
        if (File.Exists(destination) || Directory.Exists(destination))
        { MessageBox.Show("本机目标位置已有同名项目，下载不会覆盖。", "无法下载", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            var ticket = await _api.CreateFileDownloadTicketAsync(_session.SessionToken, item.FileId, _clientId, item.RevisionId);
            if (!await _transfers.AddDownloadAsync(ticket, destination, item.Name))
                MessageBox.Show(_transfers.OperationErrorMessage, "无法开始下载", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (V2ApiException exception) { MessageBox.Show(exception.Message, "无法下载", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private string GetOpenCachePath(DriveFileItem item)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuYingDrive", "OpenFiles");
        Directory.CreateDirectory(root);
        var safeName = string.Concat(item.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.Combine(root, $"{item.FileId}-{item.RevisionId ?? item.Version.ToString()}-{safeName}");
    }

    private async Task UploadFolderAsync()
    {
        var folder = _filePicker.PickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;
        LocalUploadPlan plan;
        try { plan = await Task.Run(() => LocalUploadWalker.Enumerate(folder)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { MessageBox.Show(exception.Message, "无法读取文件夹", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (plan.Files.Count == 0 && plan.Directories.Count <= 1)
        { MessageBox.Show("所选文件夹是空的。", "无法上传", MessageBoxButton.OK, MessageBoxImage.Information); return; }

        var prefix = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destinationRoot = string.IsNullOrEmpty(_currentDirectory) ? prefix : LogicalFileScope.Combine(_currentDirectory, prefix);
        var files = plan.Files.Select(entry => (entry.LocalPath, LogicalFileScope.Combine(destinationRoot, entry.RelativePath.Replace('\\', '/')))).ToArray();
        var directories = plan.Directories.Where(relative => !string.IsNullOrEmpty(relative))
            .Select(relative => LogicalFileScope.Combine(destinationRoot, relative.Replace('\\', '/'))).Prepend(destinationRoot).ToArray();
        var groupId = Guid.NewGuid().ToString("D");
        var result = await _transfers.AddFolderUploadAsync(prefix, groupId, _scope.ScopeId, _scope.ProjectId,
            files, directories);
        if (result.QueuedCount > 0)
            MessageBox.Show($"已将文件夹「{prefix}」中的 {result.QueuedCount} 项加入传输队列。", "开始上传", MessageBoxButton.OK, MessageBoxImage.Information);
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            MessageBox.Show(result.ErrorMessage, "部分文件未进入队列", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PickUpload()
    {
        var paths = _filePicker.PickFiles();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            var logicalPath = string.IsNullOrEmpty(_currentDirectory) ? name : LogicalFileScope.Combine(_currentDirectory, name);
            _ = _transfers.AddUploadAsync(path, _scope.ScopeId, _scope.ProjectId, logicalPath, displayName: name);
        }
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
        RenameCommand.RaiseCanExecuteChanged(); MoveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged(); HistoryCommand.RaiseCanExecuteChanged(); RestoreCommand.RaiseCanExecuteChanged();
        ToggleRecycleBinCommand.RaiseCanExecuteChanged(); NavigateUpCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUpload)); OnPropertyChanged(nameof(CanModify)); OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanShowRecycleBin));
    }

    public void Dispose()
    {
        _transfers.UploadCompleted -= HandleUploadCompleted;
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

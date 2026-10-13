using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>
/// The login-free tour, rendered through the same file browser as the employee page.
/// </summary>
/// <remarks>
/// No write affordance exists here — not hidden, absent: <see cref="CanUpload"/> and
/// <see cref="CanModify"/> are constant false, the commands the view calls for those
/// actions are no-ops, and the backend exposes no write route at all. Reads go over
/// HTTPS as the backend's low-privilege visitor identity, so the tour never opens an
/// SMB connection that could disturb the employee's own NAS session.
/// </remarks>
public sealed class VisitorViewModel : ObservableObject, IFileBrowser
{
    private readonly MainViewModel _main;
    private readonly V2ApiClient _api;
    private readonly WindowsFilePickerService _filePicker = new();
    private readonly string _cacheRoot;
    private VisitorSessionResponse? _session;
    private VisitorSpace? _selectedSpace;
    private string _currentPath = string.Empty;
    private string _currentPathLabel = string.Empty;
    private string _spaceTitle = string.Empty;
    private string _searchText = string.Empty;
    private bool _isGridView = true;
    private bool _isLoading;
    private bool _isBusy;
    private int _selectedCount;
    private string _errorMessage = string.Empty;
    private int _loadRequestId;
    private bool _isNavigating;

    public VisitorViewModel(MainViewModel main, V2ApiClient api)
    {
        _main = main;
        _api = api;
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuYingDrive", "Visitor");
        Files = [];
        ItemsView = CollectionViewSource.GetDefaultView(Files);
        ItemsView.Filter = FilterItem;

        OpenCommand = new RelayCommand<DriveFileItem>(item => _ = OpenAsync(item));
        DownloadCommand = new RelayCommand<DriveFileItem>(item => _ = DownloadAsync(item));
        NavigateUpCommand = new RelayCommand(_ => _ = NavigateUpAsync(), _ => CanNavigateUp);
        ToggleGridCommand = new RelayCommand(_ => IsGridView = true);
        SelectAllCommand = new RelayCommand(_ => SelectionRequested?.Invoke(SelectionRequest.SelectAll));
        ClearSelectionCommand = new RelayCommand(_ => SelectionRequested?.Invoke(SelectionRequest.Clear));
        ToggleListCommand = new RelayCommand(_ => IsGridView = false);
        UploadCommand = new RelayCommand(_ => { }, _ => false);
        UnsupportedEditCommand = new RelayCommand<string>(_ => { }, _ => false);
        DetailsCommand = new RelayCommand<DriveFileItem>(ShowDetails);
        ExitCommand = new AsyncRelayCommand(_ => _main.ExitVisitorAsync(this));
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
    }

    public ObservableCollection<DriveFileItem> Files { get; }
    public ICollectionView ItemsView { get; }

    /// <summary>The three spaces as sidebar entries, for the shared workspace shell.</summary>
    public IReadOnlyList<WorkspaceNavItem> SidebarSpaces { get; private set; } = [];
    private readonly List<VisitorSpace> _spaces = [];

    public RelayCommand<DriveFileItem> OpenCommand { get; }
    public RelayCommand<DriveFileItem> DownloadCommand { get; }
    public RelayCommand NavigateUpCommand { get; }
    public RelayCommand ToggleGridCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand ToggleListCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand<string> UnsupportedEditCommand { get; }
    public RelayCommand<DriveFileItem> DetailsCommand { get; }
    public AsyncRelayCommand ExitCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    // Explicit interface implementations so the shared view sees only ICommand while
    // the concrete types stay available here.
    ICommand IFileBrowser.OpenCommand => OpenCommand;
    ICommand IFileBrowser.DownloadCommand => DownloadCommand;
    ICommand IFileBrowser.NavigateUpCommand => NavigateUpCommand;
    ICommand IFileBrowser.ToggleGridCommand => ToggleGridCommand;
    ICommand IFileBrowser.ToggleListCommand => ToggleListCommand;
    ICommand IFileBrowser.UploadCommand => UploadCommand;
    ICommand IFileBrowser.UnsupportedEditCommand => UnsupportedEditCommand;
    ICommand IFileBrowser.DetailsCommand => DetailsCommand;

    public event Action<SelectionRequest>? SelectionRequested;

    public VisitorSpace? SelectedSpace
    {
        get => _selectedSpace;
        private set { if (SetProperty(ref _selectedSpace, value)) OnPropertyChanged(nameof(IsSpaceSelected)); }
    }

    public bool IsSpaceSelected => _selectedSpace is not null;
    public string SpaceTitle { get => _spaceTitle; private set => SetProperty(ref _spaceTitle, value); }
    public bool IsReadOnly => true;
    public bool CanUpload => false;
    public bool CanModify => false;
    public string LoadingMessage => "正在读取参观内容…";
    public string EmptyMessage => string.IsNullOrWhiteSpace(SearchText) ? "此文件夹为空" : "没有匹配的文件";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ItemsView.Refresh();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(EmptyMessage));
            }
        }
    }

    public bool IsGridView { get => _isGridView; set => SetProperty(ref _isGridView, value); }

    public string CurrentPathLabel
    {
        get => _currentPathLabel;
        private set => SetProperty(ref _currentPathLabel, value);
    }

    public bool CanNavigateUp => !string.IsNullOrEmpty(_currentPath);
    public int SelectedCount { get => _selectedCount; private set => SetProperty(ref _selectedCount, value); }
    public string SelectedLabel => SelectedCount == 1 ? "已选择 1 项" : $"已选择 {SelectedCount} 项";
    public bool HasSelection => SelectedCount > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(IsEmpty)); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RefreshCommand.RaiseCanExecuteChanged(); }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsLoading && !HasError && ItemsView.IsEmpty;

    public string? SessionToken => _session?.SessionToken;

    public void UpdateSelectionCount(int selectedCount)
    {
        SelectedCount = selectedCount;
        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(HasSelection));
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            _session = await _api.CreateVisitorSessionAsync().ConfigureAwait(true);
            _spaces.Clear();
            _spaces.AddRange(_session.Spaces);
            // Icons follow the employee sidebar's meaning: a media library is a folder,
            // the shared assets space is imagery.
            SidebarSpaces = _spaces
                .Select(space => new WorkspaceNavItem(
                    space.Id, space.Title, space.Id == "public" ? "Image" : "FolderOpen"))
                .ToArray();

            if (_spaces.Count == 0)
            {
                ErrorMessage = "参观暂未开放，请稍后再试。";
                return;
            }
            await SelectSpaceAsync(_spaces[0]).ConfigureAwait(true);
        }
        catch (V2ApiException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException)
        {
            ErrorMessage = "无法进入参观模式，请检查网络后重试。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Revokes the server session and clears the local tour cache.</summary>
    public async Task EndAsync()
    {
        var token = _session?.SessionToken;
        _session = null;
        if (!string.IsNullOrEmpty(token))
        {
            try { await _api.EndVisitorSessionAsync(token).ConfigureAwait(true); }
            catch (V2ApiException) { /* The session expires on its own. */ }
            catch (HttpRequestException) { }
        }

        try
        {
            if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool FilterItem(object candidate) =>
        string.IsNullOrWhiteSpace(SearchText) ||
        candidate is not DriveFileItem item ||
        item.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Selects a space by its server-side id, as the sidebar does.</summary>
    public Task SelectSpaceByKeyAsync(string spaceId) =>
        SelectSpaceAsync(_spaces.FirstOrDefault(space => space.Id == spaceId));

    public ValueTask DisposeAsync() => new(EndAsync());

    private async Task SelectSpaceAsync(VisitorSpace? space)
    {
        if (space is null) return;
        SelectedSpace = space;
        SpaceTitle = space.Title;
        _currentPath = string.Empty;
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task NavigateUpAsync()
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        var trimmed = _currentPath.TrimEnd('/');
        _currentPath = trimmed.Contains('/') ? trimmed[..trimmed.LastIndexOf('/')] : string.Empty;
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        if (_session is null || SelectedSpace is null) return;
        var requestId = Interlocked.Increment(ref _loadRequestId);
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var listing = await _api.ListVisitorAsync(_session.SessionToken, SelectedSpace.Id, _currentPath)
                .ConfigureAwait(true);
            if (requestId != _loadRequestId) return;

            Files.Clear();
            foreach (var entry in listing.Entries)
                Files.Add(ToItem(entry));
            CurrentPathLabel = BuildLabel(listing.Path);
            UpdateSelectionCount(0);
            OnPropertyChanged(nameof(CanNavigateUp));
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (V2ApiException exception)
        {
            if (requestId == _loadRequestId) ErrorMessage = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException)
        {
            if (requestId == _loadRequestId) ErrorMessage = "无法读取该目录，请稍后重试。";
        }
        finally
        {
            if (requestId == _loadRequestId) IsLoading = false;
        }
    }

    private string BuildLabel(string path)
    {
        var label = SpaceTitle;
        if (!string.IsNullOrEmpty(path))
            label += " / " + path.Replace('/', ' ');
        return label;
    }

    private static DriveFileItem ToItem(VisitorEntry entry)
    {
        var modified = DateTimeOffset.FromUnixTimeSeconds(entry.ModifiedUtc).ToLocalTime().DateTime;
        return new DriveFileItem
        {
            Id = entry.Name,
            Name = entry.Name,
            FullPath = entry.Name,
            // Visitor files are not on a local path; the tile shows its placeholder.
            ThumbnailPath = string.Empty,
            SizeBytes = entry.IsDirectory ? 0 : entry.Size,
            FileSize = entry.IsDirectory ? "—" : FormatSize(entry.Size),
            KindLabel = entry.IsDirectory ? "文件夹" : KindOf(entry.Name),
            ModifiedTime = modified,
            ModifiedLabel = modified.ToString("yyyy/M/d HH:mm"),
            IsDirectory = entry.IsDirectory,
            IsReadOnly = true
        };
    }

    private static string KindOf(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" or ".heic" or ".avif" => "图片",
            ".mp4" or ".mov" or ".m4v" or ".avi" or ".mkv" or ".webm" or ".wmv" or ".mpeg" or ".mpg" => "视频",
            _ => "文件"
        };
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private void ShowDetails(DriveFileItem? item)
    {
        if (item is null) return;
        MessageBox.Show(
            $"名称：{item.Name}\n类型：{item.KindLabel}\n大小：{item.FileSize}\n修改时间：{item.ModifiedLabel}\n权限：只读",
            "文件信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task OpenAsync(DriveFileItem? item)
    {
        if (item is null || _session is null || SelectedSpace is null) return;
        if (item.IsDirectory)
        {
            // A double click raises the command twice. Without this guard the second
            // invocation appends the folder name again — ".../影视项目/影视项目" — and
            // the listing comes back "not found" while the first load's contents are
            // still on screen.
            if (_isNavigating) return;
            _isNavigating = true;
            try
            {
                _currentPath = string.IsNullOrEmpty(_currentPath) ? item.Name : _currentPath + "/" + item.Name;
                await LoadAsync().ConfigureAwait(true);
            }
            finally
            {
                _isNavigating = false;
            }
            return;
        }

        IsBusy = true;
        try
        {
            var relative = RelativePathOf(item);
            var local = Path.Combine(_cacheRoot, SelectedSpace.Id,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(local))
            {
                await _api.DownloadVisitorFileAsync(_session.SessionToken, SelectedSpace.Id, relative, local)
                    .ConfigureAwait(true);
            }
            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (V2ApiException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception
            or InvalidOperationException or UnauthorizedAccessException or HttpRequestException)
        {
            MessageBox.Show("无法打开此文件。可以改用右键菜单中的“下载”保存到本机。", "无法打开文件",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadAsync(DriveFileItem? item)
    {
        if (item is null || item.IsDirectory || _session is null || SelectedSpace is null) return;
        var target = _filePicker.PickDownloadDestination(item.Name);
        if (target is null) return;

        IsBusy = true;
        try
        {
            await _api.DownloadVisitorFileAsync(_session.SessionToken, SelectedSpace.Id, RelativePathOf(item),
                target).ConfigureAwait(true);
        }
        catch (V2ApiException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            ErrorMessage = "下载失败，请稍后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string RelativePathOf(DriveFileItem item) =>
        string.IsNullOrEmpty(_currentPath) ? item.Name : _currentPath + "/" + item.Name;
}

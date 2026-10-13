using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>
/// The login-free tour: three shared libraries, read-only.
/// </summary>
/// <remarks>
/// No write affordance exists anywhere in this view model — not hidden, absent. Reads
/// go over HTTPS as the backend's low-privilege visitor identity; the client never
/// opens an SMB connection for a visitor, so a tour cannot disturb the employee's own
/// NAS session.
/// </remarks>
public sealed class VisitorViewModel : ObservableObject
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
    private bool _isLoading;
    private bool _isBusy;
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
        Items = [];
        OpenCommand = new RelayCommand<DriveFileItem>(item => _ = OpenAsync(item));
        DownloadCommand = new RelayCommand<DriveFileItem>(item => _ = DownloadAsync(item));
        SpaceCommand = new RelayCommand(parameter => _ = SelectSpaceAsync(parameter as VisitorSpace));
        NavigateUpCommand = new RelayCommand(_ => _ = NavigateUpAsync(), _ => CanNavigateUp);
        ExitCommand = new AsyncRelayCommand(_ => _main.ExitVisitorAsync(this));
        RefreshCommand = new AsyncRelayCommand(_ => ReloadAsync(), _ => !IsBusy);
    }

    public ObservableCollection<DriveFileItem> Items { get; }
    public ObservableCollection<VisitorSpace> Spaces { get; } = [];
    public RelayCommand<DriveFileItem> OpenCommand { get; }
    public RelayCommand<DriveFileItem> DownloadCommand { get; }
    public RelayCommand SpaceCommand { get; }
    public RelayCommand NavigateUpCommand { get; }
    public AsyncRelayCommand ExitCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public VisitorSpace? SelectedSpace
    {
        get => _selectedSpace;
        private set { if (SetProperty(ref _selectedSpace, value)) OnPropertyChanged(nameof(IsSpaceSelected)); }
    }

    public bool IsSpaceSelected => _selectedSpace is not null;
    public string SpaceTitle { get => _spaceTitle; private set => SetProperty(ref _spaceTitle, value); }
    public string CurrentPathLabel { get => _currentPathLabel; private set => SetProperty(ref _currentPathLabel, value); }
    public bool CanNavigateUp => !string.IsNullOrEmpty(_currentPath);

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetProperty(ref _isLoading, value)) { OnPropertyChanged(nameof(IsEmpty)); } }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsLoading && !HasError && Items.Count == 0;

    /// <summary>The visitor's session token, handed back when the tour ends.</summary>
    public string? SessionToken => _session?.SessionToken;

    public async Task InitializeAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            _session = await _api.CreateVisitorSessionAsync().ConfigureAwait(true);
            Spaces.Clear();
            foreach (var space in _session.Spaces)
                Spaces.Add(space);

            if (Spaces.Count == 0)
            {
                ErrorMessage = "参观暂未开放，请稍后再试。";
                return;
            }
            await SelectSpaceAsync(Spaces[0]).ConfigureAwait(true);
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

    /// <summary>Releases the local cache of downloaded tour files.</summary>
    public async Task EndAsync()
    {
        var token = _session?.SessionToken;
        _session = null;
        if (!string.IsNullOrEmpty(token))
        {
            try { await _api.EndVisitorSessionAsync(token).ConfigureAwait(true); }
            catch (V2ApiException) { /* The session expires on its own; nothing to recover. */ }
            catch (HttpRequestException) { }
        }

        try
        {
            if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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

    private Task ReloadAsync() => LoadAsync();

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

            Items.Clear();
            foreach (var entry in listing.Entries)
                Items.Add(ToItem(entry));
            CurrentPathLabel = BuildLabel(listing.Path);
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
            var local = await FetchAsync(item).ConfigureAwait(true);
            if (local is null) return;
            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show("无法通过 Windows 默认应用打开此文件。请改用“下载”。", "无法打开文件",
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

    /// <summary>Downloads to the tour cache so opening the same file twice costs once.</summary>
    private async Task<string?> FetchAsync(DriveFileItem item)
    {
        var relative = RelativePathOf(item);
        var local = Path.Combine(_cacheRoot, SelectedSpace!.Id, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(local)) return local;

        await _api.DownloadVisitorFileAsync(_session!.SessionToken, SelectedSpace.Id, relative, local)
            .ConfigureAwait(true);
        return local;
    }

    private string RelativePathOf(DriveFileItem item) =>
        string.IsNullOrEmpty(_currentPath) ? item.Name : _currentPath + "/" + item.Name;
}

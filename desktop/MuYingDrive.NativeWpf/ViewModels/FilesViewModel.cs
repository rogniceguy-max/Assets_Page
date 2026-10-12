using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.ComponentModel;
using System.Windows.Data;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class FilesViewModel : ObservableObject
{
    private readonly TransferViewModel _transfers;
    private readonly WindowsFilePickerService _filePicker = new();
    private readonly SmbFileCatalog _catalog;
    private string _searchText = string.Empty;
    private bool _isGridView = true;
    private string _spaceTitle = string.Empty;
    private int _selectedCount;
    private string _currentRoot = string.Empty;
    private string _currentDirectory = string.Empty;
    private bool _isLoading;
    private string _errorMessage = string.Empty;
    private DriveSpaceKind _currentSpace = DriveSpaceKind.Department;
    private readonly string _departmentName;
    private int _loadRequestId;
    private readonly bool _smbConnected;
    private readonly string _smbConnectionError;

    public FilesViewModel(DriveSession session, NasClientSettings settings, TransferViewModel transfers)
    {
        ArgumentNullException.ThrowIfNull(session);
        _transfers = transfers;
        _catalog = new SmbFileCatalog(settings, session.User);
        _departmentName = session.User.Department;
        Files = [];
        ItemsView = CollectionViewSource.GetDefaultView(Files);
        ItemsView.Filter = FilterItem;
        SpaceTitle = session.User.Department;
        _currentRoot = _catalog.GetSpacePath(DriveSpaceKind.Department);
        _currentDirectory = _currentRoot;
        _smbConnected = session.SmbConnection.Succeeded;
        _smbConnectionError = session.SmbConnection.HasCredentialConflict
            ? "Windows 已使用其他凭据连接此 NAS。请在资源管理器中关闭该连接，再重新登录。"
            : "尚未建立 NAS 文件连接，请检查网络或账号凭据。";
        if (!session.SmbConnection.Succeeded)
            ErrorMessage = _smbConnectionError;

        ToggleGridCommand = new RelayCommand(_ => SetViewMode(true));
        ToggleListCommand = new RelayCommand(_ => SetViewMode(false));
        SelectAllCommand = new RelayCommand(_ => SelectionRequested?.Invoke(SelectionRequest.SelectAll));
        ClearSelectionCommand = new RelayCommand(_ => SelectionRequested?.Invoke(SelectionRequest.Clear));
        OpenCommand = new RelayCommand<DriveFileItem>(OpenItem);
        DownloadCommand = new RelayCommand<DriveFileItem>(DownloadItem);
        UnsupportedEditCommand = new RelayCommand<string>(ShowDemoNotice);
        DetailsCommand = new RelayCommand<DriveFileItem>(ShowDetails);
        UploadCommand = new RelayCommand(_ => PickUpload(), _ => CanUpload);
        NavigateUpCommand = new RelayCommand(_ => NavigateUp(), _ => CanNavigateUp);
    }

    public event Action<SelectionRequest>? SelectionRequested;

    public ObservableCollection<DriveFileItem> Files { get; }
    public ICollectionView ItemsView { get; }

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
    public string SpaceTitle
    {
        get => _spaceTitle;
        private set
        {
            if (SetProperty(ref _spaceTitle, value))
                OnPropertyChanged(nameof(CurrentPathLabel));
        }
    }
    public int SelectedCount { get => _selectedCount; private set => SetProperty(ref _selectedCount, value); }
    public string SelectedLabel => SelectedCount == 1 ? "已选择 1 项" : $"已选择 {SelectedCount} 项";
    public bool HasSelection => SelectedCount > 0;
    public bool IsReadOnly => _currentSpace == DriveSpaceKind.OtherDepartment;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanUpload));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsLoading && !HasError && ItemsView.IsEmpty;
    public string EmptyMessage => string.IsNullOrWhiteSpace(SearchText) ? "此文件夹为空" : "没有匹配的文件";
    public bool CanUpload => _smbConnected && !IsReadOnly && !IsLoading;
    public bool CanNavigateUp => !string.Equals(_currentDirectory, _currentRoot, StringComparison.OrdinalIgnoreCase);
    public string CurrentPathLabel
    {
        get
        {
            if (!CanNavigateUp)
                return SpaceTitle;
            try
            {
                var relative = Path.GetRelativePath(_currentRoot, _currentDirectory).Replace('\\', '/');
                return relative == "." ? SpaceTitle : $"{SpaceTitle} / {relative}";
            }
            catch (ArgumentException)
            {
                return SpaceTitle;
            }
        }
    }

    public RelayCommand ToggleGridCommand { get; }
    public RelayCommand ToggleListCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand<DriveFileItem> OpenCommand { get; }
    public RelayCommand<DriveFileItem> DownloadCommand { get; }
    public RelayCommand<string> UnsupportedEditCommand { get; }
    public RelayCommand<DriveFileItem> DetailsCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand NavigateUpCommand { get; }

    public async Task InitializeAsync() => await LoadCurrentDirectoryAsync();

    public void SetSpace(string space)
    {
        _currentSpace = space switch
        {
            "本部门" => DriveSpaceKind.Department,
            "公共素材" => DriveSpaceKind.Public,
            "个人空间" => DriveSpaceKind.Personal,
            "其他部门" => DriveSpaceKind.OtherDepartment,
            _ => DriveSpaceKind.Department
        };
        SpaceTitle = _currentSpace switch
        {
            DriveSpaceKind.Department => _departmentName,
            DriveSpaceKind.Public => "公共素材",
            DriveSpaceKind.Personal => "个人空间",
            DriveSpaceKind.OtherDepartment => _departmentName == "影视资源库" ? "文学创作库" : "影视资源库",
            _ => "本部门"
        };
        _currentRoot = _catalog.GetSpacePath(_currentSpace);
        _currentDirectory = _currentRoot;
        OnPropertyChanged(nameof(CurrentPathLabel));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanNavigateUp));
        NavigateUpCommand.RaiseCanExecuteChanged();
        UploadCommand.RaiseCanExecuteChanged();
        _ = LoadCurrentDirectoryAsync();
    }

    public void UpdateSelectionCount(int selectedCount)
    {
        SelectedCount = selectedCount;
        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void SetViewMode(bool showGrid)
    {
        if (IsGridView == showGrid)
        {
            return;
        }

        SelectionRequested?.Invoke(SelectionRequest.Clear);
        UpdateSelectionCount(0);
        IsGridView = showGrid;
    }

    private bool FilterItem(object item)
    {
        if (item is not DriveFileItem file)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(SearchText) || file.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void OpenItem(DriveFileItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsDirectory)
        {
            var candidate = Path.GetFullPath(item.FullPath);
            if (IsWithinCurrentRoot(candidate))
            {
                _currentDirectory = candidate;
                OnPropertyChanged(nameof(CurrentPathLabel));
                OnPropertyChanged(nameof(CanNavigateUp));
                NavigateUpCommand.RaiseCanExecuteChanged();
                _ = LoadCurrentDirectoryAsync();
            }
            return;
        }

        if (!_smbConnected)
        {
            MessageBox.Show(_smbConnectionError, "NAS 未连接", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("无法通过 Windows 默认应用打开此文件。请检查文件权限或关联软件。", "无法打开文件", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowDemoNotice(string? operation)
    {
        MessageBox.Show($"“{operation}”尚未接入 NAS。当前版本开放文件浏览、单文件上传和下载。", "功能尚未开放", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PickUpload()
    {
        var path = _filePicker.PickSingleFile();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var destination = Path.Combine(_currentDirectory, Path.GetFileName(path));
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                MessageBox.Show("目标位置已有同名项目，上传不会覆盖。", "无法上传", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _ = _transfers.AddUploadAsync(path, destination);
        }
    }

    private void DownloadItem(DriveFileItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_smbConnected)
        {
            MessageBox.Show(_smbConnectionError, "NAS 未连接", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (item.IsDirectory)
        {
            MessageBox.Show("当前阶段只支持单文件下载。", "无法下载文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var destination = _filePicker.PickDownloadDestination(item.Name);
        if (string.IsNullOrWhiteSpace(destination))
            return;
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            MessageBox.Show("本机目标位置已有同名项目，下载不会覆盖。", "无法下载", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _ = _transfers.AddDownloadAsync(item, destination);
    }

    private static void ShowDetails(DriveFileItem? item)
    {
        if (item is null)
            return;

        MessageBox.Show($"名称：{item.Name}\n类型：{item.KindLabel}\n大小：{item.FileSize}\n修改时间：{item.ModifiedLabel}\n位置：{item.FullPath}\n权限：{(item.IsReadOnly ? "只读" : "可写（最终以 NAS ACL 为准）")}",
            "文件信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task LoadCurrentDirectoryAsync()
    {
        var requestId = Interlocked.Increment(ref _loadRequestId);
        if (!_smbConnected)
        {
            ErrorMessage = _smbConnectionError;
            Files.Clear();
            IsLoading = false;
            OnPropertyChanged(nameof(CanUpload));
            UploadCommand.RaiseCanExecuteChanged();
            return;
        }
        IsLoading = true;
        ErrorMessage = string.Empty;
        UploadCommand.RaiseCanExecuteChanged();
        try
        {
            var items = await _catalog.EnumerateDirectoryAsync(_currentSpace, _currentDirectory);
            if (requestId != _loadRequestId)
                return;

            Files.Clear();
            foreach (var item in items)
                Files.Add(item);
            ItemsView.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
            SelectionRequested?.Invoke(SelectionRequest.Clear);
            UpdateSelectionCount(0);
        }
        catch (UnauthorizedAccessException)
        {
            if (requestId != _loadRequestId) return;
            ErrorMessage = "当前账号无权读取此 NAS 目录。";
            Files.Clear();
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (DirectoryNotFoundException)
        {
            if (requestId != _loadRequestId) return;
            ErrorMessage = "NAS 空间不存在或当前账号无权访问。";
            Files.Clear();
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (IOException)
        {
            if (requestId != _loadRequestId) return;
            ErrorMessage = "NAS 文件服务暂时不可用，请检查网络后重试。";
            Files.Clear();
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (InvalidOperationException)
        {
            if (requestId != _loadRequestId) return;
            ErrorMessage = "NAS 返回的空间路径无效，请联系管理员。";
            Files.Clear();
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            if (requestId == _loadRequestId)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(CanUpload));
                UploadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool IsWithinCurrentRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = _currentRoot.TrimEnd('\\') + "\\";
        return full.Equals(_currentRoot, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void NavigateUp()
    {
        if (!CanNavigateUp)
            return;
        var parent = Path.GetDirectoryName(_currentDirectory);
        if (parent is null || !IsWithinCurrentRoot(parent))
            return;
        _currentDirectory = parent;
        OnPropertyChanged(nameof(CurrentPathLabel));
        OnPropertyChanged(nameof(CanNavigateUp));
        NavigateUpCommand.RaiseCanExecuteChanged();
        _ = LoadCurrentDirectoryAsync();
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
}

public enum SelectionRequest
{
    SelectAll,
    Clear
}

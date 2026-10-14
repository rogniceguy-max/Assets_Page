using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.NativeWpf.Views;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class FilesViewModel : ObservableObject, IFileBrowser, IDisposable
{
    private readonly TransferViewModel _transfers;
    private readonly WindowsFilePickerService _filePicker = new();
    private readonly SmbSpaceResolver _spaces;
    private readonly SmbFileCatalog _catalog;
    private readonly SmbFileMutations _mutations;
    private string _searchText = string.Empty;
    private bool _isGridView = true;
    private string _spaceTitle = string.Empty;
    private IReadOnlyList<DriveFileItem> _selection = [];
    private string _currentRoot = string.Empty;
    private string _currentDirectory = string.Empty;
    private bool _isLoading;
    private string _errorMessage = string.Empty;
    // What the browser is currently pointed at: a root and the rules that apply inside it.
    // Assigned in the constructor before anything reads it.
    private SpaceScope _scope = null!;
    private int _loadRequestId;
    private readonly bool _smbConnected;
    private readonly string _smbConnectionError;

    public FilesViewModel(DriveSession session, NasClientSettings settings, TransferViewModel transfers)
    {
        ArgumentNullException.ThrowIfNull(session);
        _transfers = transfers;
        // One resolver for both halves: reads and writes must not disagree about what
        // "inside the share" means, which is what happened when each carried its own copy.
        _spaces = new SmbSpaceResolver(settings, session.User);
        _catalog = new SmbFileCatalog(_spaces);
        _mutations = new SmbFileMutations(_spaces);
        Files = [];
        ItemsView = CollectionViewSource.GetDefaultView(Files);
        ItemsView.Filter = FilterItem;
        // Lands on the global 图片 view, which is the first entry in the sidebar. The
        // workspace re-activates it on the way in, so this is the state a browser is in
        // before anything has been clicked rather than a second source of truth.
        SpaceTitle = ProjectNavigation.ImageGroupName;
        _scope = _spaces.ResolveProjectsScope(SpaceMediaKind.Images);
        _currentRoot = _scope.Root;
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
        DetailsCommand = new RelayCommand<DriveFileItem>(ShowDetails);
        UploadCommand = new RelayCommand(_ => PickUpload(), _ => CanUpload);
        NavigateUpCommand = new RelayCommand(_ => NavigateUp(), _ => CanNavigateUp);
        UploadFolderCommand = new AsyncRelayCommand(_ => UploadFolderAsync(), _ => CanUpload);
        RenameCommand = new AsyncRelayCommand(
            parameter => RenameAsync(parameter as DriveFileItem), _ => CanModify);
        DuplicateCommand = new AsyncRelayCommand(
            parameter => DuplicateAsync(parameter as DriveFileItem), _ => CanModify);
        DeleteCommand = new AsyncRelayCommand(
            parameter => DeleteAsync(parameter as DriveFileItem), _ => CanModify);
        // 移动到 keeps its menu entry and keeps saying so. Moving needs a destination
        // picker that browses the NAS, which is a separate piece of work; continuing to
        // explain that is better than silently removing an entry the user has seen.
        UnsupportedEditCommand = new RelayCommand<string>(ShowNotYetAvailable, _ => CanModify);

        // So a finished upload shows up without the user navigating away and back.
        _transfers.UploadCompleted += HandleUploadCompleted;
    }

    private DispatcherTimer? _refreshTimer;

    /// <summary>
    /// Reloads the listing when an upload finishes in the folder currently on screen.
    /// </summary>
    /// <remarks>
    /// Coalesced through a short timer: a folder upload completes hundreds of files in
    /// quick succession, and re-reading the directory once per file would spend the whole
    /// transfer re-enumerating. Uploads into any other folder are ignored — the user is
    /// not looking at them, and each refresh is a round trip to the NAS.
    /// </remarks>
    private void HandleUploadCompleted(string directory)
    {
        if (!string.Equals(
                directory.TrimEnd(Path.DirectorySeparatorChar),
                _currentDirectory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _refreshTimer ??= CreateRefreshTimer();
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private DispatcherTimer CreateRefreshTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = LoadCurrentDirectoryAsync(preserveSelection: true);
        };
        return timer;
    }

    /// <summary>
    /// Detaches from the transfer queue.
    /// </summary>
    /// <remarks>
    /// Without this the browser keeps being told about uploads after the workspace is torn
    /// down, and reloads a directory nothing is showing any more.
    /// </remarks>
    public void Dispose()
    {
        _transfers.UploadCompleted -= HandleUploadCompleted;
        _refreshTimer?.Stop();
        _refreshTimer = null;
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
    public int SelectedCount => _selection.Count;
    public string SelectedLabel => SelectedCount == 1 ? "已选择 1 项" : $"已选择 {SelectedCount} 项";
    public bool HasSelection => SelectedCount > 0;
    public bool IsReadOnly => !_scope.CanWrite;

    private string _truncationNotice = string.Empty;

    /// <summary>
    /// Set when the view is showing only part of what it found.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ErrorMessage"/> on purpose: nothing failed, but the list is
    /// not the whole answer, and a user who cannot tell the difference will conclude that a
    /// picture is missing from the archive rather than off the end of the list.
    /// </remarks>
    public string TruncationNotice
    {
        get => _truncationNotice;
        private set => SetProperty(ref _truncationNotice, value);
    }
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshWriteState();
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
    /// <summary>
    /// Whether this space accepts writes at all: connected, not the read-only other
    /// department, and not mid-reload.
    /// </summary>
    /// <remarks>
    /// Both write affordances come from this one condition. They used to disagree —
    /// <see cref="CanModify"/> was the constant true, so the edit menu appeared while the
    /// NAS was offline and inside the read-only space, and choosing an entry explained that
    /// it was unimplemented rather than that it was not allowed here.
    /// </remarks>
    private bool CanWrite => _smbConnected && !IsReadOnly && !IsLoading;

    public bool CanUpload => CanWrite;

    /// <summary>
    /// Whether rename, duplicate and delete are offered here. The shared view uses this to
    /// omit those menu entries entirely, for the read-only space and for the tour.
    /// </summary>
    public bool CanModify => CanWrite;

    public string LoadingMessage => "正在读取 NAS 文件…";
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
    public RelayCommand<DriveFileItem> DetailsCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand NavigateUpCommand { get; }

    // Async for the four write operations: a folder copy or a recursive delete can take
    // minutes, and these disable themselves while running, which is the only progress
    // indication a directory copy can honestly give.
    public AsyncRelayCommand UploadFolderCommand { get; }
    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand DuplicateCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand<string> UnsupportedEditCommand { get; }

    // Explicit interface implementations: the concrete command types stay available for
    // RaiseCanExecuteChanged, while the shared view sees only ICommand.
    ICommand IFileBrowser.OpenCommand => OpenCommand;
    ICommand IFileBrowser.DownloadCommand => DownloadCommand;
    ICommand IFileBrowser.NavigateUpCommand => NavigateUpCommand;
    ICommand IFileBrowser.ToggleGridCommand => ToggleGridCommand;
    ICommand IFileBrowser.ToggleListCommand => ToggleListCommand;
    ICommand IFileBrowser.UploadCommand => UploadCommand;
    ICommand IFileBrowser.UploadFolderCommand => UploadFolderCommand;
    ICommand IFileBrowser.RenameCommand => RenameCommand;
    ICommand IFileBrowser.DuplicateCommand => DuplicateCommand;
    ICommand IFileBrowser.DeleteCommand => DeleteCommand;
    ICommand IFileBrowser.UnsupportedEditCommand => UnsupportedEditCommand;
    ICommand IFileBrowser.DetailsCommand => DetailsCommand;

    public async Task InitializeAsync() => await LoadCurrentDirectoryAsync();

    /// <summary>
    /// Points the browser at one of the global navigation entries.
    /// </summary>
    /// <remarks>
    /// These entries are views rather than pages: each is this same browser pointed at a
    /// different scope, which is why they are handled here instead of as separate pages.
    /// Returns false for a key that is not one of them, so the caller can fall through to a
    /// real page rather than showing an empty browser.
    /// </remarks>
    public bool ActivateNavigation(string key)
    {
        switch (key)
        {
            case "图片":
                SetScope(_spaces.ResolveProjectsScope(SpaceMediaKind.Images), ProjectNavigation.ImageGroupName);
                return true;
            case "视频":
                SetScope(_spaces.ResolveProjectsScope(SpaceMediaKind.Videos), ProjectNavigation.VideoGroupName);
                return true;
            case "个人空间":
                SetScope(_spaces.ResolveScope(DriveSpaceKind.Personal), "个人空间");
                return true;
            default:
                return false;
        }
    }

    /// <summary>Opens one project's directory, where writing is allowed.</summary>
    public void OpenProject(string projectName) =>
        SetScope(_spaces.ResolveProjectScope(projectName), projectName);

    /// <summary>Opens one project, showing only one kind of its media.</summary>
    public void OpenProjectMedia(string projectName, SpaceMediaKind media) =>
        SetScope(
            _spaces.ResolveProjectScope(projectName, media),
            media == SpaceMediaKind.Images ? ProjectNavigation.ImageGroupName : ProjectNavigation.VideoGroupName);

    /// <summary>
    /// Points the browser at a scope: a directory, what may be done in it, and which of its
    /// files to list.
    /// </summary>
    public void SetScope(SpaceScope scope, string title)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope = scope;
        SpaceTitle = title;
        _currentRoot = _scope.Root;
        // Belongs to the listing that produced it, not to the browser: carrying it into the
        // next scope would claim a limit that is no longer being applied.
        TruncationNotice = string.Empty;
        _currentDirectory = _currentRoot;
        OnPropertyChanged(nameof(CurrentPathLabel));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanNavigateUp));
        RefreshWriteState();
        NavigateUpCommand.RaiseCanExecuteChanged();
        _ = LoadCurrentDirectoryAsync();
    }

    /// <summary>
    /// Records what the list has selected.
    /// </summary>
    /// <remarks>
    /// The items, not a count: rename, duplicate and delete act on the selection, and the
    /// count is only ever a display of it.
    /// </remarks>
    public void UpdateSelection(IReadOnlyList<DriveFileItem> items)
    {
        _selection = items ?? [];
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(HasSelection));
        DeleteCommand.RaiseCanExecuteChanged();
        DuplicateCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Re-announces everything that depends on being connected, writable and idle.
    /// </summary>
    /// <remarks>
    /// Both the visibility of the edit entries and their enabled state hang off one
    /// condition, so they are refreshed together. Announcing them from separate places is
    /// how the menu ends up still offering rename after a switch to the read-only space.
    /// </remarks>
    private void RefreshWriteState()
    {
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanModify));
        UploadCommand.RaiseCanExecuteChanged();
        UploadFolderCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        DuplicateCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        UnsupportedEditCommand.RaiseCanExecuteChanged();
    }

    private void SetViewMode(bool showGrid)
    {
        if (IsGridView == showGrid)
        {
            return;
        }

        SelectionRequested?.Invoke(SelectionRequest.Clear);
        UpdateSelection([]);
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

    private void PickUpload()
    {
        var paths = _filePicker.PickFiles();
        if (paths.Count == 0) return;
        _ = UploadAsync(paths.Select(path => (path, Path.GetFileName(path))).ToArray());
    }

    private async Task UploadFolderAsync()
    {
        var folder = _filePicker.PickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;

        LocalUploadPlan plan;
        try
        {
            plan = LocalUploadWalker.Enumerate(folder);
        }
        catch (Exception exception) when (IsMutationFailure(exception))
        {
            MessageBox.Show(exception.Message, "无法读取文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (plan.Files.Count == 0 && plan.Directories.Count <= 1)
        {
            MessageBox.Show("所选文件夹是空的。", "无法上传", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // Created up front rather than left to each transfer's implicit parent creation:
            // that would never produce an empty subfolder, and a permission problem would
            // arrive as one failed transfer per file instead of one clear message.
            await _mutations.EnsureDirectoriesAsync(
                _scope, plan.Directories.Select(relative => Path.Combine(_currentDirectory, relative)).ToArray());
        }
        catch (Exception exception) when (IsMutationFailure(exception))
        {
            MessageBox.Show(exception.Message, "无法创建文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await UploadAsync(plan.Files.Select(entry => (entry.LocalPath, entry.RelativePath)).ToArray());
        // So the new folders are visible while the transfers are still running.
        await LoadCurrentDirectoryAsync();
    }

    /// <summary>
    /// Queues uploads, giving each one a free name at its destination.
    /// </summary>
    /// <remarks>
    /// The free name is resolved here, before the transfer is queued, because the engine
    /// refuses a destination that already exists. The names claimed by earlier entries in
    /// this same batch are tracked: without that, two files called <c>report.pdf</c> in one
    /// selection would both resolve against a directory where neither exists yet, both be
    /// given the same name, and the second would fail instead of becoming
    /// <c>report (2).pdf</c>. That is what lets a folder upload run to the end without
    /// stopping to ask anything.
    /// </remarks>
    private async Task UploadAsync(IReadOnlyList<(string LocalPath, string RelativePath)> entries)
    {
        if (!CanUpload || entries.Count == 0) return;

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();
        var queued = 0;

        foreach (var (localPath, relativePath) in entries)
        {
            var destinationDirectory = Path.GetDirectoryName(Path.Combine(_currentDirectory, relativePath));
            var name = Path.GetFileName(relativePath);
            if (string.IsNullOrEmpty(destinationDirectory) || string.IsNullOrEmpty(name)) continue;

            string freeName;
            try
            {
                freeName = _mutations.ResolveFreeName(_scope, destinationDirectory, name, claimed);
            }
            catch (InvalidOperationException exception)
            {
                // One unusable name must not abandon the other two hundred.
                rejected.Add($"{name}：{exception.Message}");
                continue;
            }

            claimed.Add(freeName);
            await _transfers.AddUploadAsync(localPath, Path.Combine(destinationDirectory, freeName));
            queued++;
        }

        if (rejected.Count > 0)
        {
            var shown = string.Join("\n", rejected.Take(10));
            var more = rejected.Count > 10 ? $"\n…还有 {rejected.Count - 10} 项" : string.Empty;
            MessageBox.Show($"以下 {rejected.Count} 项未能加入上传：\n\n{shown}{more}",
                "部分文件未上传", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (queued > 1)
        {
            MessageBox.Show($"已将 {queued} 项加入传输列表。文件名重复时会自动加序号，不会覆盖。",
                "开始上传", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task RenameAsync(DriveFileItem? item)
    {
        if (item is null || !CanModify) return;

        // Pre-select the name without its extension: renaming almost always keeps the
        // extension, and this makes that a single typed word rather than a careful edit.
        var extension = item.IsDirectory ? string.Empty : Path.GetExtension(item.Name);
        var dialog = new TextInputWindow(
            "重命名", $"为“{item.Name}”输入新名称。", item.Name, "重命名", item.Name.Length - extension.Length);
        ShowOwned(dialog);
        if (dialog.ShowDialog() != true) return;

        var newName = dialog.Value.Trim();
        if (string.Equals(newName, item.Name, StringComparison.Ordinal)) return;

        await RunMutationAsync($"无法重命名“{item.Name}”",
            () => _mutations.RenameAsync(_scope, item.FullPath, newName));
    }

    private async Task DuplicateAsync(DriveFileItem? clicked)
    {
        var targets = TargetsFor(clicked);
        if (targets.Count == 0 || !CanModify) return;

        var failures = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                await _mutations.DuplicateInPlaceAsync(_scope, target.FullPath);
            }
            catch (Exception exception) when (IsMutationFailure(exception))
            {
                failures.Add($"{target.Name}：{exception.Message}");
            }
        }

        await FinishMutationAsync("部分项目未能复制", failures);
    }

    private async Task DeleteAsync(DriveFileItem? clicked)
    {
        var targets = TargetsFor(clicked);
        if (targets.Count == 0 || !CanModify) return;

        // Folders are counted for real before the question is asked. A dialog that promises
        // "3 items" and then removes 900 is worse than giving no number at all.
        var listing = new List<string>();
        foreach (var target in targets)
        {
            if (!target.IsDirectory)
            {
                listing.Add(target.Name);
                continue;
            }

            try
            {
                var contained = await _mutations.CountContainedAsync(_scope, target.FullPath);
                listing.Add($"{target.Name}（文件夹，内含 {contained} 项）");
            }
            catch (Exception exception) when (IsMutationFailure(exception))
            {
                listing.Add($"{target.Name}（文件夹）");
            }
        }

        var dialog = new ConfirmDialog(
            targets.Count == 1 ? "确认删除" : $"确认删除这 {targets.Count} 项",
            "删除后无法撤销。",
            listing,
            "删除");
        ShowOwned(dialog);
        if (dialog.ShowDialog() != true) return;

        var failures = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                await _mutations.DeleteAsync(_scope, target.FullPath);
            }
            catch (Exception exception) when (IsMutationFailure(exception))
            {
                failures.Add($"{target.Name}：{exception.Message}");
            }
        }

        await FinishMutationAsync("部分项目未能删除", failures);
    }

    /// <summary>
    /// Runs one operation, reports its failure, and reloads so the list matches the share.
    /// </summary>
    /// <remarks>
    /// The reload happens whether or not the operation succeeded: a recursive delete that
    /// failed halfway has still changed the folder, and leaving a stale listing on screen
    /// would invite the user to act on rows that no longer exist.
    /// </remarks>
    private async Task RunMutationAsync(string failureTitle, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception) when (IsMutationFailure(exception))
        {
            MessageBox.Show(exception.Message, failureTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            await LoadCurrentDirectoryAsync();
        }
    }

    private async Task FinishMutationAsync(string failureTitle, IReadOnlyList<string> failures)
    {
        if (failures.Count > 0)
        {
            var shown = string.Join("\n", failures.Take(10));
            var more = failures.Count > 10 ? $"\n…还有 {failures.Count - 10} 项" : string.Empty;
            MessageBox.Show($"{shown}{more}", failureTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await LoadCurrentDirectoryAsync();
    }

    /// <summary>
    /// What an action applies to: the whole selection when the clicked row is part of it,
    /// otherwise just the clicked row.
    /// </summary>
    /// <remarks>
    /// Right-clicking a row that is not selected selects it first (see the view's
    /// right-button handler), so in practice the two agree; this covers the case where a
    /// selection is made and a different row is acted on without the view having caught up.
    /// </remarks>
    private IReadOnlyList<DriveFileItem> TargetsFor(DriveFileItem? clicked)
    {
        if (clicked is not null) return _selection.Contains(clicked) ? _selection : [clicked];
        return _selection;
    }

    private static bool IsMutationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or System.Security.SecurityException
            or System.ComponentModel.Win32Exception;

    /// <summary>Owns a dialog to the main window so it centres there rather than on the screen.</summary>
    private static void ShowOwned(Window dialog)
    {
        if (Application.Current?.MainWindow is { IsLoaded: true } owner) dialog.Owner = owner;
    }

    /// <summary>
    /// Says so, for the one menu entry that is still a placeholder.
    /// </summary>
    /// <remarks>
    /// Rename, duplicate and delete are wired to the NAS now; 移动到 is not, because moving
    /// needs a destination picker that browses the NAS. The entry stays visible and states
    /// why rather than vanishing.
    /// </remarks>
    private static void ShowNotYetAvailable(string? operation)
    {
        MessageBox.Show($"“{operation}”尚未接入。重命名、复制和删除已经可以使用。",
            "功能尚未开放", MessageBoxButton.OK, MessageBoxImage.Information);
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

    /// <param name="preserveSelection">
    /// Keep whatever is selected. Set by the refresh that follows a finished upload, which
    /// can fire many times in a row while a folder transfers.
    /// </param>
    private async Task LoadCurrentDirectoryAsync(bool preserveSelection = false)
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
            // The global 图片 and 视频 entries list media from every project at once, with no
            // folders to walk through; every other scope is one directory's children.
            IReadOnlyList<DriveFileItem> items;
            if (_scope.Flatten)
            {
                var listing = await _catalog.EnumerateFlattenedAsync(_scope);
                items = listing.Items;
                TruncationNotice = listing.Truncated
                    ? $"内容较多，仅显示前 {SmbFileCatalog.FlattenedLimit} 项。"
                    : string.Empty;
            }
            else
            {
                items = await _catalog.EnumerateDirectoryAsync(_scope, _currentDirectory);
                TruncationNotice = string.Empty;
            }

            if (requestId != _loadRequestId)
                return;

            Files.Clear();
            foreach (var item in items)
                Files.Add(item);
            ItemsView.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
            // A reload driven by a finished upload keeps the selection: the user may well be
            // selecting something else while a long folder upload runs, and clearing it every
            // few hundred milliseconds would make the list unusable until the upload ended.
            if (!preserveSelection)
            {
                SelectionRequested?.Invoke(SelectionRequest.Clear);
                UpdateSelection([]);
            }
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

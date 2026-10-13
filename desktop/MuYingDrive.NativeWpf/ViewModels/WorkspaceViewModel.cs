using System.ComponentModel;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>
/// The signed-in shell: sidebar, header and the current page.
/// </summary>
/// <remarks>
/// The employee workspace and the login-free tour render this same shell. They differ
/// only in <see cref="WorkspaceChrome"/> — which pages and spaces exist, what the
/// account button says, and whether the update entry appears. The tour therefore looks
/// and behaves like a read-only signed-in user, and its sidebar carries no feedback or
/// transfer entry because those items are never in its list.
/// </remarks>
public sealed class WorkspaceViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, object> _pages;
    private readonly Func<string, Task> _selectSpaceAsync;
    private readonly Func<ValueTask>? _disposeExtra;
    private readonly TransferViewModel? _transfers;
    private readonly FeedbackViewModel? _feedback;
    private string? _selectedSpaceKey;
    private object _currentContent;
    private bool _isSidebarExpanded = true;

    private WorkspaceViewModel(
        MainViewModel main,
        WorkspaceChrome chrome,
        Dictionary<string, object> pages,
        Func<string, Task> selectSpaceAsync,
        Func<Task> exitAsync,
        TransferViewModel? transfers = null,
        FeedbackViewModel? feedback = null,
        Func<ValueTask>? disposeExtra = null)
    {
        _main = main;
        _pages = pages;
        _selectSpaceAsync = selectSpaceAsync;
        _transfers = transfers;
        _feedback = feedback;
        _disposeExtra = disposeExtra;

        NavigationItems = chrome.NavigationItems;
        SpaceItems = chrome.SpaceItems;
        ShowUpdateEntry = chrome.ShowUpdateEntry;
        AccountLabel = chrome.AccountLabel;
        ExitLabel = chrome.ExitLabel;
        _currentContent = pages["文件"];

        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarExpanded = !IsSidebarExpanded);
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string ?? "文件"));
        SelectSpaceCommand = new RelayCommand(parameter => _ = SelectSpaceAsync(parameter as string));
        ThemeChangedCommand = new RelayCommand(parameter =>
            _main.ThemeService.SetPreference(parameter as string ?? "跟随系统"));
        ExitCommand = new AsyncRelayCommand(_ => exitAsync());

        MarkSelected();
        if (chrome.SpaceItems.Count > 0) _selectedSpaceKey = chrome.SpaceItems[0].Key;
        MarkSelected();
    }

    /// <summary>The signed-in employee workspace: files, transfers and feedback.</summary>
    public static WorkspaceViewModel ForEmployee(
        MainViewModel main, DriveSession session, NasClientSettings settings)
    {
        var transfers = new TransferViewModel(session.User.Id);
        var files = new FilesViewModel(session, settings, transfers);
        var feedback = new FeedbackViewModel(session, settings);

        var chrome = new WorkspaceChrome(
            [
                new WorkspaceNavItem("文件", "文件", "Files"),
                new WorkspaceNavItem("传输", "传输", "Upload"),
                new WorkspaceNavItem("反馈", "反馈", "CircleHelp"),
            ],
            [
                new WorkspaceNavItem("本部门", "本部门", "FolderOpen"),
                new WorkspaceNavItem("公共素材", "公共素材", "Image"),
                new WorkspaceNavItem("个人空间", "个人空间", "UserRound"),
                new WorkspaceNavItem("其他部门", "其他部门", "Folder"),
            ],
            session.User.UserName,
            "退出登录",
            ShowUpdateEntry: true);

        var workspace = new WorkspaceViewModel(
            main, chrome,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["文件"] = files,
                ["传输"] = transfers,
                ["反馈"] = feedback
            },
            key => { files.SetSpace(key); return Task.CompletedTask; },
            () => main.SignOutAsync(session),
            transfers, feedback);

        workspace.Session = session;
        workspace.Account = session.User;
        workspace.Files = files;
        return workspace;
    }

    /// <summary>
    /// The login-free tour: the same shell as an employee, with read-only spaces.
    /// </summary>
    /// <remarks>
    /// The tour keeps the feedback page — it posts anonymously — but has no transfer
    /// page and no update entry, so those are simply absent from the sidebar.
    /// </remarks>
    public static WorkspaceViewModel ForVisitor(
        MainViewModel main,
        IFileBrowser browser,
        IReadOnlyList<WorkspaceNavItem> spaces,
        IReadOnlyDictionary<string, object> pages,
        Func<string, Task> selectSpaceAsync,
        Func<Task> exitAsync,
        FeedbackViewModel? feedback = null,
        Func<ValueTask>? disposeExtra = null)
    {
        var chrome = new WorkspaceChrome(
            [
                new WorkspaceNavItem("文件", "文件", "Files"),
                new WorkspaceNavItem("反馈", "反馈", "CircleHelp"),
            ],
            spaces,
            "参观",
            "退出游览",
            ShowUpdateEntry: false);

        var workspace = new WorkspaceViewModel(
            main, chrome,
            new Dictionary<string, object>(pages, StringComparer.Ordinal),
            selectSpaceAsync,
            exitAsync,
            feedback: feedback,
            disposeExtra: disposeExtra);
        workspace.Browser = browser;
        return workspace;
    }

    public DriveSession? Session { get; private set; }
    public V2UserSession? Account { get; private set; }
    public FilesViewModel? Files { get; private set; }
    public TransferViewModel? Transfers => _transfers;
    public FeedbackViewModel? Feedback => _feedback;
    public IFileBrowser? Browser { get; private set; }

    public IReadOnlyList<WorkspaceNavItem> NavigationItems { get; }
    public IReadOnlyList<WorkspaceNavItem> SpaceItems { get; }
    public bool ShowUpdateEntry { get; }
    public string AccountLabel { get; }
    public string ExitLabel { get; }
    public IReadOnlyList<string> ThemeChoices { get; } = ["跟随系统", "浅色", "深色"];
    public AsyncRelayCommand ExitCommand { get; }

    public string CurrentTheme
    {
        get => _main.ThemeService.Preference;
        set
        {
            if (_main.ThemeService.Preference != value)
            {
                _main.ThemeService.SetPreference(value);
                OnPropertyChanged();
            }
        }
    }

    public string SelectedNavigation { get; private set; } = "文件";
    public string PageTitle => SelectedNavigation;

    public object CurrentContent
    {
        get => _currentContent;
        private set => SetProperty(ref _currentContent, value);
    }

    public bool IsSidebarExpanded
    {
        get => _isSidebarExpanded;
        set => SetProperty(ref _isSidebarExpanded, value);
    }

    public AsyncRelayCommand InstallUpdateNowCommand => _main.InstallUpdateNowCommand;
    public bool IsUpdateReady => _main.IsUpdateReady;
    public string UpdateReadyVersion => _main.UpdateReadyVersion;
    public string UpdateButtonToolTip => IsUpdateReady
        ? $"点击立即更新到版本 {UpdateReadyVersion}"
        : "后台自动检查更新；有新版本时此处会显示红点";
    public string UpdateButtonName => IsUpdateReady ? $"有新版本 {UpdateReadyVersion} 可用，立即更新" : "沐影盘更新，后台自动检查";

    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand SelectSpaceCommand { get; }
    public RelayCommand ThemeChangedCommand { get; }

    public async Task InitializeAsync()
    {
        if (_transfers is not null) await _transfers.InitializeAsync().ConfigureAwait(true);
        if (Files is not null) await Files.InitializeAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        _feedback?.Dispose();
        _main.PropertyChanged -= HandleMainPropertyChanged;
        if (_disposeExtra is not null) await _disposeExtra().ConfigureAwait(true);
        if (_transfers is not null) await _transfers.DisposeAsync().ConfigureAwait(true);
    }

    public void RefreshUpdateState()
    {
        OnPropertyChanged(nameof(IsUpdateReady));
        OnPropertyChanged(nameof(UpdateReadyVersion));
        OnPropertyChanged(nameof(UpdateButtonToolTip));
        OnPropertyChanged(nameof(UpdateButtonName));
    }

    private void HandleMainPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.IsUpdateReady) or nameof(MainViewModel.UpdateReadyVersion))
        {
            RefreshUpdateState();
        }
    }

    private void Navigate(string destination)
    {
        if (!_pages.TryGetValue(destination, out var page)) return;
        SelectedNavigation = destination;
        CurrentContent = page;
        MarkSelected();
        OnPropertyChanged(nameof(PageTitle));
    }

    private async Task SelectSpaceAsync(string? space)
    {
        if (space is null || !SpaceItems.Any(item => item.Key == space)) return;
        _selectedSpaceKey = space;
        SelectedNavigation = "文件";
        CurrentContent = _pages["文件"];
        MarkSelected();
        OnPropertyChanged(nameof(PageTitle));
        await _selectSpaceAsync(space).ConfigureAwait(true);
    }

    private void MarkSelected()
    {
        foreach (var item in NavigationItems) item.IsSelected = item.Key == SelectedNavigation;
        foreach (var item in SpaceItems) item.IsSelected = item.Key == _selectedSpaceKey;
    }
}

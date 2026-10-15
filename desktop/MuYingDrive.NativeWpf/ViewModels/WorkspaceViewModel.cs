using System.Collections.ObjectModel;
using System.ComponentModel;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.NativeWpf.Views;

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
    /// <summary>
    /// Navigation entries in the workspace's space section. Selecting one has to activate its
    /// scope or gallery as well as swap the content, or the previous view would remain under
    /// the new title.
    /// </summary>
    private static readonly HashSet<string> SpaceKeys = new(StringComparer.Ordinal)
    {
        "图片", "视频", "个人空间"
    };

    private readonly MainViewModel _main;
    private readonly Dictionary<string, object> _pages;
    private readonly Func<string, Task> _selectSpaceAsync;
    private readonly Func<ValueTask>? _disposeExtra;
    private readonly TransferViewModel? _transfers;
    private readonly FeedbackViewModel? _feedback;
    private readonly Action<string>? _onPageActivated;
    private string? _selectedSpaceKey;
    private object _currentContent;
    private bool _isSidebarExpanded = true;
    private ProjectWorkbookPage? _projectWorkbooksPage;

    private WorkspaceViewModel(
        MainViewModel main,
        WorkspaceChrome chrome,
        Dictionary<string, object> pages,
        Func<string, Task> selectSpaceAsync,
        Func<Task> exitAsync,
        TransferViewModel? transfers = null,
        FeedbackViewModel? feedback = null,
        Func<ValueTask>? disposeExtra = null,
        Action<string>? onPageActivated = null)
    {
        _main = main;
        _pages = pages;
        _selectSpaceAsync = selectSpaceAsync;
        _onPageActivated = onPageActivated;
        _transfers = transfers;
        _feedback = feedback;
        _disposeExtra = disposeExtra;

        NavigationItems = chrome.NavigationItems;
        SpaceItems = chrome.SpaceItems;
        Projects = new ObservableCollection<ProjectNode>(chrome.Projects);
        ShowUpdateEntry = chrome.ShowUpdateEntry;
        AccountLabel = chrome.AccountLabel;
        ExitLabel = chrome.ExitLabel;

        // The first entry in the sidebar is where the workspace opens. Read from the chrome
        // rather than hard-coded: the employee and the tour offer different first entries,
        // and a hard-coded one would be a page the tour does not have.
        var first = chrome.NavigationItems.FirstOrDefault();
        _selectedSpaceKey = first?.Key;
        _currentContent = ResolveOpeningContent(chrome, pages);

        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarExpanded = !IsSidebarExpanded);
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string ?? string.Empty));
        SelectSpaceCommand = new RelayCommand(parameter => _ = SelectSpaceAsync(parameter as string));
        ActivateProjectNodeCommand = new RelayCommand(
            parameter => { if (parameter is ProjectNode node) ActivateProjectNode(node); });
        ThemeChangedCommand = new RelayCommand(parameter =>
            _main.ThemeService.SetPreference(parameter as string ?? "跟随系统"));
        ExitCommand = new AsyncRelayCommand(_ => exitAsync());

        MarkSelected();
        // The browser entries have to actually be activated, or the workspace opens showing
        // a browser that has never been pointed anywhere.
        if (first is not null && SpaceKeys.Contains(first.Key)) _ = _selectSpaceAsync(first.Key);
    }

    /// <summary>Activating a row in the 项目 tree.</summary>
    public RelayCommand ActivateProjectNodeCommand { get; }

    /// <summary>
    /// Creating a project, or null when nothing can serve it yet.
    /// </summary>
    /// <remarks>
    /// Null rather than a command that reports "not yet": the sidebar disables the button and
    /// shows <see cref="CreateProjectToolTip"/> as its reason, which is the same choice the
    /// 项目 entry itself made when there was no page behind it — the entry is not offered as
    /// working until it is.
    /// </remarks>
    public System.Windows.Input.ICommand? CreateProjectCommand { get; set; }

    /// <summary>Why the + is disabled, or what it will do.</summary>
    public string CreateProjectToolTip { get; set; } = "项目创建服务尚未接入。";

    /// <summary>
    /// What activating a project row does. Assigned by the factory after construction,
    /// because the handler needs the workspace itself — opening a row sets the content, and
    /// that is the workspace's to set.
    /// </summary>
    public Action<ProjectNode>? ProjectNodeActivator { get; set; }

    private void ActivateProjectNode(ProjectNode node)
    {
        SelectProjectNode(node);
        ProjectNodeActivator?.Invoke(node);
    }

    private ProjectNode? _selectedProjectNode;
    public ProjectNode? SelectedProjectNode => _selectedProjectNode;

    /// <summary>
    /// Highlights the active project node in the sidebar tree.
    /// </summary>
    public void SelectProjectNode(ProjectNode? target)
    {
        _selectedProjectNode = target;
        void UpdateNode(ProjectNode n)
        {
            n.IsSelected = target != null && (ReferenceEquals(n, target) || n.Id == target.Id);
            foreach (var child in n.Children) UpdateNode(child);
        }
        foreach (var project in Projects) UpdateNode(project);
        OnPropertyChanged(nameof(PageTitle));
    }

    /// <summary>
    /// Opens an .xlsx the user opened from the file browser in the workbook reader, returning
    /// false when this does not recognise it and the default application should have it.
    /// </summary>
    public bool TryOpenWorkbookFile(string fullPath)
    {
        var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootIndex = Array.FindLastIndex(segments, segment =>
            segment.Equals(SmbSpaceResolver.ProjectFolderName, StringComparison.OrdinalIgnoreCase));
        if (rootIndex < 0 || rootIndex + 2 != segments.Length - 1) return false;

        var projectName = segments[rootIndex + 1];
        var fileName = segments[^1];
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return false;

        var projectNode = Projects.FirstOrDefault(project =>
            project.Kind == ProjectNodeKind.Project &&
            string.Equals(project.ProjectName, projectName, StringComparison.OrdinalIgnoreCase));
        if (projectNode is null) return false;

        ShowPage(WorkbookPageKey);
        if (_projectWorkbooksPage is not null)
        {
            var node = FindWorkbookNode(fullPath);
            if (node is not null) SelectProjectNode(node);
            _ = _projectWorkbooksPage.OpenWorkbookByNameAsync(projectNode.ProjectId, fileName);
            _ = LoadProjectsAsync();
        }
        else
        {
            var node = FindWorkbookNode(fullPath);
            if (node is not null) ProjectNodeActivator?.Invoke(node);
        }
        return true;
    }

    /// <summary>
    /// The 工作簿 row for a file directly under a project folder, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The path is matched against the tree rather than turned into a file id here. That id
    /// is the server's own encoding of the project id and the file name, and the tree already
    /// holds one per workbook because the server returned it — recomputing it would be a
    /// second implementation of the same rule, and the two would disagree the day it changed.
    ///
    /// Deliberately narrow: the file has to sit at exactly
    /// <c>&lt;share&gt;\Project\&lt;project&gt;\&lt;name&gt;</c> and that project has to have
    /// listed a workbook of that name. A file in a subfolder, or in a project that is not
    /// loaded, is not something this can open — and declining is the honest answer, because
    /// the alternative is handing it to Excel and calling that success.
    /// </remarks>
    private ProjectNode? FindWorkbookNode(string fullPath)
    {
        var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootIndex = Array.FindLastIndex(segments, segment =>
            segment.Equals(SmbSpaceResolver.ProjectFolderName, StringComparison.OrdinalIgnoreCase));
        if (rootIndex < 0 || rootIndex + 2 != segments.Length - 1) return null;

        var projectName = segments[rootIndex + 1];
        var fileName = segments[^1];
        return Projects
            .Where(project => project.Kind == ProjectNodeKind.Project
                              && string.Equals(project.ProjectName, projectName, StringComparison.Ordinal))
            .SelectMany(project => project.Children)
            .Where(group => group.Kind == ProjectNodeKind.WorkbookGroup)
            .SelectMany(group => group.Children)
            .FirstOrDefault(child => child.Kind == ProjectNodeKind.Workbook
                                     && string.Equals(child.Name, fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The page table key for the workbook reader, which has no sidebar entry.</summary>
    public const string WorkbookPageKey = "工作簿";

    /// <summary>
    /// What the workspace shows before anything has been clicked: the first sidebar entry
    /// that has a page behind it.
    /// </summary>
    /// <remarks>
    /// Throws rather than falling back to an empty content area. A chrome whose entries all
    /// point at pages that were never built is a wiring mistake, and a blank workspace is
    /// the one symptom that would not say so.
    /// </remarks>
    private static object ResolveOpeningContent(WorkspaceChrome chrome, Dictionary<string, object> pages)
    {
        foreach (var item in chrome.NavigationItems)
            if (pages.TryGetValue(item.Key, out var page)) return page;
        if (pages.Count > 0) return pages.Values.First();
        throw new InvalidOperationException("工作区没有任何可显示的页面。");
    }

    /// <summary>
    /// Shows the file browser, wherever it is currently pointed.
    /// </summary>
    /// <remarks>
    /// Used when a project row opens a directory. No global entry is highlighted afterwards:
    /// the user is inside a project, not in one of the views at the top of the sidebar, and
    /// leaving the previous entry lit would say otherwise.
    /// </remarks>
    public void ShowBrowser(object browser)
    {
        SelectedNavigation = string.Empty;
        CurrentContent = browser;
        MarkSelected();
        OnPropertyChanged(nameof(PageTitle));
        _onPageActivated?.Invoke("browser");
    }

    /// <summary>Shows a page that exists without a sidebar entry of its own.</summary>
    /// <remarks>
    /// The workbook page is reached from a project's 工作簿 row rather than from a
    /// navigation entry, so it lives in the page table but not in the sidebar list.
    /// </remarks>
    public void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        SelectedNavigation = string.Empty;
        CurrentContent = page;
        MarkSelected();
        OnPropertyChanged(nameof(PageTitle));
        _onPageActivated?.Invoke(key);
    }

    /// <summary>The signed-in employee workspace: files, transfers and feedback.</summary>
    public static WorkspaceViewModel ForEmployee(
        MainViewModel main, DriveSession session, NasClientSettings settings)
    {
        var transfers = new TransferViewModel(session.User.Id);
        var feedback = new FeedbackViewModel(session, settings);

        // Built per sign-in and disposed with the workspace: the workbook library scopes its
        // retry records to a hash of the server origin and the account id, so a page that
        // outlived a sign-out could resume one person's pending work as the next. Absent when
        // the client configuration did not load, since there is then no pinned client to build
        // it from — and the entry is simply not offered rather than offered and broken.
        var projectWorkbooks = main.ApiClient is { } api
            ? new ProjectWorkbookPage(api, session.SessionToken, session.User.Id)
            : null;
        if (projectWorkbooks is not null)
        {
            projectWorkbooks.ViewModel.ShareRoot = settings.ShareRoot;
        }
        var files = new FilesViewModel(session, settings, transfers);

        var chrome = new WorkspaceChrome(
            [
                new WorkspaceNavItem("图片", "图片", "Image"),
                new WorkspaceNavItem("视频", "视频", "Video"),
                new WorkspaceNavItem("个人空间", "个人空间", "UserRound"),
                new WorkspaceNavItem("传输", "传输", "Upload"),
                new WorkspaceNavItem("反馈", "反馈", "CircleHelp"),
            ],
            [],
            // Filled once the project list has been read. Empty until then, and empty is
            // shown as "loading or none" rather than as a project that is not there.
            [],
            session.User.UserName,
            "退出登录",
            ShowUpdateEntry: true);

        var pages = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["图片"] = files,
            ["视频"] = files,
            ["个人空间"] = files,
            ["传输"] = transfers,
            ["反馈"] = feedback
        };
        // No sidebar entry of its own: the workbook page is reached from a project's
        // 工作簿 row, so it is a page without being a navigation destination.
        if (projectWorkbooks is not null) pages[WorkbookPageKey] = projectWorkbooks.ViewModel;

        var workspace = new WorkspaceViewModel(
            main, chrome, pages,
            key =>
            {
                files.ActivateNavigation(key);
                return Task.CompletedTask;
            },
            () => main.SignOutAsync(session),
            transfers, feedback,
            disposeExtra: projectWorkbooks is null
                ? null
                : () =>
                {
                    projectWorkbooks.Dispose();
                    return ValueTask.CompletedTask;
                },
            onPageActivated: key =>
            {
                if (key == "反馈") _ = feedback.EnsureLoadedAsync();
            });

        workspace._projectWorkbooksPage = projectWorkbooks;
        if (projectWorkbooks is not null)
        {
            projectWorkbooks.ProjectFilesChanged = () =>
            {
                _ = workspace.LoadProjectsAsync();
            };
            projectWorkbooks.ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "IsOpen" or "Document")
                {
                    workspace.OnPropertyChanged(nameof(workspace.PageTitle));
                }
            };
        }

        workspace.ProjectNodeActivator = node =>
        {
            workspace.SelectProjectNode(node);
            switch (node.Kind)
            {
                case ProjectNodeKind.Workbook when projectWorkbooks is not null:
                    workspace.ShowPage(WorkbookPageKey);
                    _ = projectWorkbooks.OpenWorkbookAsync(node.ProjectId, node.Id, node.Name, async () =>
                    {
                        await workspace.LoadProjectsAsync();
                    });
                    break;
                case ProjectNodeKind.WorkbookGroup when projectWorkbooks is not null:
                    workspace.ShowPage(WorkbookPageKey);
                    _ = projectWorkbooks.OpenProjectHomeAsync(node.ProjectId);
                    _ = workspace.LoadProjectsAsync();
                    break;
                case ProjectNodeKind.MediaGroup:
                    files.OpenProjectMedia(
                        node.ProjectName,
                        node.Name == ProjectNavigation.ImageGroupName ? SpaceMediaKind.Images : SpaceMediaKind.Videos);
                    workspace.ShowBrowser(files);
                    break;
                default:
                    files.OpenProject(node.ProjectName);
                    workspace.ShowBrowser(files);
                    break;
            }
        };

        if (projectWorkbooks is not null && main.ApiClient is { } createApi)
        {
            workspace.ProjectListLoader = async () =>
            {
                var projects = await projectWorkbooks.ListProjectsAsync();
                var trees = new List<ProjectNode>(projects.Count);
                // One request per project for its workbooks. Fine at this size and honest
                // about it; if the list ever grows, the fix is a backend route that returns
                // projects with their workbooks, not a cache here.
                foreach (var project in projects)
                {
                    var files = await projectWorkbooks.ListProjectFilesAsync(project.Id);
                    trees.Add(ProjectNavigation.ForProject(
                        project.Id,
                        project.Name,
                        files.Select(file => new ProjectNavigation.WorkbookEntry(file.Id, file.Name)).ToArray()));
                }
                return trees;
            };
            // Not awaited: the sidebar fills in when it fills in, and the workspace is usable
            // — 图片, 个人空间, 传输, 反馈 — while it does.
            _ = workspace.LoadProjectsAsync();

            workspace.CreateProjectToolTip = "新建项目";
            workspace.CreateProjectCommand = new AsyncRelayCommand(async _ =>
            {
                var dialog = new TextInputWindow(
                    "新建项目", "为项目取一个名字。创建后会生成节拍表和资产表。", string.Empty, "创建", 0)
                {
                    Owner = System.Windows.Application.Current?.MainWindow
                };
                if (dialog.ShowDialog() != true) return;
                var name = dialog.Value.Trim();
                if (name.Length == 0) return;

                try
                {
                    await createApi.CreateProjectSpaceAsync(session.SessionToken, name);
                }
                catch (Exception error)
                {
                    // The server knows which rule was broken — a taken name, an unusable one —
                    // and its sentence is the one to show. A silent failure here would look
                    // like the + doing nothing at all.
                    System.Windows.MessageBox.Show(
                        error.Message, "无法创建项目",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                await workspace.LoadProjectsAsync();
            });
        }

        // Opening a workbook from the file browser goes to the reader rather than to Excel.
        files.WorkbookOpener = workspace.TryOpenWorkbookFile;

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
            // The tour has no projects. It is a read-only walk through a few shared spaces,
            // not a workspace someone works in.
            [],
            "参观",
            "退出游览",
            ShowUpdateEntry: false);

        var workspace = new WorkspaceViewModel(
            main, chrome,
            new Dictionary<string, object>(pages, StringComparer.Ordinal),
            selectSpaceAsync,
            exitAsync,
            feedback: feedback,
            disposeExtra: disposeExtra,
            onPageActivated: key => { if (key == "反馈") _ = feedback?.EnsureLoadedAsync(); });
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

    /// <summary>The 项目 group: one tree per project. Empty until the list has been read.</summary>
    public ObservableCollection<ProjectNode> Projects { get; }

    /// <summary>
    /// Reads the projects for the sidebar. Assigned by the factory, because it needs the
    /// workbook page's service and the workspace should not know how one is built.
    /// </summary>
    public Func<Task<IReadOnlyList<ProjectNode>>>? ProjectListLoader { get; set; }

    /// <summary>Why the 项目 group is empty, or empty when it simply has not loaded yet.</summary>
    public string ProjectListNotice
    {
        get => _projectListNotice;
        private set => SetProperty(ref _projectListNotice, value);
    }

    private string _projectListNotice = string.Empty;

    /// <summary>
    /// Fills the 项目 group from the backend.
    /// </summary>
    /// <remarks>
    /// A failure leaves the group empty and says why, rather than leaving it looking like a
    /// workspace that has no projects — those two are the same pixels and different
    /// problems. It does not throw: a sidebar that cannot load is not a reason to take the
    /// file browser, the transfers and the feedback page down with it.
    /// </remarks>
    public async Task LoadProjectsAsync()
    {
        if (ProjectListLoader is null) return;
        try
        {
            var projects = await ProjectListLoader();
            Projects.Clear();
            foreach (var project in projects) Projects.Add(project);
            ProjectListNotice = projects.Count == 0 ? "还没有项目。点击 + 新建。" : string.Empty;
        }
        catch (Exception)
        {
            ProjectListNotice = "项目列表暂时读不到，请稍后重试。";
        }
    }

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
    public string PageTitle
    {
        get
        {
            if (CurrentContent == _projectWorkbooksPage?.ViewModel || CurrentContent == _projectWorkbooksPage)
            {
                var projectName = _selectedProjectNode?.ProjectName;
                if (string.IsNullOrWhiteSpace(projectName))
                {
                    projectName = _projectWorkbooksPage?.ViewModel.SelectedProject?.Name;
                }
                var docTitle = _projectWorkbooksPage?.ViewModel.IsOpen == true
                    ? _projectWorkbooksPage.ViewModel.Document?.File.Name ?? "工作簿"
                    : "工作簿";

                if (!string.IsNullOrWhiteSpace(projectName))
                {
                    return $"{projectName} · {docTitle}";
                }
                return docTitle;
            }

            if (_selectedProjectNode != null)
            {
                var projectName = _selectedProjectNode.ProjectName;
                if (_selectedProjectNode.Kind == ProjectNodeKind.Project)
                {
                    return projectName;
                }
                return string.IsNullOrWhiteSpace(projectName)
                    ? _selectedProjectNode.Name
                    : $"{projectName} · {_selectedProjectNode.Name}";
            }

            return SelectedNavigation;
        }
    }

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
        // The browser subscribes to the transfer queue to refresh after an upload; it has
        // to let go before the queue it listens to is torn down.
        Files?.Dispose();
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
        var isSpace = SpaceKeys.Contains(destination);
        if (isSpace) _selectedSpaceKey = destination;
        SelectedNavigation = destination;
        CurrentContent = page;
        MarkSelected();
        OnPropertyChanged(nameof(PageTitle));
        // The image gallery and file-browser scopes have different activation work, so each
        // gets a chance to select its project scope before the page is shown.
        if (isSpace) _ = _selectSpaceAsync(destination);
        // Some pages fetch on first display; without this the feedback list opened empty
        // until a filter was clicked.
        _onPageActivated?.Invoke(destination);
    }

    private async Task SelectSpaceAsync(string? space)
    {
        if (space is null || !SpaceItems.Any(item => item.Key == space)) return;

        // 项目 sits in the space list for placement, but it is a page rather than a space:
        // choosing it navigates instead of re-filtering the file browser underneath.
        if (_pages.ContainsKey(space))
        {
            Navigate(space);
            return;
        }

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
        // A space item is normally highlighted by the space the browser is showing, but 项目
        // is a page that lives in this list — without the second test it would never light up
        // while its page was on screen.
        foreach (var item in SpaceItems)
            item.IsSelected = item.Key == _selectedSpaceKey || item.Key == SelectedNavigation;
        if (!string.IsNullOrEmpty(SelectedNavigation))
        {
            SelectProjectNode(null);
        }
    }
}

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
/// Employees and visitors use the same shell and navigation. Read-only capability checks
/// disable actions and pages the current session cannot use.
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

        // The first enabled entry in the common sidebar is where the workspace opens.
        var first = chrome.NavigationItems.FirstOrDefault(item => item.IsEnabled);
        _selectedSpaceKey = first?.Key;
        _currentContent = ResolveOpeningContent(chrome, pages);

        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarExpanded = !IsSidebarExpanded);
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string ?? string.Empty));
        SelectSpaceCommand = new RelayCommand(parameter => _ = SelectSpaceAsync(parameter as string));
        ActivateProjectNodeCommand = new RelayCommand(
            parameter =>
            {
                var node = parameter as ProjectNode ?? _selectedProjectNode;
                if (node is not null) ActivateProjectNode(node);
            });
        OpenProjectNodeCommand = new RelayCommand(
            parameter =>
            {
                var node = parameter as ProjectNode ?? _selectedProjectNode;
                if (node is not null) ActivateProjectNode(node);
            });
        RenameProjectNodeCommand = new AsyncRelayCommand(async parameter =>
        {
            if (IsReadOnlySession) return;
            var node = parameter as ProjectNode ?? _selectedProjectNode;
            if (node is null || node.Kind != ProjectNodeKind.Workbook) return;
            if (_projectWorkbooksPage is null) return;

            var dialog = new TextInputWindow("重命名工作簿", "请输入工作簿的新名称：", node.Name, "确定", node.Name.Length)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true) return;

            var newName = dialog.Value.Trim();
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, node.Name, StringComparison.Ordinal))
                return;
            try
            {
                var files = await _projectWorkbooksPage.ListProjectFilesAsync(node.ProjectId);
                var file = files.FirstOrDefault(f => f.Id == node.Id);
                var nameVersion = file?.NameVersion ?? 1;

                var updated = await _projectWorkbooksPage.ViewModel.Service.RenameWorkbookAsync(node.Id, nameVersion, newName);
                if (_projectWorkbooksPage.ViewModel.Document?.File.Id == node.Id)
                {
                    await _projectWorkbooksPage.OpenWorkbookAsync(node.ProjectId, node.Id, newName);
                }
                await LoadProjectsAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    ex.Message, "重命名失败",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        });
        DeleteWorkbookProjectNodeCommand = new AsyncRelayCommand(async parameter =>
        {
            if (IsReadOnlySession) return;
            var node = parameter as ProjectNode ?? _selectedProjectNode;
            if (node is null || node.Kind != ProjectNodeKind.Workbook) return;
            if (_projectWorkbooksPage is null) return;

            var confirm = new ConfirmDialog(
                "删除工作簿",
                "确定要删除该工作簿吗？\n工作簿会移入回收站，之后可以恢复。",
                [node.Name],
                "删除")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (confirm.ShowDialog() != true) return;

            try
            {
                await _projectWorkbooksPage.ViewModel.Service.DeleteWorkbookAsync(node.Id);
                if (_projectWorkbooksPage.ViewModel.Document?.File.Id == node.Id)
                {
                    _projectWorkbooksPage.ViewModel.Back();
                }
                await LoadProjectsAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    ex.Message, "删除工作簿失败",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        });
        DeleteProjectNodeCommand = new AsyncRelayCommand(async parameter =>
        {
            if (IsReadOnlySession) return;
            var node = parameter as ProjectNode ?? _selectedProjectNode;
            if (node is null || node.Kind != ProjectNodeKind.Project) return;
            if (Session is null || _main.ApiClient is null) return;

            var confirm = new ConfirmDialog(
                "删除项目",
                "确定要删除该项目吗？\n项目会移入回收站，之后可以恢复。项目与工作簿的关联会保留。",
                [node.Name],
                "删除")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (confirm.ShowDialog() != true) return;

            try
            {
                await _main.ApiClient.DeleteProjectSpaceAsync(Session.SessionToken, node.Id);
                if (_projectWorkbooksPage?.ViewModel.Document?.File.ProjectId == node.Id ||
                    _projectWorkbooksPage?.ViewModel.SelectedProject?.Id == node.Id)
                {
                    _projectWorkbooksPage.ViewModel.Back();
                }
                if (_selectedProjectNode?.ProjectId == node.Id || _selectedProjectNode?.Id == node.Id)
                {
                    SelectProjectNode(null);
                }
                await LoadProjectsAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    ex.Message, "删除项目失败",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        });
        OpenProjectRecycleBinCommand = new AsyncRelayCommand(
            _ => OpenProjectRecycleBinAsync(),
            _ => Session is not null && !IsReadOnlySession && _main.ApiClient is not null);
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
    public RelayCommand OpenProjectNodeCommand { get; }
    public AsyncRelayCommand RenameProjectNodeCommand { get; }
    public AsyncRelayCommand DeleteWorkbookProjectNodeCommand { get; }
    public AsyncRelayCommand DeleteProjectNodeCommand { get; }
    public AsyncRelayCommand OpenProjectRecycleBinCommand { get; }

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
            if (item.IsEnabled && pages.TryGetValue(item.Key, out var page)) return page;
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

    /// <summary>Compatibility entry point for employee-only callers.</summary>
    public static WorkspaceViewModel ForEmployee(
        MainViewModel main, DriveSession session, NasClientSettings settings) =>
        ForSession(main, session, settings);

    /// <summary>Builds the common workspace for any session; visitor sessions are read-only.</summary>
    public static WorkspaceViewModel ForSession(
        MainViewModel main, DriveSession session, NasClientSettings settings)
    {
        var api = main.ApiClient ?? throw new InvalidOperationException("文件服务不可用。");
        var clientId = session.IsVisitor
            ? Guid.NewGuid().ToString("D")
            : FileClientIdentity.GetOrCreate(settings.ApiBaseUri, session.User.Id);
        var agent = new RustTransferAgentClient(session.IsVisitor
            ? FileClientIdentity.GetVisitorAgentNamespace(settings.ApiBaseUri, session.SessionToken)
            : FileClientIdentity.GetEmployeeAgentNamespace(settings.ApiBaseUri, session.User.Id));
        var transfers = new TransferViewModel(api, session, settings, agent, clientId);
        FeedbackViewModel? feedback = session.IsReadOnly ? null : new FeedbackViewModel(session, settings);
        var imageTransfer = new AgentProjectImageFileTransfer(api, session, transfers, clientId);

        // The workbook page belongs to this signed-in session and is disposed with the
        // workspace. Its shared view model receives the session's read-only capability.
        var projectWorkbooks = new ProjectWorkbookPage(api, session.SessionToken, session.User.Id,
            imageTransfer, isReadOnly: session.IsReadOnly);
        var files = new FilesViewModel(api, session, transfers, agent, clientId);
        SyncViewModel? sync = session.IsReadOnly
            ? null
            : new SyncViewModel(projectWorkbooks, transfers, session.User.Id, settings.ApiBaseUri);

        var chrome = new WorkspaceChrome(
            [
                new WorkspaceNavItem("图片", "图片", "Image"),
                new WorkspaceNavItem("视频", "视频", "Video"),
                new WorkspaceNavItem("个人空间", "个人空间", "UserRound", isEnabled: !session.IsReadOnly,
                    disabledReason: "此只读账户没有个人空间访问权限。"),
                new WorkspaceNavItem("同步", "同步", "RefreshCw", isEnabled: !session.IsReadOnly,
                    disabledReason: "只读账户不能配置同步。"),
                new WorkspaceNavItem("传输", "传输", "Upload"),
                new WorkspaceNavItem("反馈", "反馈", "CircleHelp", isEnabled: !session.IsReadOnly,
                    disabledReason: "只读账户不能提交或管理反馈。"),
            ],
            [],
            // Filled once the project list has been read. Empty until then, and empty is
            // shown as "loading or none" rather than as a project that is not there.
            [],
            session.User.UserName,
            session.IsVisitor ? "退出只读访问" : "退出登录",
            ShowUpdateEntry: true);

        var pages = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["图片"] = files,
            ["视频"] = files,
            ["个人空间"] = files,
            ["传输"] = transfers
        };
        if (sync is not null) pages["同步"] = sync;
        if (feedback is not null) pages["反馈"] = feedback;
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
            disposeExtra: () =>
            {
                sync?.Dispose();
                projectWorkbooks?.Dispose();
                return ValueTask.CompletedTask;
            },
            onPageActivated: key =>
            {
                if (key == "反馈" && feedback is not null) _ = feedback.EnsureLoadedAsync();
                else if (key == "同步" && sync is not null) _ = sync.InitializeAsync();
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
                        node.ProjectId,
                        node.ProjectName,
                        node.Name == ProjectNavigation.ImageGroupName ? SpaceMediaKind.Images : SpaceMediaKind.Videos);
                    workspace.ShowBrowser(files);
                    break;
                default:
                    files.OpenProject(node.ProjectId, node.ProjectName);
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
            // Not awaited: the sidebar fills in when it fills in, while the common workspace
            // remains usable during the request.
            _ = workspace.LoadProjectsAsync();

            if (!session.IsReadOnly)
            {
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
            else
            {
                workspace.CreateProjectToolTip = "只读账户不能创建项目";
                workspace.CreateProjectCommand = null;
            }
        }

        workspace.Session = session;
        workspace.Account = session.User;
        workspace.Files = files;
        return workspace;
    }

    private DriveSession? _session;
    public DriveSession? Session
    {
        get => _session;
        private set
        {
            if (!SetProperty(ref _session, value)) return;
            OnPropertyChanged(nameof(IsReadOnlySession));
            OpenProjectRecycleBinCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsReadOnlySession => Session?.IsReadOnly == true;
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
            ProjectListNotice = projects.Count == 0
                ? IsReadOnlySession ? "没有可查看的项目。" : "还没有项目。点击 + 新建。"
                : string.Empty;
        }
        catch (Exception)
        {
            ProjectListNotice = "项目列表暂时读不到，请稍后重试。";
        }
    }

    private async Task OpenProjectRecycleBinAsync()
    {
        if (Session is null || IsReadOnlySession || _main.ApiClient is not { } api) return;
        try
        {
            var projects = await api.ListProjectSpacesAsync(Session.SessionToken, includeDeleted: true);
            var items = new List<RecycledProjectItem>();
            foreach (var project in projects)
            {
                if (project.DeletedAt is { } deletedAt)
                    items.Add(new RecycledProjectItem(RecycledProjectItemKind.Project, project.Id,
                        project.Name, project.Id, project.Name, deletedAt));

                var workbooks = await api.ListProjectWorkbooksAsync(
                    Session.SessionToken, project.Id, includeDeleted: true);
                foreach (var workbook in workbooks)
                {
                    if (workbook.DeletedAt is not { } workbookDeletedAt) continue;
                    items.Add(new RecycledProjectItem(RecycledProjectItemKind.Workbook, workbook.Id,
                        workbook.Name, project.Id, project.Name, workbookDeletedAt));
                }
            }

            var dialog = new ProjectRecycleBinWindow(items)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true || dialog.SelectedItem is not { } selected) return;
            if (selected.Kind == RecycledProjectItemKind.Project)
                await api.RestoreProjectSpaceAsync(Session.SessionToken, selected.Id);
            else
                await api.RestoreProjectWorkbookAsync(Session.SessionToken, selected.Id);

            await LoadProjectsAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message, "项目回收站操作失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
        if (!NavigationItems.Any(item => item.Key == destination && item.IsEnabled)) return;
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
        if (space is null || !SpaceItems.Any(item => item.Key == space && item.IsEnabled)) return;

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

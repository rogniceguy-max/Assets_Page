using System.Collections.ObjectModel;
using System.IO;

namespace MuYingDrive.ProjectWorkbooks;

/// <summary>
/// Navigation pages owned by the project-space shell. Workbook rendering and the legacy
/// Personal/Transfer/Feedback pages are supplied by the host through HostContent.
/// </summary>
public enum ProjectSpaceShellPage
{
    ProjectOverview,
    Images,
    Videos,
    ProjectImages,
    ProjectVideos,
    ImageFolder,
    RecycleBin,
    PersonalSpace,
    Transfers,
    Feedback,
    Workbook
}

public enum ProjectSpaceNavigationKind
{
    Project,
    WorkbookGroup,
    Workbook,
    ImageLibrary,
    VideoLibrary,
    ImageFolder
}

public enum ProjectSpaceMediaKind
{
    Image,
    Video
}

public sealed record ProjectSpaceWorkbookRecord(string Id, string Name, string Kind);

/// <summary>
/// A persisted folder associated with an asset row. The host supplies these records from
/// storage; the shell does not infer or create folder records on its own.
/// </summary>
public sealed record ProjectSpaceAssetFolderRecord(
    string Id,
    string ProjectId,
    string Name,
    string? ParentFolderId,
    string? AssetRowId)
{
    public string? Path { get; init; }
}

/// <summary>Backend-confirmed project state used to populate the navigation tree.</summary>
public sealed record ProjectSpaceSnapshot(
    string Id,
    string Name,
    IReadOnlyList<ProjectSpaceWorkbookRecord> Workbooks,
    IReadOnlyList<ProjectSpaceAssetFolderRecord> ImageFolders);

public sealed record ProjectSpaceMediaRecord(
    string Id,
    string ProjectId,
    string Name,
    ProjectSpaceMediaKind Kind,
    string? FolderId,
    System.Windows.Media.ImageSource? Preview = null,
    string? Details = null)
{
    public bool IsVideo => Kind == ProjectSpaceMediaKind.Video;
}

public sealed record ProjectSpaceMediaGroupRecord(
    string ProjectId,
    string ProjectName,
    IReadOnlyList<ProjectSpaceMediaRecord> Items);

public sealed class ProjectSpaceMediaGroup(string projectId, string projectName, IEnumerable<ProjectSpaceMediaRecord> items)
{
    public string ProjectId { get; } = projectId;
    public string ProjectName { get; } = projectName;
    public ObservableCollection<ProjectSpaceMediaRecord> Items { get; } = new(items);
}

public sealed record ProjectSpaceRecycleBinRecord(
    string Id,
    string ProjectId,
    string Name,
    string? OriginalFolderId,
    string OriginalFolderPath,
    DateTimeOffset? RemovedAt = null);

public sealed class ProjectSpacePrimaryNavigationItem(string key, string label, string glyph) : ObservableModel
{
    private bool _isActive;

    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;
    public bool IsActive
    {
        get => _isActive;
        internal set { _isActive = value; Changed(); }
    }
}

public sealed class ProjectSpaceNavigationNode(
    string id,
    string name,
    string projectId,
    ProjectSpaceNavigationKind kind,
    string? targetId = null,
    string glyph = "•")
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string ProjectId { get; } = projectId;
    public ProjectSpaceNavigationKind Kind { get; } = kind;
    public string? TargetId { get; } = targetId;
    public string Glyph { get; } = glyph;
    public bool IsExpanded { get; set; }
    public ObservableCollection<ProjectSpaceNavigationNode> Children { get; } = [];
}

public sealed class ProjectSpaceShellProject(ProjectSpaceSnapshot snapshot) : ObservableModel
{
    private string _name = snapshot.Name;

    public string Id { get; } = snapshot.Id;
    public string Name
    {
        get => _name;
        internal set { _name = value; Changed(); }
    }

    public ObservableCollection<ProjectSpaceWorkbookRecord> Workbooks { get; } = new(snapshot.Workbooks);
    public ObservableCollection<ProjectSpaceAssetFolderRecord> ImageFolders { get; } = new(snapshot.ImageFolders);

    internal ProjectSpaceNavigationNode BuildNavigationRoot()
    {
        var root = new ProjectSpaceNavigationNode(Id, Name, Id, ProjectSpaceNavigationKind.Project, glyph: "▣")
        {
            IsExpanded = true
        };

        var workbookGroup = new ProjectSpaceNavigationNode(
            $"{Id}:workbooks", "工作簿", Id, ProjectSpaceNavigationKind.WorkbookGroup, glyph: "▤");
        foreach (var workbook in Workbooks)
        {
            workbookGroup.Children.Add(new ProjectSpaceNavigationNode(
                workbook.Id, workbook.Name, Id, ProjectSpaceNavigationKind.Workbook, workbook.Id, "▧"));
        }

        var imageGroup = new ProjectSpaceNavigationNode(
            $"{Id}:images", "图片", Id, ProjectSpaceNavigationKind.ImageLibrary, glyph: "▦");
        var foldersById = ImageFolders.ToDictionary(
            folder => folder.Id,
            folder => new ProjectSpaceNavigationNode(
                folder.Id,
                folder.Name,
                Id,
                ProjectSpaceNavigationKind.ImageFolder,
                folder.Id,
                "▱"),
            StringComparer.Ordinal);

        foreach (var folder in ImageFolders)
        {
            var node = foldersById[folder.Id];
            if (folder.ParentFolderId is { } parentId && foldersById.TryGetValue(parentId, out var parent))
                parent.Children.Add(node);
            else
                imageGroup.Children.Add(node);
        }

        var videoGroup = new ProjectSpaceNavigationNode(
            $"{Id}:videos", "视频", Id, ProjectSpaceNavigationKind.VideoLibrary, glyph: "▶");

        root.Children.Add(workbookGroup);
        root.Children.Add(imageGroup);
        root.Children.Add(videoGroup);
        return root;
    }
}

public sealed class ProjectSpaceCreateRequestedEventArgs(Guid requestId, string name) : EventArgs
{
    public Guid RequestId { get; } = requestId;
    public string Name { get; } = name;

    /// <summary>
    /// The host should create these two workbook kinds as part of project creation, then
    /// return the backend-confirmed workbook records in the project snapshot.
    /// </summary>
    public IReadOnlyList<(string Name, string Kind)> RequiredInitialWorkbooks { get; } =
    [
        ("节拍表", "scene_beats"),
        ("资产表", "asset_review")
    ];
}

public sealed class ProjectSpaceRenameRequestedEventArgs(Guid requestId, string projectId, string name) : EventArgs
{
    public Guid RequestId { get; } = requestId;
    public string ProjectId { get; } = projectId;
    public string Name { get; } = name;
}

public sealed class ProjectSpaceWorkbookImportRequestedEventArgs(
    Guid requestId,
    string projectId,
    IReadOnlyList<string> filePaths) : EventArgs
{
    public Guid RequestId { get; } = requestId;
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<string> FilePaths { get; } = filePaths;
}

public sealed class ProjectSpaceMediaRequestEventArgs(
    string? projectId,
    ProjectSpaceMediaKind kind,
    string? folderId = null) : EventArgs
{
    /// <summary>Null means the host must query all visible project spaces and group results by project.</summary>
    public string? ProjectId { get; } = projectId;
    public bool IsAggregate => ProjectId == null;
    public ProjectSpaceMediaKind Kind { get; } = kind;
    public string? FolderId { get; } = folderId;
}

public sealed class ProjectSpaceUploadRequestedEventArgs(
    Guid requestId,
    string projectId,
    ProjectSpaceMediaKind kind,
    IReadOnlyList<string> filePaths,
    string? folderId) : EventArgs
{
    public Guid RequestId { get; } = requestId;
    public string ProjectId { get; } = projectId;
    public ProjectSpaceMediaKind Kind { get; } = kind;
    public IReadOnlyList<string> FilePaths { get; } = filePaths;
    public string? FolderId { get; } = folderId;
}

public sealed class ProjectSpaceMediaActionRequestedEventArgs(
    Guid requestId,
    string projectId,
    string mediaId) : EventArgs
{
    public Guid RequestId { get; } = requestId;
    public string ProjectId { get; } = projectId;
    public string MediaId { get; } = mediaId;
}

public sealed class ProjectSpaceMediaOpenRequestedEventArgs(ProjectSpaceMediaRecord media) : EventArgs
{
    public ProjectSpaceMediaRecord Media { get; } = media;
}

public sealed class ProjectSpaceRecycleBinRequestedEventArgs(string projectId) : EventArgs
{
    public string ProjectId { get; } = projectId;
}

public sealed class ProjectSpaceRestoreRequestedEventArgs(
    Guid requestId,
    string projectId,
    string itemId,
    string? originalFolderId) : EventArgs
{
    public Guid RequestId { get; } = requestId;
    public string ProjectId { get; } = projectId;
    public string ItemId { get; } = itemId;
    public string? OriginalFolderId { get; } = originalFolderId;
}

public sealed class ProjectSpaceWorkbookOpenRequestedEventArgs(
    string projectId,
    ProjectSpaceWorkbookRecord workbook) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public ProjectSpaceWorkbookRecord Workbook { get; } = workbook;
}

public sealed class ProjectSpaceHostNavigationRequestedEventArgs(ProjectSpaceShellPage page) : EventArgs
{
    public ProjectSpaceShellPage Page { get; } = page;
}

/// <summary>
/// View model for the file-management shell. It emits requests and changes visible state
/// only when the host applies backend-confirmed data through the Apply/Replace methods.
/// </summary>
public sealed class ProjectSpaceShellViewModel : ObservableModel
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm" };

    private readonly Dictionary<Guid, string> _pendingCreates = new();
    private readonly Dictionary<Guid, string> _pendingRenames = new();
    private string? _selectedProjectId;
    private string? _selectedFolderId;
    private string? _selectedWorkbookId;
    private ProjectSpaceShellPage _page = ProjectSpaceShellPage.ProjectOverview;
    private ProjectSpaceShellPage _pageBeforeRecycleBin = ProjectSpaceShellPage.Images;
    private string? _activePrimaryKey;
    private string _newProjectName = "";
    private string _renameProjectName = "";
    private bool _isCreatingProject;
    private bool _isRenamingProject;
    private string? _statusMessage;
    private object? _hostContent;

    public ObservableCollection<ProjectSpacePrimaryNavigationItem> PrimaryNavigation { get; } =
    [
        new("images", "图片", "▦"),
        new("videos", "视频", "▶"),
        new("personal", "个人空间", "♙"),
        new("transfers", "传输", "↕"),
        new("feedback", "反馈", "◌")
    ];

    public ObservableCollection<ProjectSpaceShellProject> Projects { get; } = [];
    public ObservableCollection<ProjectSpaceNavigationNode> ProjectNavigationRoots { get; } = [];
    public ObservableCollection<ProjectSpaceMediaGroup> MediaGroups { get; } = [];
    public ObservableCollection<ProjectSpaceRecycleBinRecord> RecycleBinItems { get; } = [];

    public event EventHandler<ProjectSpaceCreateRequestedEventArgs>? CreateProjectRequested;
    public event EventHandler<ProjectSpaceRenameRequestedEventArgs>? RenameProjectRequested;
    public event EventHandler<ProjectSpaceWorkbookImportRequestedEventArgs>? WorkbookImportRequested;
    public event EventHandler<ProjectSpaceMediaRequestEventArgs>? MediaRequested;
    public event EventHandler<ProjectSpaceUploadRequestedEventArgs>? MediaUploadRequested;
    public event EventHandler<ProjectSpaceMediaActionRequestedEventArgs>? ArchiveMediaRequested;
    public event EventHandler<ProjectSpaceMediaOpenRequestedEventArgs>? MediaOpenRequested;
    public event EventHandler<ProjectSpaceRecycleBinRequestedEventArgs>? RecycleBinRequested;
    public event EventHandler<ProjectSpaceRestoreRequestedEventArgs>? RestoreRequested;
    public event EventHandler<ProjectSpaceWorkbookOpenRequestedEventArgs>? WorkbookOpenRequested;
    public event EventHandler<ProjectSpaceHostNavigationRequestedEventArgs>? HostNavigationRequested;

    public string? SelectedProjectId
    {
        get => _selectedProjectId;
        private set
        {
            if (_selectedProjectId == value) return;
            _selectedProjectId = value;
            Changed();
            Changed(nameof(SelectedProject));
            Changed(nameof(SelectedProjectName));
            Changed(nameof(ScopeText));
            Changed(nameof(CanUpload));
            Changed(nameof(IsProjectSelected));
            Changed(nameof(IsNoProjectSelected));
            Changed(nameof(PageTitle));
            Changed(nameof(CanOpenRecycleBin));
            Changed(nameof(SelectedWorkbook));
            Changed(nameof(CanImportWorkbook));
        }
    }

    public ProjectSpaceShellProject? SelectedProject =>
        Projects.FirstOrDefault(project => project.Id == SelectedProjectId);
    public string SelectedProjectName => SelectedProject?.Name ?? "";
    public bool IsProjectSelected => SelectedProject != null;
    public bool IsNoProjectSelected => SelectedProject == null;

    public ProjectSpaceShellPage Page
    {
        get => _page;
        private set
        {
            if (_page == value) return;
            _page = value;
            Changed();
            Changed(nameof(PageTitle));
            Changed(nameof(IsProjectOverview));
            Changed(nameof(IsMediaPage));
            Changed(nameof(IsProjectScopedMediaPage));
            Changed(nameof(IsImagesPage));
            Changed(nameof(IsVideosPage));
            Changed(nameof(IsRecycleBinPage));
            Changed(nameof(IsHostContentPage));
            Changed(nameof(IsWorkbookPage));
            Changed(nameof(IsMediaEmpty));
            Changed(nameof(MediaEmptyTitle));
            Changed(nameof(MediaEmptyHint));
            Changed(nameof(CanUpload));
            Changed(nameof(CanOpenRecycleBin));
            Changed(nameof(CanImportWorkbook));
            Changed(nameof(ScopeText));
            Changed(nameof(UploadButtonText));
        }
    }

    public string PageTitle => Page switch
    {
        ProjectSpaceShellPage.ProjectOverview => SelectedProject?.Name ?? "项目",
        ProjectSpaceShellPage.Images or ProjectSpaceShellPage.ProjectImages => "图片",
        ProjectSpaceShellPage.Videos or ProjectSpaceShellPage.ProjectVideos => "视频",
        ProjectSpaceShellPage.ImageFolder => SelectedFolderName ?? "图片文件夹",
        ProjectSpaceShellPage.RecycleBin => "回收站",
        ProjectSpaceShellPage.PersonalSpace => "个人空间",
        ProjectSpaceShellPage.Transfers => "传输",
        ProjectSpaceShellPage.Feedback => "反馈",
        ProjectSpaceShellPage.Workbook => SelectedWorkbook?.Name ?? "工作簿",
        _ => "项目"
    };

    public ProjectSpaceWorkbookRecord? SelectedWorkbook =>
        SelectedProject?.Workbooks.FirstOrDefault(item => item.Id == _selectedWorkbookId);
    private string? SelectedFolderName =>
        SelectedProject?.ImageFolders.FirstOrDefault(item => item.Id == _selectedFolderId)?.Name;

    public string ScopeText => Page switch
    {
        ProjectSpaceShellPage.PersonalSpace => "仅自己可见 · 可读写",
        ProjectSpaceShellPage.Images or ProjectSpaceShellPage.Videos => "全部项目",
        ProjectSpaceShellPage.ProjectImages or ProjectSpaceShellPage.ProjectVideos or ProjectSpaceShellPage.ImageFolder
            or ProjectSpaceShellPage.RecycleBin when SelectedProject != null =>
                $"{SelectedProject.Name} · 全员可读写",
        ProjectSpaceShellPage.ProjectImages or ProjectSpaceShellPage.ProjectVideos or ProjectSpaceShellPage.ImageFolder
            or ProjectSpaceShellPage.RecycleBin => "请先从左侧选择项目",
        ProjectSpaceShellPage.ProjectOverview when SelectedProject != null => "项目 · 全员可读写",
        _ => ""
    };

    public bool IsProjectOverview => Page == ProjectSpaceShellPage.ProjectOverview;
    public bool IsImagesPage => Page is ProjectSpaceShellPage.Images or ProjectSpaceShellPage.ProjectImages;
    public bool IsVideosPage => Page is ProjectSpaceShellPage.Videos or ProjectSpaceShellPage.ProjectVideos;
    public bool IsMediaPage => Page is ProjectSpaceShellPage.Images or ProjectSpaceShellPage.Videos
        or ProjectSpaceShellPage.ProjectImages or ProjectSpaceShellPage.ProjectVideos or ProjectSpaceShellPage.ImageFolder;
    public bool IsProjectScopedMediaPage => Page is ProjectSpaceShellPage.ProjectImages or ProjectSpaceShellPage.ProjectVideos or ProjectSpaceShellPage.ImageFolder;
    public bool IsRecycleBinPage => Page == ProjectSpaceShellPage.RecycleBin;
    public bool IsHostContentPage => Page is ProjectSpaceShellPage.PersonalSpace or ProjectSpaceShellPage.Transfers or ProjectSpaceShellPage.Feedback or ProjectSpaceShellPage.Workbook;
    public bool IsWorkbookPage => Page == ProjectSpaceShellPage.Workbook;
    public bool IsMediaEmpty => IsMediaPage && MediaGroups.Count == 0;
    public string MediaEmptyTitle => IsProjectScopedMediaPage ? UploadButtonText : "暂无文件";
    public string MediaEmptyHint => IsProjectScopedMediaPage ? "拖入此处" : "";
    public bool CanUpload => SelectedProject != null && IsProjectScopedMediaPage;
    public bool CanOpenRecycleBin => SelectedProject != null && IsProjectScopedMediaPage;
    public bool CanImportWorkbook => SelectedProject != null && IsProjectOverview && !IsCreatingProject && !IsCreateRequestPending;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; Changed(); }
    }

    public object? HostContent
    {
        get => _hostContent;
        private set { _hostContent = value; Changed(); Changed(nameof(HasHostContent)); Changed(nameof(IsHostContentMissing)); }
    }
    public bool HasHostContent => HostContent != null;
    public bool IsHostContentMissing => HostContent == null;

    public bool IsCreatingProject
    {
        get => _isCreatingProject;
        private set { _isCreatingProject = value; Changed(); Changed(nameof(CanImportWorkbook)); }
    }

    public bool IsRenamingProject
    {
        get => _isRenamingProject;
        private set { _isRenamingProject = value; Changed(); }
    }

    public string NewProjectName
    {
        get => _newProjectName;
        set { _newProjectName = value; Changed(); }
    }

    public string RenameProjectName
    {
        get => _renameProjectName;
        set { _renameProjectName = value; Changed(); }
    }

    public bool IsCreateRequestPending => _pendingCreates.Count > 0;
    public bool IsRenameRequestPending => _pendingRenames.Count > 0;
    public bool CanCreateProject => !IsCreateRequestPending;
    public bool CanSaveRenameProject => !IsRenameRequestPending;

    public ProjectSpaceMediaKind ActiveMediaKind => Page is ProjectSpaceShellPage.Videos or ProjectSpaceShellPage.ProjectVideos
        ? ProjectSpaceMediaKind.Video
        : ProjectSpaceMediaKind.Image;

    public string UploadButtonText => ActiveMediaKind == ProjectSpaceMediaKind.Video ? "导入视频" : "导入图片";

    public ProjectSpaceShellViewModel()
    {
        MediaGroups.CollectionChanged += (_, _) => Changed(nameof(IsMediaEmpty));
        Projects.CollectionChanged += (_, _) =>
        {
            Changed(nameof(SelectedProject));
            Changed(nameof(SelectedProjectName));
            Changed(nameof(IsProjectSelected));
            Changed(nameof(IsNoProjectSelected));
            Changed(nameof(PageTitle));
            Changed(nameof(ScopeText));
        };
    }

    /// <summary>Replace navigation using projects already returned by the host/backend.</summary>
    public void ApplyProjects(IEnumerable<ProjectSpaceSnapshot> snapshots)
    {
        var previousId = SelectedProjectId;
        Projects.Clear();
        ProjectNavigationRoots.Clear();
        foreach (var snapshot in snapshots)
        {
            var project = new ProjectSpaceShellProject(snapshot);
            Projects.Add(project);
            ProjectNavigationRoots.Add(project.BuildNavigationRoot());
        }

        SelectedProjectId = Projects.Any(project => project.Id == previousId)
            ? previousId
            : Projects.FirstOrDefault()?.Id;
        if (SelectedProjectId is null && Page is ProjectSpaceShellPage.ProjectOverview)
            StatusMessage = "使用“项目 +”创建项目。";
        Changed(nameof(PageTitle));
        Changed(nameof(ScopeText));
    }

    /// <summary>Call only after the backend confirms project creation and its default workbooks.</summary>
    public void ApplyProjectCreated(Guid requestId, ProjectSpaceSnapshot snapshot)
    {
        if (!_pendingCreates.Remove(requestId)) return;
        var hasBeatSheet = snapshot.Workbooks.Any(item => Path.GetFileNameWithoutExtension(item.Name) == "节拍表" && item.Kind == "scene_beats");
        var hasAssetSheet = snapshot.Workbooks.Any(item => Path.GetFileNameWithoutExtension(item.Name) == "资产表" && item.Kind == "asset_review");
        if (!hasBeatSheet || !hasAssetSheet)
        {
            IsCreatingProject = false;
            StatusMessage = "项目未返回默认工作簿，未加入导航；请重新加载确认。";
            Changed(nameof(IsCreateRequestPending));
            Changed(nameof(CanCreateProject));
            Changed(nameof(CanImportWorkbook));
            return;
        }
        UpsertProject(snapshot);
        SelectedProjectId = snapshot.Id;
        IsCreatingProject = false;
        NewProjectName = "";
        StatusMessage = null;
        Navigate(ProjectSpaceShellPage.ProjectOverview);
        Changed(nameof(IsCreateRequestPending));
        Changed(nameof(CanCreateProject));
        Changed(nameof(CanImportWorkbook));
    }

    public void FailProjectCreation(Guid requestId, string message)
    {
        if (!_pendingCreates.Remove(requestId)) return;
        StatusMessage = message;
        Changed(nameof(IsCreateRequestPending));
        Changed(nameof(CanCreateProject));
        Changed(nameof(CanImportWorkbook));
    }

    /// <summary>Call only after the backend confirms the project rename.</summary>
    public void ApplyProjectRenamed(Guid requestId, string projectId, string name)
    {
        if (!_pendingRenames.Remove(requestId)) return;
        var project = Projects.FirstOrDefault(item => item.Id == projectId);
        if (project != null)
        {
            project.Name = name;
            RebuildNavigation(project);
            for (var index = 0; index < MediaGroups.Count; index++)
            {
                var group = MediaGroups[index];
                if (group.ProjectId == projectId)
                    MediaGroups[index] = new ProjectSpaceMediaGroup(projectId, name, group.Items);
            }
        }
        RenameProjectName = name;
        IsRenamingProject = false;
        StatusMessage = null;
        Changed(nameof(PageTitle));
        Changed(nameof(ScopeText));
        Changed(nameof(IsRenameRequestPending));
        Changed(nameof(CanSaveRenameProject));
    }

    public void FailProjectRename(Guid requestId, string message)
    {
        if (!_pendingRenames.Remove(requestId)) return;
        StatusMessage = message;
        Changed(nameof(IsRenameRequestPending));
        Changed(nameof(CanSaveRenameProject));
    }

    /// <summary>Apply one authoritative project snapshot after an import or host-side refresh.</summary>
    public void ApplyProjectSnapshot(ProjectSpaceSnapshot snapshot)
    {
        UpsertProject(snapshot);
        Changed(nameof(PageTitle));
        Changed(nameof(ScopeText));
        Changed(nameof(CanImportWorkbook));
    }

    /// <summary>Report a host-side load or mutation error without claiming a local success.</summary>
    public void SetHostStatus(string? message) => StatusMessage = message;

    /// <summary>
    /// Called by the workbook host after an asset row and its image-folder mapping are
    /// persisted. The tree is never populated from an uncommitted row draft.
    /// </summary>
    public void ApplyAssetRowCreated(ProjectSpaceAssetFolderRecord folder)
    {
        var project = Projects.FirstOrDefault(item => item.Id == folder.ProjectId);
        if (project == null) return;
        var existing = project.ImageFolders.FirstOrDefault(item => item.Id == folder.Id);
        if (existing == null) project.ImageFolders.Add(folder);
        else project.ImageFolders[project.ImageFolders.IndexOf(existing)] = folder;
        RebuildNavigation(project);
    }

    /// <summary>
    /// Apply the host's aggregate query result for the global Images/Videos navigation.
    /// The host supplies only projects visible to the current user and groups each result
    /// by its actual project; the shell does not fabricate groups or choose a default project.
    /// </summary>
    public void ReplaceAllProjectMedia(ProjectSpaceMediaKind kind, IEnumerable<ProjectSpaceMediaGroupRecord> groups)
    {
        var expectedPage = kind == ProjectSpaceMediaKind.Image ? ProjectSpaceShellPage.Images : ProjectSpaceShellPage.Videos;
        if (Page != expectedPage) return;
        MediaGroups.Clear();
        foreach (var group in groups)
        {
            var files = group.Items.Where(item => item.ProjectId == group.ProjectId && item.Kind == kind).ToArray();
            if (files.Length > 0) MediaGroups.Add(new ProjectSpaceMediaGroup(group.ProjectId, group.ProjectName, files));
        }
    }

    /// <summary>Apply authoritative media results scoped to the selected project or folder.</summary>
    public void ReplaceProjectMedia(string projectId, ProjectSpaceMediaKind kind, IEnumerable<ProjectSpaceMediaRecord> items)
    {
        if (SelectedProjectId != projectId || ActiveMediaKind != kind || !IsProjectScopedMediaPage) return;
        MediaGroups.Clear();
        var folderId = Page == ProjectSpaceShellPage.ImageFolder ? _selectedFolderId : null;
        var files = items.Where(item => item.ProjectId == projectId && item.Kind == kind &&
                         (folderId == null || item.FolderId == folderId)).ToArray();
        if (files.Length > 0 && SelectedProject != null)
            MediaGroups.Add(new ProjectSpaceMediaGroup(projectId, SelectedProject.Name, files));
    }

    public void ReplaceRecycleBin(string projectId, IEnumerable<ProjectSpaceRecycleBinRecord> items)
    {
        if (SelectedProjectId != projectId || Page != ProjectSpaceShellPage.RecycleBin) return;
        RecycleBinItems.Clear();
        foreach (var item in items.Where(item => item.ProjectId == projectId)) RecycleBinItems.Add(item);
    }

    public void SetHostContent(object? content) => HostContent = content;

    public void BeginCreateProject()
    {
        StatusMessage = null;
        NewProjectName = "";
        IsCreatingProject = true;
        IsRenamingProject = false;
    }

    public void CancelCreateProject()
    {
        if (IsCreateRequestPending) return;
        IsCreatingProject = false;
        StatusMessage = null;
    }

    public void RequestCreateProject()
    {
        var name = NewProjectName.Trim();
        if (name.Length == 0) { StatusMessage = "请输入项目名称。"; return; }
        if (CreateProjectRequested == null) { StatusMessage = "项目创建服务尚未接入。"; return; }

        var requestId = Guid.NewGuid();
        _pendingCreates.Add(requestId, name);
        Changed(nameof(IsCreateRequestPending));
        Changed(nameof(CanCreateProject));
        Changed(nameof(CanImportWorkbook));
        StatusMessage = null;
        CreateProjectRequested.Invoke(this, new ProjectSpaceCreateRequestedEventArgs(requestId, name));
    }

    public void BeginRenameProject()
    {
        if (SelectedProject == null) return;
        StatusMessage = null;
        RenameProjectName = SelectedProject.Name;
        IsRenamingProject = true;
        IsCreatingProject = false;
    }

    public void CancelRenameProject()
    {
        if (IsRenameRequestPending) return;
        IsRenamingProject = false;
        StatusMessage = null;
    }

    public void RequestRenameProject()
    {
        if (SelectedProject == null) return;
        var name = RenameProjectName.Trim();
        if (name.Length == 0) { StatusMessage = "请输入项目名称。"; return; }
        if (RenameProjectRequested == null) { StatusMessage = "项目改名服务尚未接入。"; return; }

        var requestId = Guid.NewGuid();
        _pendingRenames.Add(requestId, SelectedProject.Id);
        Changed(nameof(IsRenameRequestPending));
        Changed(nameof(CanSaveRenameProject));
        StatusMessage = null;
        RenameProjectRequested.Invoke(this, new ProjectSpaceRenameRequestedEventArgs(requestId, SelectedProject.Id, name));
    }

    public void NavigatePrimary(string key)
    {
        StatusMessage = null;
        HostContent = null;
        switch (key)
        {
            case "images":
                _activePrimaryKey = key;
                _selectedFolderId = null;
                Navigate(ProjectSpaceShellPage.Images);
                RequestMedia(ProjectSpaceMediaKind.Image, null);
                break;
            case "videos":
                _activePrimaryKey = key;
                _selectedFolderId = null;
                Navigate(ProjectSpaceShellPage.Videos);
                RequestMedia(ProjectSpaceMediaKind.Video, null);
                break;
            case "personal":
                _activePrimaryKey = key;
                Navigate(ProjectSpaceShellPage.PersonalSpace);
                HostNavigationRequested?.Invoke(this, new ProjectSpaceHostNavigationRequestedEventArgs(Page));
                break;
            case "transfers":
                _activePrimaryKey = key;
                Navigate(ProjectSpaceShellPage.Transfers);
                HostNavigationRequested?.Invoke(this, new ProjectSpaceHostNavigationRequestedEventArgs(Page));
                break;
            case "feedback":
                _activePrimaryKey = key;
                Navigate(ProjectSpaceShellPage.Feedback);
                HostNavigationRequested?.Invoke(this, new ProjectSpaceHostNavigationRequestedEventArgs(Page));
                break;
            default:
                return;
        }
        RefreshPrimaryNavigation();
    }

    public void Navigate(ProjectSpaceNavigationNode node)
    {
        if (node.Kind == ProjectSpaceNavigationKind.WorkbookGroup) return;
        StatusMessage = null;
        HostContent = null;
        SelectedProjectId = node.ProjectId;

        switch (node.Kind)
        {
            case ProjectSpaceNavigationKind.Project:
                _activePrimaryKey = null;
                _selectedFolderId = null;
                _selectedWorkbookId = null;
                Navigate(ProjectSpaceShellPage.ProjectOverview);
                break;
            case ProjectSpaceNavigationKind.Workbook:
                _activePrimaryKey = null;
                _selectedWorkbookId = node.TargetId;
                var workbook = SelectedProject?.Workbooks.FirstOrDefault(item => item.Id == node.TargetId);
                if (workbook == null) return;
                Navigate(ProjectSpaceShellPage.Workbook);
                RaiseWorkbookOpen(node.ProjectId, workbook);
                break;
            case ProjectSpaceNavigationKind.ImageLibrary:
                _activePrimaryKey = "images";
                _selectedFolderId = null;
                Navigate(ProjectSpaceShellPage.ProjectImages);
                RequestMedia(ProjectSpaceMediaKind.Image, null);
                break;
            case ProjectSpaceNavigationKind.VideoLibrary:
                _activePrimaryKey = "videos";
                Navigate(ProjectSpaceShellPage.ProjectVideos);
                RequestMedia(ProjectSpaceMediaKind.Video, null);
                break;
            case ProjectSpaceNavigationKind.ImageFolder:
                _activePrimaryKey = "images";
                _selectedFolderId = node.TargetId;
                Navigate(ProjectSpaceShellPage.ImageFolder);
                RequestMedia(ProjectSpaceMediaKind.Image, _selectedFolderId);
                break;
            case ProjectSpaceNavigationKind.WorkbookGroup:
                break;
        }
        RefreshPrimaryNavigation();
    }

    public void OpenProjectOverview(string projectId)
    {
        var root = ProjectNavigationRoots.FirstOrDefault(item => item.ProjectId == projectId && item.Kind == ProjectSpaceNavigationKind.Project);
        if (root != null) Navigate(root);
    }

    public void OpenWorkbook(ProjectSpaceWorkbookRecord workbook)
    {
        if (SelectedProject == null || !SelectedProject.Workbooks.Any(item => item.Id == workbook.Id)) return;
        _activePrimaryKey = null;
        _selectedWorkbookId = workbook.Id;
        Navigate(ProjectSpaceShellPage.Workbook);
        RaiseWorkbookOpen(SelectedProject.Id, workbook);
    }

    private void RaiseWorkbookOpen(string projectId, ProjectSpaceWorkbookRecord workbook) =>
        WorkbookOpenRequested?.Invoke(this, new ProjectSpaceWorkbookOpenRequestedEventArgs(projectId, workbook));

    public void RequestWorkbookImport(IEnumerable<string> filePaths)
    {
        var paths = filePaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!CanImportWorkbook || SelectedProject == null)
        {
            StatusMessage = "请先打开一个项目。";
            return;
        }
        if (paths.Length == 0) return;
        if (paths.Any(path => !string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "项目只接收 .xlsx 文件。";
            return;
        }
        if (WorkbookImportRequested == null)
        {
            StatusMessage = "工作簿导入服务尚未接入。";
            return;
        }
        StatusMessage = null;
        WorkbookImportRequested.Invoke(this,
            new ProjectSpaceWorkbookImportRequestedEventArgs(Guid.NewGuid(), SelectedProject.Id, paths));
    }

    public void RequestMediaUpload(IEnumerable<string> filePaths, ProjectSpaceMediaKind kind)
    {
        var paths = filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!CanUpload || SelectedProject == null)
        {
            StatusMessage = IsMediaPage ? "请从项目树中的图片或视频位置导入文件。" : "请先选择图片或视频页面。";
            return;
        }
        if (kind != ActiveMediaKind) { StatusMessage = "请将文件拖入对应的图片或视频页面。"; return; }
        if (paths.Length == 0) return;

        var supported = kind == ProjectSpaceMediaKind.Image ? ImageExtensions : VideoExtensions;
        if (paths.Any(path => !supported.Contains(Path.GetExtension(path))))
        {
            StatusMessage = kind == ProjectSpaceMediaKind.Image
                ? "此处只接收图片文件。"
                : "此处只接收视频文件。";
            return;
        }
        if (MediaUploadRequested == null) { StatusMessage = "媒体上传服务尚未接入。"; return; }

        StatusMessage = null;
        MediaUploadRequested.Invoke(this, new ProjectSpaceUploadRequestedEventArgs(
            Guid.NewGuid(), SelectedProject.Id, kind, paths, Page == ProjectSpaceShellPage.ImageFolder ? _selectedFolderId : null));
    }

    public bool CanAcceptDroppedFiles(IEnumerable<string> filePaths, ProjectSpaceMediaKind kind)
    {
        if (!CanUpload || SelectedProject == null || kind != ActiveMediaKind) return false;
        var paths = filePaths.ToArray();
        if (paths.Length == 0) return false;
        var supported = kind == ProjectSpaceMediaKind.Image ? ImageExtensions : VideoExtensions;
        return paths.All(path => supported.Contains(Path.GetExtension(path)));
    }

    public void OpenRecycleBin()
    {
        if (!CanOpenRecycleBin || SelectedProject == null) return;
        _pageBeforeRecycleBin = Page;
        StatusMessage = null;
        Navigate(ProjectSpaceShellPage.RecycleBin);
        RecycleBinRequested?.Invoke(this, new ProjectSpaceRecycleBinRequestedEventArgs(SelectedProject.Id));
    }

    public void BackFromRecycleBin()
    {
        if (SelectedProject == null) { Navigate(ProjectSpaceShellPage.ProjectOverview); return; }
        var previousPage = _pageBeforeRecycleBin is ProjectSpaceShellPage.ProjectImages or ProjectSpaceShellPage.ProjectVideos or ProjectSpaceShellPage.ImageFolder
            ? _pageBeforeRecycleBin
            : ProjectSpaceShellPage.ProjectImages;
        Navigate(previousPage);
        if (previousPage == ProjectSpaceShellPage.ProjectVideos) RequestMedia(ProjectSpaceMediaKind.Video, null);
        else RequestMedia(ProjectSpaceMediaKind.Image, previousPage == ProjectSpaceShellPage.ImageFolder ? _selectedFolderId : null);
    }

    public void RequestArchive(ProjectSpaceMediaRecord item)
    {
        if (ArchiveMediaRequested == null) return;
        ArchiveMediaRequested.Invoke(this, new ProjectSpaceMediaActionRequestedEventArgs(Guid.NewGuid(), item.ProjectId, item.Id));
    }

    public void RequestRestore(ProjectSpaceRecycleBinRecord item)
    {
        if (SelectedProjectId != item.ProjectId || RestoreRequested == null) return;
        RestoreRequested.Invoke(this, new ProjectSpaceRestoreRequestedEventArgs(
            Guid.NewGuid(), item.ProjectId, item.Id, item.OriginalFolderId));
    }

    public void RequestOpenMedia(ProjectSpaceMediaRecord item)
    {
        MediaOpenRequested?.Invoke(this, new ProjectSpaceMediaOpenRequestedEventArgs(item));
    }

    private void RequestMedia(ProjectSpaceMediaKind kind, string? folderId)
    {
        MediaGroups.Clear();
        if (Page is ProjectSpaceShellPage.Images or ProjectSpaceShellPage.Videos)
        {
            MediaRequested?.Invoke(this, new ProjectSpaceMediaRequestEventArgs(null, kind));
            return;
        }
        if (SelectedProject == null)
        {
            StatusMessage = "请先从左侧项目列表选择一个项目。";
            return;
        }
        MediaRequested?.Invoke(this, new ProjectSpaceMediaRequestEventArgs(SelectedProject.Id, kind, folderId));
    }

    private void Navigate(ProjectSpaceShellPage page)
    {
        Page = page;
        RefreshPrimaryNavigation();
    }

    private void RefreshPrimaryNavigation()
    {
        foreach (var item in PrimaryNavigation)
            item.IsActive = item.Key == _activePrimaryKey;
    }

    private void UpsertProject(ProjectSpaceSnapshot snapshot)
    {
        var existing = Projects.FirstOrDefault(project => project.Id == snapshot.Id);
        if (existing != null)
        {
            existing.Name = snapshot.Name;
            existing.Workbooks.Clear();
            foreach (var workbook in snapshot.Workbooks) existing.Workbooks.Add(workbook);
            existing.ImageFolders.Clear();
            foreach (var folder in snapshot.ImageFolders) existing.ImageFolders.Add(folder);
            RebuildNavigation(existing);
            return;
        }

        var project = new ProjectSpaceShellProject(snapshot);
        Projects.Add(project);
        ProjectNavigationRoots.Add(project.BuildNavigationRoot());
    }

    private void RebuildNavigation(ProjectSpaceShellProject project)
    {
        var index = Projects.IndexOf(project);
        if (index < 0) return;
        ProjectNavigationRoots[index] = project.BuildNavigationRoot();
    }
}

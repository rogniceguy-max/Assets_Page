using System.Collections.ObjectModel;
using MuYingDrive.NativeWpf.Infrastructure;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>What a row in the sidebar's 项目 tree stands for.</summary>
public enum ProjectNodeKind
{
    /// <summary>A project. <c>Id</c> is the project directory's inode.</summary>
    Project,
    /// <summary>The 工作簿 group under a project. Not itself openable.</summary>
    WorkbookGroup,
    /// <summary>One workbook. <c>Id</c> is the file id the document route takes.</summary>
    Workbook,
    /// <summary>The 图片 or 视频 group under a project: a view over the project's directory.</summary>
    MediaGroup,
    /// <summary>A folder inside a project.</summary>
    Folder
}

/// <summary>
/// One row of the 项目 tree, at any depth.
/// </summary>
/// <remarks>
/// Ported from the handoff package's <c>ProjectSpaceNavigationNode</c> (:110-126 there), with
/// the parts that only made sense against endpoints that do not exist yet dropped — the
/// image-folder parent links, the recycle bin, and the asset-row binding. One node type
/// covers every level, so the view has a single template and the tree is data rather than a
/// class hierarchy.
///
/// Two identities matter and they are not the same thing:
/// <list type="bullet">
/// <item><c>Id</c> — for a project, the directory's <b>inode</b>; for a workbook, the file id.
/// These are the stable ones, and they are what the client joins on.</item>
/// <item><c>ProjectName</c> — the display name, which the user may change. It is carried
/// because the media views enumerate the project's directory over SMB, and a directory is
/// found by name. <b>A rename therefore breaks the media half of the tree until it is
/// reloaded.</b> That is a known limitation of this cut, not an oversight: the backend
/// exposes no name-independent way to reach a project's directory yet.</item>
/// </list>
/// </remarks>
public sealed class ProjectNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public ProjectNode(string id, string name, ProjectNodeKind kind, string icon)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Icon = icon;
    }

    public string Id { get; }
    public string Name { get; }
    public ProjectNodeKind Kind { get; }

    /// <summary>A <c>LucideIcon</c> name. Unknown names render blank rather than throwing.</summary>
    public string Icon { get; }

    /// <summary>
    /// The project this node belongs to: <c>ProjectId</c> is the stable identity the backend
    /// takes, <c>ProjectName</c> is the display name. Both are carried because the two
    /// halves of a project are reached differently — the workbook routes take the id, the
    /// media views enumerate a directory by name.
    /// </summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>The project this node belongs to, by name.</summary>
    public string ProjectName { get; init; } = string.Empty;

    public ObservableCollection<ProjectNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Whether activating this row is meant to open something.</summary>
    public bool IsOpenable => Kind is ProjectNodeKind.Workbook or ProjectNodeKind.MediaGroup or ProjectNodeKind.Folder;
}

/// <summary>Builds the tree the sidebar shows, and names its groups.</summary>
public static class ProjectNavigation
{
    public const string WorkbookGroupName = "工作簿";
    public const string ImageGroupName = "图片";
    public const string VideoGroupName = "视频";

    /// <summary>One workbook row: the file id the document route takes, and its label.</summary>
    public readonly record struct WorkbookEntry(string FileId, string Name);

    /// <summary>
    /// The tree for one project: the project itself, its workbooks, and the two media views.
    /// </summary>
    /// <remarks>
    /// The groups are always present, even when empty. A project with no workbooks yet is a
    /// real state — a freshly created one, before the template service exists — and hiding
    /// the group would make "nothing here yet" indistinguishable from "this is not what this
    /// row does". The content area says which of the two it is.
    /// </remarks>
    public static ProjectNode ForProject(
        string projectId, string projectName, IReadOnlyList<WorkbookEntry> workbooks)
    {
        var project = new ProjectNode(projectId, projectName, ProjectNodeKind.Project, "FolderOpen")
        {
            ProjectId = projectId,
            ProjectName = projectName,
            IsExpanded = true
        };

        var workbookGroup = new ProjectNode(
            $"{projectId}:workbooks", WorkbookGroupName, ProjectNodeKind.WorkbookGroup, "Table")
        {
            ProjectId = projectId,
            ProjectName = projectName,
            IsExpanded = true
        };
        foreach (var workbook in workbooks)
        {
            workbookGroup.Children.Add(new ProjectNode(
                workbook.FileId, workbook.Name, ProjectNodeKind.Workbook, "File")
            {
                ProjectId = projectId,
                ProjectName = projectName
            });
        }

        var images = new ProjectNode(
            $"{projectId}:images", ImageGroupName, ProjectNodeKind.MediaGroup, "Image")
        {
            ProjectId = projectId,
            ProjectName = projectName
        };
        var videos = new ProjectNode(
            $"{projectId}:videos", VideoGroupName, ProjectNodeKind.MediaGroup, "Video")
        {
            ProjectId = projectId,
            ProjectName = projectName
        };

        project.Children.Add(workbookGroup);
        project.Children.Add(images);
        project.Children.Add(videos);
        return project;
    }
}

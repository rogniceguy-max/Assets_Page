using MuYingDrive.NativeWpf.Infrastructure;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>One entry in the workspace sidebar, either a navigation page or a space.</summary>
public sealed class WorkspaceNavItem : ObservableObject
{
    private bool _isSelected;

    public WorkspaceNavItem(string key, string label, string icon, bool isEnabled = true,
        string? disabledReason = null)
    {
        Key = key;
        Label = label;
        Icon = icon;
        IsEnabled = isEnabled;
        DisabledReason = disabledReason;
    }

    public string Key { get; }
    public string Label { get; }
    public string Icon { get; }
    public bool IsEnabled { get; }
    public string? DisabledReason { get; }
    public string ToolTip => IsEnabled || string.IsNullOrWhiteSpace(DisabledReason)
        ? Label
        : $"{Label}：{DisabledReason}";

    /// <summary>Highlighted in the sidebar. Driven by the workspace, not by bindings.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// Data for the common workspace shell. Session capabilities enable or disable entries.
/// </summary>
/// <remarks>
/// Employees and visitors render the same navigation structure. Items unavailable to a
/// read-only session remain visible with an explanation.
/// </remarks>
/// <param name="NavigationItems">Pages shown at the top of the sidebar.</param>
/// <param name="SpaceItems">Spaces listed under the "空间" heading.</param>
/// <param name="Projects">
/// The 项目 group. Empty is a real state — a workspace whose projects have not loaded, or a
/// until the list has loaded, and the sidebar says so rather than showing an empty box.
/// </param>
/// <param name="AccountLabel">Text shown in the account button.</param>
/// <param name="ExitLabel">Label for the account menu action.</param>
/// <param name="ShowUpdateEntry">Whether the update button appears in the header.</param>
public sealed record WorkspaceChrome(
    IReadOnlyList<WorkspaceNavItem> NavigationItems,
    IReadOnlyList<WorkspaceNavItem> SpaceItems,
    IReadOnlyList<ProjectNode> Projects,
    string AccountLabel,
    string ExitLabel,
    bool ShowUpdateEntry);

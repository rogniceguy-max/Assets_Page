using MuYingDrive.NativeWpf.Infrastructure;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>One entry in the workspace sidebar, either a navigation page or a space.</summary>
public sealed class WorkspaceNavItem : ObservableObject
{
    private bool _isSelected;

    public WorkspaceNavItem(string key, string label, string icon)
    {
        Key = key;
        Label = label;
        Icon = icon;
    }

    public string Key { get; }
    public string Label { get; }
    public string Icon { get; }

    /// <summary>Highlighted in the sidebar. Driven by the workspace, not by bindings.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// Everything that differs between the employee workspace and the read-only tour.
/// </summary>
/// <remarks>
/// The two render the same shell; only this differs. The tour supplies a single
/// navigation entry and three read-only spaces, so the transfer and feedback pages are
/// absent from the sidebar rather than merely unreachable.
/// </remarks>
/// <param name="NavigationItems">Pages shown at the top of the sidebar.</param>
/// <param name="SpaceItems">Spaces listed under the "空间" heading.</param>
/// <param name="AccountLabel">Text shown in the account button.</param>
/// <param name="ExitLabel">Label for the account menu action.</param>
/// <param name="ShowUpdateEntry">Whether the update button appears in the header.</param>
public sealed record WorkspaceChrome(
    IReadOnlyList<WorkspaceNavItem> NavigationItems,
    IReadOnlyList<WorkspaceNavItem> SpaceItems,
    string AccountLabel,
    string ExitLabel,
    bool ShowUpdateEntry);

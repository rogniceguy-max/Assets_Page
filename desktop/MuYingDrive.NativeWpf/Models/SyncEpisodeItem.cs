using MuYingDrive.NativeWpf.Infrastructure;

namespace MuYingDrive.NativeWpf.Models;

/// <summary>
/// An episode choice available for synchronization.
/// </summary>
public sealed class SyncEpisodeItem : ObservableObject
{
    private bool _isSelected;

    public required string Name { get; init; }
    public required string FileId { get; init; }
    public string? SheetId { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

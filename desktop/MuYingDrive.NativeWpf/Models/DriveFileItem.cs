using MuYingDrive.NativeWpf.Infrastructure;

namespace MuYingDrive.NativeWpf.Models;

/// <summary>A file or folder discovered in the authenticated SMB share.</summary>
public sealed class DriveFileItem : ObservableObject
{
    private double _aspectRatio = 1.4;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required string ThumbnailPath { get; init; }
    public required string FileSize { get; init; }
    public required string KindLabel { get; init; }
    public required string ModifiedLabel { get; init; }
    public double AspectRatio
    {
        get => _aspectRatio;
        set => SetProperty(ref _aspectRatio, Math.Clamp(value, 0.45, 3.4));
    }
    public bool IsVideo { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsReadOnly { get; init; }
    public long SizeBytes { get; init; }
    public DateTime ModifiedTime { get; init; }
    public string LogicalPath { get; init; } = string.Empty;
}

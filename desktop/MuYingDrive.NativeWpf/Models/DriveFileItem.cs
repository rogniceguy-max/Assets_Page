using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.Models;

/// <summary>A logical file or folder returned by the authenticated file API.</summary>
public sealed class DriveFileItem : ObservableObject
{
    private double _aspectRatio = 1.4;
    private string _thumbnailPath = string.Empty;
    private bool _hasThumbnail;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public string ThumbnailPath
    {
        get => _thumbnailPath;
        set => SetProperty(ref _thumbnailPath, value);
    }
    public bool HasThumbnail
    {
        get => _hasThumbnail;
        set => SetProperty(ref _hasThumbnail, value);
    }
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
    public string? FileId { get; init; }
    public string? DirectoryId { get; init; }
    public string ScopeId { get; init; } = string.Empty;
    public string? ProjectId { get; init; }
    public string? RevisionId { get; init; }
    public long Version { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public string? MediaType { get; init; }
    public FileThumbnailProvider? FileThumbnailProvider { get; init; }
}

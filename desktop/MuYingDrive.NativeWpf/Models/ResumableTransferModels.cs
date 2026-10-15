namespace MuYingDrive.NativeWpf.Models;

public enum ResumableTransferDirection
{
    Upload,
    Download
}

public enum ResumableTransferStatus
{
    Queued,
    Running,
    Paused,
    Failed,
    Completed,
    Canceled
}

/// <summary>Persisted presentation context for files that belong to one folder upload.</summary>
public sealed record TransferFolderContext(
    string GroupId, string FolderName, string DestinationDirectory, int ExpectedChildCount = 0);

/// <summary>A UI-independent view of a single-file transfer.</summary>
public sealed record ResumableTransferSnapshot(
    string Id,
    string FileName,
    ResumableTransferDirection Direction,
    ResumableTransferStatus Status,
    long BytesTransferred,
    long TotalBytes,
    string Location,
    double ProgressPercent,
    string? Error,
    DateTimeOffset LastActivityUtc,
    TransferFolderContext? FolderContext = null,
    long Revision = 0);

public sealed class ResumableTransferChangedEventArgs(ResumableTransferSnapshot snapshot) : EventArgs
{
    public ResumableTransferSnapshot Snapshot { get; } = snapshot;
}

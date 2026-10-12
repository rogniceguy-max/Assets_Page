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
    DateTimeOffset LastActivityUtc);

public sealed class ResumableTransferChangedEventArgs(ResumableTransferSnapshot snapshot) : EventArgs
{
    public ResumableTransferSnapshot Snapshot { get; } = snapshot;
}

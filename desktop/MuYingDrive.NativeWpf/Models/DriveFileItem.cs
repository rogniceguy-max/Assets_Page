using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.Models;

/// <summary>A logical file or folder returned by the authenticated file API.</summary>
public sealed class DriveFileItem : ObservableObject
{
    private double _aspectRatio = 1.4;
    private string _thumbnailPath = string.Empty;
    private bool _hasThumbnail;
    private string? _inlineTransferOperationId;
    private string? _inlineTransferStatus;
    private string? _inlineTransferError;
    private double _inlineTransferProgress;
    private bool _inlineTransferHasProgress;
    private bool _inlineTransferCanRetry;
    private bool _inlineTransferCanCancel;

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
    public string? Sha256 { get; init; }
    public string? DirectoryId { get; init; }
    public string ScopeId { get; init; } = string.Empty;
    public string? ProjectId { get; init; }
    public string? RevisionId { get; init; }
    public long Version { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public string? MediaType { get; init; }
    public FileThumbnailProvider? FileThumbnailProvider { get; init; }

    /// <summary>Download feedback shown on this file row/card instead of a dialog.</summary>
    public string? InlineTransferOperationId
    {
        get => _inlineTransferOperationId;
        private set => SetProperty(ref _inlineTransferOperationId, value);
    }
    public string? InlineTransferStatus
    {
        get => _inlineTransferStatus;
        private set
        {
            if (!SetProperty(ref _inlineTransferStatus, value)) return;
            OnPropertyChanged(nameof(HasInlineTransfer));
        }
    }
    public string? InlineTransferError
    {
        get => _inlineTransferError;
        private set
        {
            if (!SetProperty(ref _inlineTransferError, value)) return;
            OnPropertyChanged(nameof(HasInlineTransferError));
        }
    }
    public bool HasInlineTransfer => !string.IsNullOrWhiteSpace(InlineTransferStatus);
    public bool HasInlineTransferError => !string.IsNullOrWhiteSpace(InlineTransferError);
    public double InlineTransferProgress { get => _inlineTransferProgress; private set => SetProperty(ref _inlineTransferProgress, value); }
    public bool InlineTransferHasProgress { get => _inlineTransferHasProgress; private set => SetProperty(ref _inlineTransferHasProgress, value); }
    public bool InlineTransferCanRetry { get => _inlineTransferCanRetry; private set => SetProperty(ref _inlineTransferCanRetry, value); }
    public bool InlineTransferCanCancel { get => _inlineTransferCanCancel; private set => SetProperty(ref _inlineTransferCanCancel, value); }

    public void BeginInlineTransfer(string operationId, string status, bool canCancel = false)
    {
        InlineTransferOperationId = operationId;
        InlineTransferError = null;
        InlineTransferStatus = status;
        InlineTransferProgress = 0;
        InlineTransferHasProgress = false;
        InlineTransferCanRetry = false;
        InlineTransferCanCancel = canCancel;
    }

    public void ApplyInlineTransfer(TransferAgentTaskSnapshot snapshot)
    {
        InlineTransferOperationId = snapshot.OperationId;
        InlineTransferStatus = TransferTaskStateProjection.Label(snapshot.State);
        InlineTransferError = snapshot.State is "failed" or "conflict" ? snapshot.Message : null;
        InlineTransferProgress = TransferTaskStateProjection.Progress(snapshot.BytesDone, snapshot.BytesTotal, snapshot.State);
        InlineTransferHasProgress = snapshot.BytesTotal is > 0;
        InlineTransferCanRetry = snapshot.Retryable;
        InlineTransferCanCancel = TransferTaskStateProjection.CanCancel(snapshot.State, snapshot.CommitStarted);
    }

    public void SetInlineTransferError(string message, bool canRetry = true)
    {
        InlineTransferStatus = "失败";
        InlineTransferError = message;
        InlineTransferCanRetry = canRetry;
        InlineTransferCanCancel = false;
    }

    public void SetInlineTransferStatus(string status)
    {
        InlineTransferStatus = status;
        InlineTransferError = null;
        InlineTransferProgress = 100;
        InlineTransferHasProgress = false;
        InlineTransferCanRetry = false;
        InlineTransferCanCancel = false;
    }
}

using MuYingDrive.NativeWpf.Infrastructure;

namespace MuYingDrive.NativeWpf.Models;

public enum TransferState
{
    Queued,
    Running,
    Paused,
    Failed,
    Completed,
    Conflict,
    Cancelled
}

/// <summary>Observable UI projection of a persisted real transfer task.</summary>
public sealed class TransferTask : ObservableObject
{
    private TransferState _state;
    private double _progress;
    private long _bytesTransferred;
    private string? _errorMessage;
    private bool _isFolderTransfer;
    private bool? _canRetryOverride;
    private bool _commitStarted;
    private long _lastRevision = -1;
    private DateTimeOffset _lastAgentUpdated = DateTimeOffset.MinValue;

    public TransferTask(ResumableTransferSnapshot snapshot) => Update(snapshot);
    public TransferTask(TransferAgentTaskSnapshot snapshot) => Update(snapshot);

    public string Id { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string Direction { get; private set; } = string.Empty;
    public string Location { get; private set; } = string.Empty;
    public string Size { get; private set; } = string.Empty;
    public long TotalBytes { get; private set; }
    public bool IsFolderTransfer
    {
        get => _isFolderTransfer;
        private set
        {
            if (SetProperty(ref _isFolderTransfer, value))
                OnPropertyChanged(nameof(IconName));
        }
    }
    public string IconName => IsFolderTransfer ? "Folder" : "File";
    public long BytesTransferred
    {
        get => _bytesTransferred;
        private set
        {
            if (SetProperty(ref _bytesTransferred, value))
            {
                OnPropertyChanged(nameof(ProgressLabel));
                OnPropertyChanged(nameof(TransferSummary));
            }
        }
    }

    public TransferState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(StateBrushKey));
                OnPropertyChanged(nameof(TransferSummary));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, Math.Clamp(value, 0, 100)))
                OnPropertyChanged(nameof(ProgressLabel));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool CommitOutcomeUnknown => _commitStarted &&
        State is not (TransferState.Completed or TransferState.Conflict or TransferState.Cancelled);
    public string CommitOutcomeNotice => CommitOutcomeUnknown
        ? "提交结果尚未确认。代理会用同一操作编号重试，确认前不能取消。"
        : string.Empty;
    public bool CanPause => !CommitOutcomeUnknown &&
        (State is TransferState.Running or TransferState.Queued);
    public bool CanResume => State == TransferState.Paused;
    public bool CanRetry => _canRetryOverride ?? State is TransferState.Failed or TransferState.Conflict;
    public bool CanCancel => !CommitOutcomeUnknown &&
        (State is TransferState.Queued or TransferState.Running or TransferState.Paused or TransferState.Failed);

    public string StateLabel => State switch
    {
        TransferState.Queued => "等待中",
        TransferState.Running => "进行中",
        TransferState.Paused => "已暂停",
        TransferState.Failed => "失败",
        TransferState.Conflict => "发生冲突",
        TransferState.Cancelled => "已取消",
        _ => "已完成"
    };

    public string ProgressLabel => $"{FormatSize(BytesTransferred)} / {Size} · {Progress:0}%";
    public string TransferSummary => State == TransferState.Completed
        ? $"{Direction} · {Size}"
        : $"{Direction} · {FormatSize(BytesTransferred)} / {Size}";
    public string StateBrushKey => State is TransferState.Failed or TransferState.Conflict ? "Brush.Status.Danger" : "Brush.Text.Secondary";

    public TransferTask(string id, string name, string direction, long totalBytes, long bytesTransferred,
        TransferState state, string? error = null, bool isFolderTransfer = false)
    {
        Id = id;
        FileName = name;
        Direction = direction;
        IsFolderTransfer = isFolderTransfer;
        TotalBytes = totalBytes;
        Size = FormatSize(totalBytes);
        BytesTransferred = bytesTransferred;
        Progress = totalBytes > 0
            ? (double)bytesTransferred / totalBytes * 100
            : state == TransferState.Completed ? 100 : 0;
        _commitStarted = false;
        State = state;
        _commitStarted = false;
        OnPropertyChanged(nameof(CommitOutcomeUnknown));
        OnPropertyChanged(nameof(CommitOutcomeNotice));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanCancel));
        ErrorMessage = error;
    }

    public void UpdateManual(long totalBytes, long bytesTransferred, TransferState state, string? error = null,
        bool? canRetry = null)
    {
        if (_canRetryOverride != canRetry)
        {
            _canRetryOverride = canRetry;
            OnPropertyChanged(nameof(CanRetry));
        }
        TotalBytes = totalBytes;
        Size = FormatSize(totalBytes);
        BytesTransferred = bytesTransferred;
        Progress = totalBytes > 0
            ? (double)bytesTransferred / totalBytes * 100
            : state == TransferState.Completed ? 100 : 0;
        State = state;
        ErrorMessage = error;
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(BytesTransferred));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(TransferSummary));
        OnPropertyChanged(nameof(ProgressLabel));
    }

    public void Update(ResumableTransferSnapshot snapshot)
    {
        if (snapshot.Revision < _lastRevision) return;
        _lastRevision = snapshot.Revision;
        Id = snapshot.Id;
        FileName = snapshot.FolderContext?.FolderName ?? snapshot.FileName;
        Direction = snapshot.Direction == ResumableTransferDirection.Upload ? "上传" : "下载";
        IsFolderTransfer = snapshot.FolderContext is not null;
        Location = snapshot.Location;
        TotalBytes = snapshot.TotalBytes;
        Size = FormatSize(TotalBytes);
        BytesTransferred = snapshot.BytesTransferred;
        Progress = snapshot.ProgressPercent;
        _commitStarted = false;
        State = snapshot.Status switch
        {
            ResumableTransferStatus.Queued => TransferState.Queued,
            ResumableTransferStatus.Running => TransferState.Running,
            ResumableTransferStatus.Paused => TransferState.Paused,
            ResumableTransferStatus.Failed => TransferState.Failed,
            _ => TransferState.Completed
        };
        ErrorMessage = snapshot.Error;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(TransferSummary));
        OnPropertyChanged(nameof(ProgressLabel));
    }

    public void Update(TransferAgentTaskSnapshot snapshot)
    {
        if (snapshot.UpdatedAt < _lastAgentUpdated) return;
        var previousOutcomeUnknown = CommitOutcomeUnknown;
        _lastAgentUpdated = snapshot.UpdatedAt;
        Id = string.IsNullOrWhiteSpace(snapshot.TaskId) ? snapshot.OperationId : snapshot.TaskId;
        FileName = string.IsNullOrWhiteSpace(snapshot.DisplayName) ? snapshot.LogicalPath ?? Id : snapshot.DisplayName;
        Direction = snapshot.Kind.Contains("download", StringComparison.OrdinalIgnoreCase) ? "下载" : "上传";
        IsFolderTransfer = snapshot.Kind.Contains("batch", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(snapshot.FolderGroup);
        Location = snapshot.LogicalPath ?? string.Empty;
        TotalBytes = Math.Max(0, snapshot.BytesTotal ?? 0);
        Size = FormatSize(TotalBytes);
        BytesTransferred = Math.Clamp(snapshot.BytesDone, 0, TotalBytes > 0 ? TotalBytes : long.MaxValue);
        Progress = TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100
            : snapshot.State == "completed" ? 100 : 0;
        _canRetryOverride = snapshot.Retryable;
        _commitStarted = snapshot.CommitStarted;
        State = snapshot.State switch
        {
            "queued" or "waiting_stable" or "hashing" => TransferState.Queued,
            "transferring" or "verifying" or "staged" or "committing" => TransferState.Running,
            "paused" => TransferState.Paused,
            "conflict" => TransferState.Conflict,
            "failed" => TransferState.Failed,
            "cancelled" => TransferState.Cancelled,
            "completed" => TransferState.Completed,
            _ => TransferState.Queued
        };
        ErrorMessage = snapshot.Message;
        if (previousOutcomeUnknown != CommitOutcomeUnknown)
        {
            OnPropertyChanged(nameof(CommitOutcomeUnknown));
            OnPropertyChanged(nameof(CommitOutcomeNotice));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanCancel));
        }
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(TransferSummary));
        OnPropertyChanged(nameof(CanRetry));
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

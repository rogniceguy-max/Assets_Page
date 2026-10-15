using MuYingDrive.NativeWpf.Infrastructure;

namespace MuYingDrive.NativeWpf.Models;

public enum TransferState
{
    Queued,
    Running,
    Paused,
    Failed,
    Completed
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
    private long _lastRevision = -1;

    public TransferTask(ResumableTransferSnapshot snapshot) => Update(snapshot);

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

    public bool CanPause => State is TransferState.Running or TransferState.Queued;
    public bool CanResume => State == TransferState.Paused;
    public bool CanRetry => _canRetryOverride ?? State == TransferState.Failed;
    public bool CanCancel => State is TransferState.Queued or TransferState.Running or TransferState.Paused or TransferState.Failed;

    public string StateLabel => State switch
    {
        TransferState.Queued => "等待中",
        TransferState.Running => "进行中",
        TransferState.Paused => "已暂停",
        TransferState.Failed => "失败",
        _ => "已完成"
    };

    public string ProgressLabel => $"{FormatSize(BytesTransferred)} / {Size} · {Progress:0}%";
    public string TransferSummary => State == TransferState.Completed
        ? $"{Direction} · {Size}"
        : $"{Direction} · {FormatSize(BytesTransferred)} / {Size}";
    public string StateBrushKey => State == TransferState.Failed ? "Brush.Status.Danger" : "Brush.Text.Secondary";

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
        State = state;
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

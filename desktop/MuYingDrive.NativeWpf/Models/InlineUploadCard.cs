using System.Windows.Media;
using MuYingDrive.NativeWpf.Infrastructure;

namespace MuYingDrive.NativeWpf.Models;

/// <summary>A WPF projection of a real upload operation; the Rust agent remains the state source.</summary>
public sealed class InlineUploadCard : ObservableObject
{
    private TransferAgentTaskSnapshot? _snapshot;
    private ImageSource? _localPreview;
    private string? _queueError;
    private string? _localState;
    private bool _localCanRetry;

    public InlineUploadCard(string operationId, string displayName, string localPath,
        string scopeId, string? projectId, string logicalPath, string? folderGroup = null, bool isFolderBatch = false)
    {
        OperationId = operationId;
        DisplayName = displayName;
        LocalPath = localPath;
        ScopeId = scopeId;
        ProjectId = projectId;
        LogicalPath = logicalPath;
        FolderGroup = folderGroup;
        IsFolderBatch = isFolderBatch;
    }

    public string OperationId { get; }
    public string DisplayName { get; }
    public string LocalPath { get; }
    public string ScopeId { get; }
    public string? ProjectId { get; }
    public string LogicalPath { get; }
    public string? FolderGroup { get; }
    public bool IsFolderBatch { get; }
    public TransferAgentTaskSnapshot? Snapshot => _snapshot;
    public ImageSource? LocalPreview
    {
        get => _localPreview;
        set
        {
            if (!SetProperty(ref _localPreview, value)) return;
            OnPropertyChanged(nameof(HasLocalPreview));
        }
    }
    public string? ErrorMessage => _snapshot?.Message ?? _queueError;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage) || _snapshot?.State is "failed" or "conflict";
    public bool HasLocalPreview => _localPreview is not null;
    public bool IsImage => Path.GetExtension(DisplayName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif";
    public bool IsVideo => Path.GetExtension(DisplayName).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm";
    public string StateLabel => _snapshot is null
        ? _localState ?? (_queueError is null ? "排队中" : "未进入传输队列")
        : TransferTaskStateProjection.Label(_snapshot.State);
    public string ProgressLabel => _snapshot?.BytesTotal is > 0
        ? $"{_snapshot.BytesDone:N0} / {_snapshot.BytesTotal:N0} 字节 · {Progress:0}%"
        : _snapshot?.State == "completed" ? "传输已提交" : "等待传输代理";
    public double Progress => _snapshot is null ? 0 : TransferTaskStateProjection.Progress(
        _snapshot.BytesDone, _snapshot.BytesTotal, _snapshot.State);
    public bool HasProgress => _snapshot?.BytesTotal is > 0;
    public bool CanRetry => _snapshot?.Retryable == true || (_snapshot is null && _localCanRetry);
    public bool CanCancel => _snapshot is not null &&
        TransferTaskStateProjection.CanCancel(_snapshot.State, _snapshot.CommitStarted);

    public void Apply(TransferAgentTaskSnapshot snapshot)
    {
        _snapshot = snapshot;
        _queueError = null;
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCancel));
    }

    public void SetQueueError(string message)
    {
        _queueError = string.IsNullOrWhiteSpace(message) ? "无法将文件加入传输队列。" : message;
        _localCanRetry = true;
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCancel));
    }

    public void SetLocalStatus(string state, string? error = null, bool canRetry = false)
    {
        _localState = state;
        _queueError = error;
        _localCanRetry = canRetry;
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCancel));
    }
}

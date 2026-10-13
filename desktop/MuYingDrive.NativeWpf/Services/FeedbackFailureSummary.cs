using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Allowlist-only prefill. Never copies the task's ID, filename, path or error text.</summary>
public static class FeedbackFailureSummary
{
    public static (string Category, string Title, string Description) From(TransferTask task)
    {
        bool upload = task.Direction == "上传";
        string direction = upload ? "上传" : "下载";
        return (upload ? "upload" : "download", $"{direction}未完成", $"文件{direction}未完成。请补充发生经过、复现步骤和预期结果，并遮盖敏感内容。");
    }
}

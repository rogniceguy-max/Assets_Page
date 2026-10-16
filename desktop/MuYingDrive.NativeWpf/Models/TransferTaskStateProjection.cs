namespace MuYingDrive.NativeWpf.Models;

/// <summary>One shared, truthful label for Rust task states in file cards and sync summaries.</summary>
public static class TransferTaskStateProjection
{
    public static string Label(string? state) => state?.ToLowerInvariant() switch
    {
        "queued" => "排队中",
        "waiting_stable" => "等待文件稳定",
        "hashing" => "校验文件",
        "transferring" => "传输中",
        "verifying" => "校验传输",
        "staged" => "等待提交",
        "committing" => "提交中",
        "linking" => "等待关联",
        "paused" => "已暂停",
        "waiting_network" or "offline" => "等待网络",
        "completed" => "已完成",
        "conflict" => "名称或版本冲突",
        "failed" => "失败",
        "cancelled" or "canceled" => "已取消",
        _ => "等待代理状态"
    };

    public static double Progress(long bytesDone, long? bytesTotal, string? state)
    {
        if (string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase)) return 100;
        if (bytesTotal is not > 0) return 0;
        return Math.Clamp((double)Math.Max(0, bytesDone) / bytesTotal.Value * 100, 0, 100);
    }

    public static bool IsTerminal(string? state) => state?.ToLowerInvariant() is
        "completed" or "failed" or "conflict" or "cancelled" or "canceled";

    public static bool CanCancel(string? state, bool commitStarted) => !commitStarted &&
        state?.ToLowerInvariant() is "queued" or "waiting_stable" or "hashing" or
            "transferring" or "verifying" or "staged" or "paused" or "waiting_network" or "offline";
}

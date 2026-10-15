using System.IO;

namespace MuYingDrive.ProjectWorkbooks;

/// <summary>Host and API must use the same upload limit. No limit may be unbounded.</summary>
public sealed record WorkbookTransportOptions
{
    public long MaxUploadBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxJsonResponseBytes { get; init; } = 25 * 1024 * 1024;
    public int MaxPreviewBytes { get; init; } = 64 * 1024 * 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromSeconds(180);

    internal void Validate()
    {
        if (MaxUploadBytes < 1 || MaxJsonResponseBytes < 1 || MaxPreviewBytes < 1)
            throw new ArgumentException("传输大小限制必须为正数。");
        foreach (var timeout in new[] { RequestTimeout, UploadTimeout })
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentException("传输时限必须为有限正数。");
    }
}

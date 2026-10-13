using System.IO;
using System.Net;
using System.Net.Http;

namespace MuYingDrive.ProjectWorkbooks;

/// <summary>Host and API must use the same upload limit. No limit may be unbounded.</summary>
public sealed record WorkbookTransportOptions
{
    public long MaxUploadBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxJsonResponseBytes { get; init; } = 25 * 1024 * 1024;
    public int MaxPreviewBytes { get; init; } = 64 * 1024 * 1024;
    public long MaxDownloadBytes { get; init; } = 256L * 1024 * 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromSeconds(180);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(180);

    internal void Validate()
    {
        if (MaxUploadBytes < 1 || MaxJsonResponseBytes < 1 || MaxPreviewBytes < 1 || MaxDownloadBytes < 1)
            throw new ArgumentException("传输大小限制必须为正数。");
        foreach (var timeout in new[] { RequestTimeout, UploadTimeout, DownloadTimeout })
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentException("传输时限必须为有限正数。");
    }
}

// A seekable caller stream is sent from its current position without copying or taking ownership.
// Only a non-seekable stream is spooled; the bounded temporary file is removed on disposal.
internal sealed class WorkbookUploadSource(Stream stream, long length, bool ownsStream) : IDisposable
{
    public Stream Stream { get; } = stream;
    public long Length { get; } = length;

    public static async Task<WorkbookUploadSource> PrepareAsync(Stream source, long limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new InvalidDataException("上传流不可读。");
        if (source.CanSeek)
        {
            var remaining = source.Length - source.Position;
            if (remaining < 1 || remaining > limit) throw new InvalidDataException($"文件不能为空，且不得超过 {limit / (1024 * 1024)} MiB。");
            return new WorkbookUploadSource(source, remaining, false);
        }
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "pwb-upload-" + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        try
        {
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), ct)) != 0)
            {
                total = checked(total + count);
                if (total > limit) throw new InvalidDataException("文件超过上传大小限制。");
                await temp.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (total == 0) throw new InvalidDataException("不能上传空文件。");
            temp.Position = 0;
            return new WorkbookUploadSource(temp, total, true);
        }
        catch { await temp.DisposeAsync(); throw; }
    }

    public void Dispose() { if (ownsStream) Stream.Dispose(); }
}

internal sealed class WorkbookStreamContent(Stream source, long length, CancellationToken operationToken) : HttpContent
{
    protected override bool TryComputeLength(out long computedLength) { computedLength = length; return true; }
    protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) =>
        CopyAsync(target, operationToken);
    protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, ct);
        await CopyAsync(target, linked.Token);
    }
    private async Task CopyAsync(Stream target, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (count == 0) throw new EndOfStreamException("上传期间源文件长度发生变化。");
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
            remaining -= count;
        }
    }
    // HttpContent.Dispose does not own or dispose source. The caller/temporary lease owns it.
}

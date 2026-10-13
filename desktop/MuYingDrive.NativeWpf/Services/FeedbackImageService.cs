using System.Windows.Media.Imaging;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

public static class FeedbackImageService
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static async Task<FeedbackPreview> ReadAsync(string path)
    {
        // Path is used transiently; neither original path nor filename enters a record/request.
        return await Task.Run(() =>
        {
            using var file = File.OpenRead(path);
            if (file.Length > MaxBytes) throw new InvalidOperationException("每张截图最多 2 MiB。");
            using var memory = new MemoryStream();
            file.CopyTo(memory);
            var bytes = memory.ToArray();
            var mime = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
                : bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg"
                : throw new InvalidOperationException("仅支持 PNG 和 JPEG 截图。");
            return Decode(bytes, mime);
        });
    }

    public static FeedbackPreview Decode(byte[] bytes, string mime = "image/png")
    {
        using var memory = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (decoder.Frames.Count != 1 || frame.PixelWidth > 4096 || frame.PixelHeight > 4096 || (long)frame.PixelWidth * frame.PixelHeight > 12_000_000)
            throw new InvalidOperationException("截图尺寸过大，请缩小后重试。");
        frame.Freeze();
        return new FeedbackPreview(frame, bytes, mime);
    }

    public static FeedbackPreview FromClipboard(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        if (bitmap.PixelWidth > 4096 || bitmap.PixelHeight > 4096 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 12_000_000)
            throw new InvalidOperationException("截图尺寸过大，请缩小后重试。");
        // Rebuild from pixels so clipboard metadata is not serialized.
        var converted = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var clean = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(clean));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        if (memory.Length > MaxBytes) throw new InvalidOperationException("每张截图最多 2 MiB。");
        return Decode(memory.ToArray());
    }
}

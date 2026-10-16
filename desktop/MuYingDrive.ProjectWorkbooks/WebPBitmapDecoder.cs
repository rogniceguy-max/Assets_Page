using Imazen.WebP;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MuYingDrive.ProjectWorkbooks;

/// <summary>Decodes WebP through the app-bundled libwebp runtime instead of machine-specific WIC codecs.</summary>
public static class WebPBitmapDecoder
{
    private const int MaximumEncodedBytes = 64 * 1024 * 1024;
    private const int MaximumDimension = 8192;
    private const long MaximumImagePixelCount = 40_000_000;
    private const long MaximumThumbnailPixelCount = 8_000_000;
    private static readonly SemaphoreSlim ThumbnailDecodeSlots = new(2, 2);

    public static bool IsWebP(ReadOnlySpan<byte> bytes) => bytes.Length >= 12 &&
        bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8);

    public static BitmapSource Decode(byte[] bytes, int decodePixelWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumEncodedBytes)
            throw new InvalidDataException("WebP 图片超过允许大小。");
        var maximumPixelCount = decodePixelWidth > 0 ? MaximumThumbnailPixelCount : MaximumImagePixelCount;
        if (!WebPInfo.TryGetSize(bytes, out var width, out var height) || width <= 0 || height <= 0 ||
            width > MaximumDimension || height > MaximumDimension || (long)width * height > maximumPixelCount)
            throw new InvalidDataException("WebP 图片尺寸无效或超过允许范围。");

        var limitThumbnailDecode = decodePixelWidth > 0;
        if (limitThumbnailDecode) ThumbnailDecodeSlots.Wait();
        try
        {
            var bgra = WebPDecoder.Decode(bytes, out var decodedWidth, out var decodedHeight, WebPPixelFormat.Bgra);
            if (decodedWidth != width || decodedHeight != height || bgra.Length != checked(width * height * 4))
                throw new InvalidDataException("WebP 解码器返回了不匹配的像素数据。");

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
                null, bgra, checked(width * 4));
            bitmap.Freeze();
            if (!limitThumbnailDecode || width <= decodePixelWidth) return bitmap;

            var scale = (double)decodePixelWidth / width;
            var resized = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            resized.Freeze();
            return resized;
        }
        finally
        {
            if (limitThumbnailDecode) ThumbnailDecodeSlots.Release();
        }
    }
}

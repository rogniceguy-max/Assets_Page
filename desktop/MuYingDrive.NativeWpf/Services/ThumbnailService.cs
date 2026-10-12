using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MuYingDrive.NativeWpf.Services;

public static class ThumbnailService
{
    private const int MaximumCachedThumbnails = 256;
    private static readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource>>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> CacheOrder = new();

    public static async Task<BitmapSource> LoadAsync(string path)
    {
        var candidate = new Lazy<Task<BitmapSource>>(
            () => Task.Run(() => Load(path)), LazyThreadSafetyMode.ExecutionAndPublication);
        var cached = Cache.GetOrAdd(path, candidate);
        if (ReferenceEquals(candidate, cached))
        {
            CacheOrder.Enqueue(path);
            while (Cache.Count > MaximumCachedThumbnails && CacheOrder.TryDequeue(out var oldest))
                Cache.TryRemove(oldest, out _);
        }

        try
        {
            return await cached.Value;
        }
        catch
        {
            if (Cache.TryGetValue(path, out var current) && ReferenceEquals(current, cached))
                Cache.TryRemove(path, out _);
            throw;
        }
    }

    private static BitmapSource Load(string path)
    {
        if (IsVideo(path))
        {
            return LoadShellThumbnail(path);
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 640;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".mp4" or ".mov" or ".wmv" or ".avi" or ".mkv" or ".m4v" or ".webm";

    private static BitmapSource LoadShellThumbnail(string path)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory));
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            // Let Windows' registered video thumbnail provider decode a representative frame.
            Marshal.ThrowExceptionForHR(factory.GetImage(new NativeSize(640, 420), 0, out bitmapHandle));
            var bitmap = Imaging.CreateBitmapSourceFromHBitmap(bitmapHandle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            Marshal.ReleaseComObject(factory);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IShellItemImageFactory imageFactory);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }
}

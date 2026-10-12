using System.Windows.Media.Imaging;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

public sealed class DemoAssetCatalog
{
    public const int DemoItemCount = 2_000;

    private readonly DemoAsset[] _assets;

    public DemoAssetCatalog()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Demo");
        var images = Directory.GetFiles(directory, "sample-*.jpg")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new DemoAsset(path, ReadAspectRatio(path), false));
        var videos = Directory.GetFiles(directory, "sample-*.mp4")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new DemoAsset(path, 16d / 9d, true));
        _assets = images.Concat(videos).ToArray();

        if (_assets.Length == 0)
        {
            throw new FileNotFoundException("没有找到本地演示缩略图。", directory);
        }
    }

    public IReadOnlyList<DemoFileItem> CreateItems(string department, int count = DemoItemCount)
    {
        var items = new DemoFileItem[count];
        for (var index = 0; index < count; index++)
        {
            var asset = _assets[index % _assets.Length];
            var number = index + 1;
            items[index] = new DemoFileItem(
                $"demo-{number:0000}",
                $"演示素材-{number:0000}.{(asset.IsVideo ? "mp4" : "jpg")}",
                department,
                asset.Path,
                asset.AspectRatio,
                $"{(index % 28) + 1}.{(index % 9) + 1} MB",
                asset.IsVideo);
        }

        return items;
    }

    private static double ReadAspectRatio(string path)
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return frame.PixelHeight > 0 ? (double)frame.PixelWidth / frame.PixelHeight : 1d;
        }
        catch (IOException)
        {
            return 1d;
        }
    }

    private sealed record DemoAsset(string Path, double AspectRatio, bool IsVideo);
}

using MuYingDrive.NativeWpf.Infrastructure;

namespace MuYingDrive.NativeWpf.Models;

public sealed class DemoFileItem : ObservableObject
{
    private string _name;

    public DemoFileItem(string id, string name, string department, string thumbnailPath, double aspectRatio, string fileSize, bool isVideo)
    {
        Id = id;
        _name = name;
        Department = department;
        ThumbnailPath = thumbnailPath;
        AspectRatio = aspectRatio;
        FileSize = fileSize;
        IsVideo = isVideo;
    }

    public string Id { get; }
    public string Department { get; }
    public string ThumbnailPath { get; }
    public double AspectRatio { get; }
    public string FileSize { get; }
    public bool IsVideo { get; }
    public string KindLabel => IsVideo ? "视频" : "图片";
    public string ModifiedLabel => "演示素材";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
}

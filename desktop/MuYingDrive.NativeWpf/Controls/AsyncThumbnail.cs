using MuYingDrive.NativeWpf.Services;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Controls;

/// <summary>Loads only realized gallery thumbnails, decodes off the UI thread, and caches frozen bitmaps.</summary>
public sealed class AsyncThumbnail : Image
{
    private static readonly TimeSpan MissingThumbnailRetryDelay = TimeSpan.FromSeconds(50);
    private const int MaximumMissingThumbnailRetries = 5;
    private int _requestVersion;
    private CancellationTokenSource? _loadCancellation;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(AsyncThumbnail),
        new FrameworkPropertyMetadata(string.Empty, OnSourcePathChanged));

    public static readonly DependencyProperty FileThumbnailProviderProperty = DependencyProperty.Register(
        nameof(FileThumbnailProvider), typeof(FileThumbnailProvider), typeof(AsyncThumbnail),
        new FrameworkPropertyMetadata(null, OnFileThumbnailProviderChanged));

    public AsyncThumbnail()
    {
        DataContextChanged += (_, _) => BeginLoad(SourcePath);
        Loaded += (_, _) => BeginLoad(SourcePath);
        Unloaded += (_, _) => CancelCurrentLoad();
    }

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public FileThumbnailProvider? FileThumbnailProvider
    {
        get => (FileThumbnailProvider?)GetValue(FileThumbnailProviderProperty);
        set => SetValue(FileThumbnailProviderProperty, value);
    }

    private static void OnSourcePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is AsyncThumbnail control)
        {
            control.BeginLoad(args.NewValue as string ?? string.Empty);
        }
    }

    private static void OnFileThumbnailProviderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is AsyncThumbnail control)
            control.BeginLoad(control.SourcePath);
    }

    private async void BeginLoad(string path)
    {
        var requestVersion = Interlocked.Increment(ref _requestVersion);
        var loadCancellation = new CancellationTokenSource();
        TryCancel(Interlocked.Exchange(ref _loadCancellation, loadCancellation));
        var cancellationToken = loadCancellation.Token;
        Source = null;
        var item = DataContext as DriveFileItem;
        var provider = FileThumbnailProvider ?? item?.FileThumbnailProvider;
        var hasRemoteThumbnail = provider is not null && item is { FileId.Length: > 0, RevisionId.Length: > 0 };
        if (string.IsNullOrWhiteSpace(path) && !hasRemoteThumbnail)
        {
            Interlocked.CompareExchange(ref _loadCancellation, null, loadCancellation);
            loadCancellation.Dispose();
            return;
        }

        try
        {
            var cachePath = path;
            if (hasRemoteThumbnail)
            {
                string? resolved = null;
                for (var retry = 0; retry <= MaximumMissingThumbnailRetries; retry++)
                {
                    resolved = await provider!.GetThumbnailPathAsync(item!.FileId!, item.RevisionId!, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(resolved)) break;
                    if (retry == MaximumMissingThumbnailRetries || !IsLoaded || requestVersion != _requestVersion)
                        return;

                    // The service briefly caches a 404 while the Mac thumbnail worker catches up.
                    // Retry only while this virtualized item remains visible, and bound the wait
                    // so a permanently unsupported file does not create an endless background loop.
                    await Task.Delay(MissingThumbnailRetryDelay, cancellationToken);
                }
                if (string.IsNullOrWhiteSpace(resolved)) return;
                cachePath = resolved;
                if (requestVersion != _requestVersion) return;
                if (!string.Equals(item.ThumbnailPath, resolved, StringComparison.OrdinalIgnoreCase))
                    item.ThumbnailPath = resolved;
            }
            if (string.IsNullOrWhiteSpace(cachePath)) return;
            BitmapSource bitmap = await ThumbnailService.LoadAsync(cachePath);
            if (requestVersion == _requestVersion)
            {
                Source = bitmap;
                if (DataContext is DriveFileItem boundItem)
                {
                    boundItem.HasThumbnail = true;
                    if (bitmap.PixelHeight > 0)
                    {
                        var ratio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
                        if (Math.Abs(boundItem.AspectRatio - ratio) > 0.02)
                        {
                            boundItem.AspectRatio = ratio;
                            InvalidateGalleryLayout();
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            // An unavailable demo thumbnail leaves the neutral media placeholder visible.
        }
        catch (NotSupportedException)
        {
            // Unsupported image formats use the same placeholder.
        }
        catch (COMException)
        {
            // A video may have no registered Windows thumbnail provider.
        }
        catch (Win32Exception)
        {
            // The Windows thumbnail provider could not decode this demo file.
        }
        catch (UnauthorizedAccessException)
        {
            // The current account cannot read this item; leave the neutral placeholder visible.
        }
        catch (OperationCanceledException)
        {
            // The item was recycled or unloaded while the request was pending.
        }
        catch (Exception)
        {
            // Thumbnail providers and network filesystem metadata can fail independently.
        }
        finally
        {
            Interlocked.CompareExchange(ref _loadCancellation, null, loadCancellation);
            loadCancellation.Dispose();
        }
    }

    private void CancelCurrentLoad()
    {
        Interlocked.Increment(ref _requestVersion);
        TryCancel(Interlocked.Exchange(ref _loadCancellation, null));
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void InvalidateGalleryLayout()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is JustifiedGalleryPanel panel)
            {
                panel.InvalidateLayout();
                return;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }
}

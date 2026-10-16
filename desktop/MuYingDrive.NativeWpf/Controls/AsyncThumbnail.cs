using MuYingDrive.NativeWpf.Services;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using MuYingDrive.NativeWpf.Models;
using System.Collections.Concurrent;

namespace MuYingDrive.NativeWpf.Controls;

/// <summary>Loads only realized gallery thumbnails, decodes off the UI thread, and caches frozen bitmaps.</summary>
public sealed class AsyncThumbnail : Image
{
    private static readonly TimeSpan MissingThumbnailRetryDelay = TimeSpan.FromSeconds(10);
    private const int MaximumMissingThumbnailRetries = 5;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastFailureLogged = new(StringComparer.Ordinal);
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
                if (!string.Equals(item!.ThumbnailPath, resolved, StringComparison.OrdinalIgnoreCase))
                    item.ThumbnailPath = resolved;
            }
            if (string.IsNullOrWhiteSpace(cachePath)) return;
            BitmapSource bitmap = await LoadWithCacheRecoveryAsync(cachePath,
                hasRemoteThumbnail ? provider : null, hasRemoteThumbnail ? item : null, cancellationToken);
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
        catch (IOException exception)
        {
            // An unavailable demo thumbnail leaves the neutral media placeholder visible.
            RecordFailure(item, exception);
        }
        catch (NotSupportedException exception)
        {
            // Unsupported image formats use the same placeholder.
            RecordFailure(item, exception);
        }
        catch (COMException exception)
        {
            // A video may have no registered Windows thumbnail provider.
            RecordFailure(item, exception);
        }
        catch (Win32Exception exception)
        {
            // The Windows thumbnail provider could not decode this demo file.
            RecordFailure(item, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            // The current account cannot read this item; leave the neutral placeholder visible.
            RecordFailure(item, exception);
        }
        catch (OperationCanceledException)
        {
            // The item was recycled or unloaded while the request was pending.
        }
        catch (Exception exception)
        {
            RecordFailure(item, exception);
        }
        finally
        {
            Interlocked.CompareExchange(ref _loadCancellation, null, loadCancellation);
            loadCancellation.Dispose();
        }
    }

    private static async Task<BitmapSource> LoadWithCacheRecoveryAsync(string path,
        FileThumbnailProvider? provider, DriveFileItem? item, CancellationToken cancellationToken)
    {
        var isRemoteWebp = provider is not null && item is { FileId.Length: > 0, RevisionId.Length: > 0 } &&
            string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase);
        var stamp = default(FileThumbnailProvider.CacheFileStamp);
        var hasStamp = isRemoteWebp && provider!.TryCaptureCacheFileStamp(
            item!.FileId!, item.RevisionId!, path, out stamp);

        try
        {
            var bitmap = await ThumbnailService.LoadAsync(path);
            if (hasStamp)
                provider!.MarkCacheDecodedSuccessfully(item!.FileId!, item.RevisionId!, path, stamp);
            return bitmap;
        }
        catch (Exception exception) when (IsWebpDecodeFailure(isRemoteWebp, exception))
        {
            if (hasStamp)
                provider!.InvalidateAfterDecodeFailure(item!.FileId!, item.RevisionId!, path, stamp);

            // Refetch a server thumbnail once after the on-disk RIFF/WEBP header passed but
            // the real decoder rejected the image. The provider coalesces concurrent requests;
            // its per-revision cooldown prevents repeated failures from starting a download loop.
            var refreshedPath = await provider!.GetThumbnailPathAsync(
                item!.FileId!, item.RevisionId!, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refreshedPath)) throw;

            if (!string.Equals(item.ThumbnailPath, refreshedPath, StringComparison.OrdinalIgnoreCase))
                item.ThumbnailPath = refreshedPath;

            var refreshedStamp = default(FileThumbnailProvider.CacheFileStamp);
            var hasRefreshedStamp = provider.TryCaptureCacheFileStamp(
                item.FileId!, item.RevisionId!, refreshedPath, out refreshedStamp);
            try
            {
                var bitmap = await ThumbnailService.LoadAsync(refreshedPath);
                if (hasRefreshedStamp)
                    provider.MarkCacheDecodedSuccessfully(item.FileId!, item.RevisionId!, refreshedPath, refreshedStamp);
                return bitmap;
            }
            catch (Exception retryException) when (IsWebpDecodeFailure(isRemoteWebp, retryException))
            {
                if (hasRefreshedStamp)
                    provider.InvalidateAfterDecodeFailure(
                        item.FileId!, item.RevisionId!, refreshedPath, refreshedStamp);
                throw;
            }
        }
    }

    private static bool IsWebpDecodeFailure(bool isRemoteWebp, Exception exception) =>
        isRemoteWebp &&
        (exception is InvalidDataException or NotSupportedException or ArgumentException or ExternalException);

    private static void RecordFailure(DriveFileItem? item, Exception exception)
    {
        var key = string.IsNullOrWhiteSpace(item?.FileId)
            ? exception.GetType().FullName ?? "unknown"
            : $"{item.FileId}\0{item.RevisionId}";
        var now = DateTimeOffset.UtcNow;
        if (LastFailureLogged.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromMinutes(1))
            return;
        LastFailureLogged[key] = now;
        App.Record(exception, "AsyncThumbnail.Load");
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

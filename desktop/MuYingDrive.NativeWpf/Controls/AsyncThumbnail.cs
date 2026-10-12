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
    private int _requestVersion;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(AsyncThumbnail),
        new FrameworkPropertyMetadata(string.Empty, OnSourcePathChanged));

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    private static void OnSourcePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is AsyncThumbnail control)
        {
            control.BeginLoad(args.NewValue as string ?? string.Empty);
        }
    }

    private async void BeginLoad(string path)
    {
        var requestVersion = Interlocked.Increment(ref _requestVersion);
        Source = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            BitmapSource bitmap = await ThumbnailService.LoadAsync(path);
            if (requestVersion == _requestVersion)
            {
                Source = bitmap;
                if (DataContext is DriveFileItem item && bitmap.PixelHeight > 0)
                {
                    var ratio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
                    if (Math.Abs(item.AspectRatio - ratio) > 0.02)
                    {
                        item.AspectRatio = ratio;
                        InvalidateGalleryLayout();
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
        catch (Exception)
        {
            // Thumbnail providers and network filesystem metadata can fail independently.
        }
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

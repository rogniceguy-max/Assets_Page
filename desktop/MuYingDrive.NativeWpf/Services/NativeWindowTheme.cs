using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace MuYingDrive.NativeWpf.Services;

internal static class NativeWindowTheme
{
    private const uint DwmwaUseImmersiveDarkMode = 20;
    private const uint DwmwaUseImmersiveDarkModeLegacy = 19;
    private const uint DwmwaBorderColor = 34;
    private const uint DwmwaCaptionColor = 35;
    private const uint DwmwaTextColor = 36;

    public static void Apply(Window window, bool isDark)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var captionColor = ToColorRef(ReadThemeColor(
            "Brush.Surface.Canvas",
            isDark ? Color.FromRgb(17, 17, 19) : Color.FromRgb(252, 252, 253)));
        var textColor = ToColorRef(ReadThemeColor(
            "Brush.Text.Primary",
            isDark ? Color.FromRgb(237, 238, 240) : Color.FromRgb(28, 32, 36)));
        var borderColor = ToColorRef(ReadThemeColor(
            "Brush.Border.Subtle",
            isDark ? Color.FromRgb(39, 42, 45) : Color.FromRgb(232, 232, 236)));
        var darkMode = isDark ? 1 : 0;

        try
        {
            if (SetAttribute(handle, DwmwaUseImmersiveDarkMode, darkMode) < 0)
            {
                SetAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, darkMode);
            }

            SetAttribute(handle, DwmwaCaptionColor, captionColor);
            SetAttribute(handle, DwmwaTextColor, textColor);
            SetAttribute(handle, DwmwaBorderColor, borderColor);
        }
        catch (DllNotFoundException)
        {
            // Keep the standard Windows title bar on systems without DWM.
        }
        catch (EntryPointNotFoundException)
        {
            // Keep the standard Windows title bar when these attributes are unavailable.
        }
    }

    private static Color ReadThemeColor(string resourceKey, Color fallback) =>
        Application.Current?.TryFindResource(resourceKey) is SolidColorBrush brush
            ? brush.Color
            : fallback;

    private static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    private static int SetAttribute(IntPtr handle, uint attribute, int value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        uint dwAttribute,
        ref int pvAttribute,
        uint cbAttribute);
}

using Microsoft.Win32;
using System.Windows;

namespace MuYingDrive.NativeWpf.Services;

public sealed class ThemeService
{
    private readonly string _preferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuYingDrive.NativeWpf",
        "theme.txt");

    public ThemeService()
    {
        Preference = ReadPreference();
        Apply(Preference);
    }

    public string Preference { get; private set; }

    public bool IsDark { get; private set; }

    public event Action<bool>? ThemeChanged;

    public void SetPreference(string preference)
    {
        Preference = preference is "浅色" or "深色" ? preference : "跟随系统";
        Directory.CreateDirectory(Path.GetDirectoryName(_preferencePath)!);
        File.WriteAllText(_preferencePath, Preference);
        Apply(Preference);
    }

    private void Apply(string preference)
    {
        var isDark = preference switch
        {
            "深色" => true,
            "浅色" => false,
            _ => ReadSystemDarkMode()
        };
        IsDark = isDark;

        if (Application.Current is null || Application.Current.Resources.MergedDictionaries.Count == 0)
        {
            return;
        }

        var fileName = isDark ? "Dark.xaml" : "Light.xaml";
        var uri = new Uri($"pack://application:,,,/MuYingDrive.NativeWpf;component/Themes/{fileName}", UriKind.Absolute);
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
        ThemeChanged?.Invoke(isDark);
    }

    private string ReadPreference()
    {
        try
        {
            var value = File.ReadAllText(_preferencePath).Trim();
            return value is "浅色" or "深色" ? value : "跟随系统";
        }
        catch (IOException)
        {
            return "跟随系统";
        }
    }

    private static bool ReadSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MuYingDrive.Updater.ViewModels;

namespace MuYingDrive.Updater.Views;

public partial class UpdaterWindow : Window
{
    private readonly UpdaterViewModel _viewModel;

    public UpdaterWindow()
    {
        InitializeComponent();
        _viewModel = new UpdaterViewModel(Close);
        DataContext = _viewModel;
        ApplySystemTheme();
    }

    public Task RunAsync(string[] arguments) => _viewModel.RunAsync(arguments);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        e.Cancel = true;
        if (_viewModel.CanCancel) _viewModel.CancelUpdate();
    }

    private void ApplySystemTheme()
    {
        var dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException) { }

        var name = dark ? "Dark.xaml" : "Light.xaml";
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/MuYingDrive.Updater;component/Themes/{name}", UriKind.Absolute)
        };
    }
}

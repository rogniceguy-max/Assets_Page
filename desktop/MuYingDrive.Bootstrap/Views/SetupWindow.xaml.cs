using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using MuYingDrive.Bootstrap.ViewModels;

namespace MuYingDrive.Bootstrap.Views;

public partial class SetupWindow : Window
{
    private readonly SetupViewModel _viewModel;
    private bool _completed;
    private bool _closeRequested;

    public SetupViewModel ViewModel => _viewModel;

    public SetupWindow(string[] arguments)
    {
        InitializeComponent();
        _viewModel = new SetupViewModel(Close);
        DataContext = _viewModel;
        ApplySystemTheme();

        // Unattended deployment: start out of the way and close once the work is done.
        if (arguments.Contains("--quiet")) WindowState = WindowState.Minimized;
    }

    public async Task RunAsync(string[] arguments)
    {
        await _viewModel.RunAsync(arguments);
        _completed = true;
        // Unattended mode never waits for a click and never launches the client, so it
        // closes on completion whether the install succeeded or failed.
        if (_closeRequested || arguments.Contains("--quiet")) Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_completed || !_viewModel.IsBusy) return;

        // Closing mid-install could leave the install directory in a half-swapped state, so
        // the close is deferred rather than refused: ask the install to stop, and close as
        // soon as it has unwound. Refusing silently — which is what this used to do — makes
        // the X look broken, and left Task Manager as the only exit from a stalled install.
        _closeRequested = true;
        e.Cancel = true;
        _viewModel.CancelCommand.Execute(null);
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
            Source = new Uri($"pack://application:,,,/MuYingDrive.Setup;component/Themes/{name}", UriKind.Absolute)
        };
    }
}

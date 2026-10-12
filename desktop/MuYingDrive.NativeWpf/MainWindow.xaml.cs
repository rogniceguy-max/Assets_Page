using System.Windows;
using System.ComponentModel;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.NativeWpf.ViewModels;

namespace MuYingDrive.NativeWpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isWindowHandleReady;
    private bool _allowClose;
    private bool _isPreparingClose;

    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = 760;
        MinHeight = 500;
        Width = Math.Min(1280, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(820, Math.Max(MinHeight, workArea.Height - 32));

        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.ThemeService.ThemeChanged += OnThemeChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await _viewModel.InitializeAsync();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        if (_isPreparingClose)
            return;
        _isPreparingClose = true;
        await _viewModel.ShutdownAsync();
        _allowClose = true;
        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _isWindowHandleReady = true;
        NativeWindowTheme.Apply(this, _viewModel.ThemeService.IsDark);
    }

    private void OnThemeChanged(bool isDark)
    {
        if (_isWindowHandleReady)
        {
            NativeWindowTheme.Apply(this, isDark);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.ThemeService.ThemeChanged -= OnThemeChanged;
    }
}

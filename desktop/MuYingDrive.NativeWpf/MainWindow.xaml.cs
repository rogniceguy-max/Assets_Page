using System.Windows;
using System.Windows.Threading;
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
    private readonly TaskCompletionSource _healthWindowLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        _viewModel.CloseRequested += OnCloseRequested;
        _viewModel.ThemeService.ThemeChanged += OnThemeChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _healthWindowLoaded.TrySetResult();
            if (App.IsUpdateHealthCheck) return;
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.Record(ex, "MainWindow.OnLoaded");
            MessageBox.Show($"初始化主窗口遇到问题：{ex.Message}", "初始化失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        if (_isPreparingClose)
            return;
        _isPreparingClose = true;
        try
        {
            try
            {
                await _viewModel.PrepareForShutdownAsync();
            }
            catch
            {
            }

            try
            {
                await _viewModel.ShutdownAsync();
            }
            catch
            {
            }

            if (_viewModel.HasPreparedUpdateHandoff)
            {
                try
                {
                    _viewModel.LaunchPreparedUpdater();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    internal async Task RunUpdateHealthCheckAsync(string[] arguments)
    {
        await _healthWindowLoaded.Task;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!IsInitialized || !IsLoaded || DataContext is not MainViewModel || _viewModel.CurrentPage is not LoginViewModel)
            throw new InvalidOperationException("The main client window did not load correctly.");
        _viewModel.EnsureClientConfigurationValid();
        await _viewModel.ShutdownAsync();
        Services.UpdateHealthReporter.ReportIfRequested(arguments, Services.UpdateCoordinator.GetCurrentVersion());
        _allowClose = true;
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
        _viewModel.CloseRequested -= OnCloseRequested;
    }
}

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
        _healthWindowLoaded.TrySetResult();
        if (App.IsUpdateHealthCheck) return;
        await _viewModel.InitializeAsync();
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
            if (!await _viewModel.PrepareForShutdownAsync())
            {
                MessageBox.Show(_viewModel.UpdateCloseErrorMessage, "暂时无法退出更新", MessageBoxButton.OK, MessageBoxImage.Information);
                _isPreparingClose = false;
                return;
            }

            if (!string.IsNullOrWhiteSpace(_viewModel.UpdateCloseErrorMessage))
                MessageBox.Show(_viewModel.UpdateCloseErrorMessage, "更新已取消", MessageBoxButton.OK, MessageBoxImage.Warning);

            await _viewModel.ShutdownAsync();
            if (_viewModel.HasPreparedUpdateHandoff && !_viewModel.LaunchPreparedUpdater())
                MessageBox.Show("更新程序无法启动。当前版本仍保留，下次启动沐影盘后可以重试更新。", "更新未启动", MessageBoxButton.OK, MessageBoxImage.Warning);
            _allowClose = true;
            Close();
        }
        catch (Exception)
        {
            MessageBox.Show("关闭时无法安全保存所有传输进度。沐影盘仍保持打开，请稍后重试。", "暂时无法退出", MessageBoxButton.OK, MessageBoxImage.Warning);
            _isPreparingClose = false;
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

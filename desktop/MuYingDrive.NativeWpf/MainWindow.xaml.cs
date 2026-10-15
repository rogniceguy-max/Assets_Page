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
        var shouldClose = false;
        try
        {
            bool canClose;
            try
            {
                canClose = await _viewModel.PrepareForShutdownAsync();
            }
            catch
            {
                canClose = false;
            }

            if (!canClose)
            {
                if (!string.IsNullOrWhiteSpace(_viewModel.UpdateCloseErrorMessage))
                    MessageBox.Show(_viewModel.UpdateCloseErrorMessage, "暂时无法关闭", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                await _viewModel.ShutdownAsync();
            }
            catch (Exception exception) when (_viewModel.HasPreparedUpdateHandoff)
            {
                try { await _viewModel.AbortPreparedUpdateCloseAsync(); }
                catch { }
                MessageBox.Show(
                    $"更新已取消，沐影盘仍保持打开。{Environment.NewLine}{exception.Message}",
                    "无法安全关闭传输代理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            catch
            {
                // A regular close may proceed if a non-update cleanup step failed. For an
                // update handoff, the filtered catch above keeps the window open.
            }

            if (!string.IsNullOrWhiteSpace(_viewModel.AgentShutdownWarning))
                MessageBox.Show(
                    $"沐影盘即将关闭，但传输代理未能确认完全停止。{Environment.NewLine}{_viewModel.AgentShutdownWarning}",
                    "传输代理未完全关闭", MessageBoxButton.OK, MessageBoxImage.Warning);

            if (_viewModel.HasPreparedUpdateHandoff)
                _viewModel.LaunchPreparedUpdater();
            shouldClose = true;
        }
        catch
        {
        }
        finally
        {
            if (shouldClose)
            {
                _allowClose = true;
                Close();
            }
            else
            {
                _isPreparingClose = false;
            }
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

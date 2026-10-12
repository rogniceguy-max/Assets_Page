using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MuYingDrive.Wpf;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _backendProcess;
    private Uri? _backendUri;
    private string _lastBackendError = string.Empty;
    private bool _isClosing;
    private bool _backendReady;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "正在启动本地文件服务…";
            _backendUri = await StartBackendAsync(_lifetime.Token);

            string userDataFolder = Path.Combine(GetAppDataRoot(), "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);

            await WebView.EnsureCoreWebView2Async(environment);
            ConfigureWebView();
            WebView.Source = _backendUri;
            WebView.Visibility = Visibility.Visible;
            StartupPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            StopBackend();
            StartupProgress.Visibility = Visibility.Collapsed;
            StatusText.Text = DescribeStartupError(error);
            CloseButton.Visibility = Visibility.Visible;
        }
    }

    private async Task<Uri> StartBackendAsync(CancellationToken cancellationToken)
    {
        string appDataRoot = GetAppDataRoot();
        string backendRoot = Path.Combine(AppContext.BaseDirectory, "backend");
        string serverScript = Path.Combine(backendRoot, "server.py");
        string frontendRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        if (!File.Exists(serverScript))
        {
            throw new FileNotFoundException("应用包中未找到本地服务文件。", serverScript);
        }
        if (!File.Exists(Path.Combine(frontendRoot, "index.html")))
        {
            throw new FileNotFoundException("应用包中未找到前端文件。", frontendRoot);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("MUYING_PYTHON") ?? "python",
            WorkingDirectory = backendRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(serverScript);
        startInfo.Environment["MUYING_PORT"] = "0";
        startInfo.Environment["MUYING_DATA_DIR"] = Path.Combine(appDataRoot, "Data");
        startInfo.Environment["MUYING_FRONTEND_DIR"] = frontendRoot;
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _backendProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _backendProcess.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            const string prefix = "READY_URL=";
            if (args.Data.StartsWith(prefix, StringComparison.Ordinal) &&
                Uri.TryCreate(args.Data[prefix.Length..], UriKind.Absolute, out var uri) &&
                IsLoopbackUri(uri))
            {
                ready.TrySetResult(uri);
            }
        };
        _backendProcess.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) _lastBackendError = args.Data;
        };
        _backendProcess.Exited += (_, _) =>
        {
            int exitCode;
            try { exitCode = _backendProcess?.ExitCode ?? -1; }
            catch (InvalidOperationException) { exitCode = -1; }
            exited.TrySetResult(exitCode);

            if (_backendReady && !_isClosing)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_backendReady && !_isClosing) ShowBackendStopped(exitCode);
                }));
            }
        };

        if (!_backendProcess.Start())
        {
            throw new InvalidOperationException("无法启动本地文件服务进程。");
        }
        _backendProcess.BeginOutputReadLine();
        _backendProcess.BeginErrorReadLine();

        Task timeout = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        Task completed = await Task.WhenAny(ready.Task, exited.Task, timeout);
        if (completed == ready.Task)
        {
            Uri uri = await ready.Task;
            if (_backendProcess.HasExited)
            {
                throw new InvalidOperationException("本地文件服务启动后立即退出。" + _lastBackendError);
            }
            _backendReady = true;
            if (_backendProcess.HasExited)
            {
                _backendReady = false;
                throw new InvalidOperationException("本地文件服务启动后立即退出。" + _lastBackendError);
            }
            return uri;
        }
        if (completed == exited.Task)
        {
            throw new InvalidOperationException(
                $"本地文件服务提前退出（代码 {await exited.Task}）。{_lastBackendError}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            string.IsNullOrWhiteSpace(_lastBackendError)
                ? "本地文件服务启动超时。请确认本机 Python 运行时可用。"
                : $"本地文件服务启动超时。{_lastBackendError}");
    }

    private void ConfigureWebView()
    {
        if (WebView.CoreWebView2 is null || _backendUri is null) return;

        WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        WebView.CoreWebView2.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !IsSameOrigin(uri, _backendUri))
            {
                args.Cancel = true;
            }
        };
        WebView.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
        };
    }

    private static bool IsLoopbackUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp &&
        uri.Host == "127.0.0.1" &&
        uri.AbsolutePath == "/";

    private static bool IsSameOrigin(Uri candidate, Uri? origin) =>
        origin is not null &&
        candidate.Scheme == origin.Scheme &&
        string.Equals(candidate.Host, origin.Host, StringComparison.OrdinalIgnoreCase) &&
        candidate.Port == origin.Port;

    private static string GetAppDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuYingDrive");

    private static string DescribeStartupError(Exception error)
    {
        if (error is FileNotFoundException)
        {
            return $"无法加载本地应用文件。{error.Message}";
        }
        if (error.GetType().Name.Contains("WebView2RuntimeNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return "此电脑未安装 Microsoft Edge WebView2 Runtime。安装 Evergreen Runtime 后再启动。";
        }
        if (error is System.ComponentModel.Win32Exception)
        {
            return "未找到 Python 运行时。请安装 Python 3.11 或更高版本，或通过 MUYING_PYTHON 指定 python.exe 路径。";
        }
        return $"本地应用启动失败：{error.Message}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowBackendStopped(int exitCode)
    {
        if (_isClosing) return;

        WebView.Visibility = Visibility.Collapsed;
        StartupPanel.Visibility = Visibility.Visible;
        StartupProgress.Visibility = Visibility.Collapsed;
        StatusText.Text = string.IsNullOrWhiteSpace(_lastBackendError)
            ? $"本地文件服务已停止（退出代码 {exitCode}）。请关闭应用后重新启动。"
            : $"本地文件服务已停止（退出代码 {exitCode}）。{_lastBackendError}";
        CloseButton.Visibility = Visibility.Visible;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        _lifetime.Cancel();
        StopBackend();
    }

    private void StopBackend()
    {
        _backendReady = false;
        Process? process = _backendProcess;
        _backendProcess = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}

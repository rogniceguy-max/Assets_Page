using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.UpdateCore;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly V2SessionService? _sessionService;
    private readonly V2ApiClient? _api;
    private NasClientSettings? _settings;
    private object _currentPage;
    private readonly string? _configurationError;
    private Task? _initializeTask;
    private UpdateCoordinator? _updateCoordinator;
    private bool _installUpdateOnExit;
    private bool _shutdownComplete;
    private string? _preparedHandoffPath;
    private string? _preparedUpdaterPath;
    private string _updateCloseErrorMessage = string.Empty;

    public MainViewModel()
    {
        ThemeService = new ThemeService();
        InstallUpdateNowCommand = new AsyncRelayCommand(async _ => await InstallUpdateNowAsync(), _ => IsUpdateReady);
        var login = new LoginViewModel(this);
        _currentPage = login;
        try
        {
            var settings = NasClientSettings.Load();
            _settings = settings;
            var apiOptions = new V2ApiClientOptions(settings.ApiBaseUri, settings.CertificateSha256);
            var api = new V2ApiClient(apiOptions);
            _api = api;
            var profileSuffix = settings.CredentialProfile == "default" ? string.Empty : "." + settings.CredentialProfile;
            var vault = new WindowsCredentialVault(new WindowsCredentialVaultOptions(
                "MuYingDrive.V2" + profileSuffix + ".ActiveSession",
                "MuYingDrive.V2" + profileSuffix + ".ActiveSmbCredential"));
            var smb = new WindowsSmbConnectionService(new WindowsSmbConnectionOptions(settings.ShareRoot), vault);
            _sessionService = new V2SessionService(api, vault, smb);
            if (!App.IsUpdateHealthCheck)
            {
                _ = UpdateFailureReporter.SendPendingAsync(settings.ApiBaseUri, settings.CertificateSha256);
                _updateCoordinator = new UpdateCoordinator(apiOptions, UpdateCoordinator.GetCurrentVersion());
                _updateCoordinator.Changed += HandleUpdateChanged;
                _updateCoordinator.Start();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _configurationError = exception.Message;
            login.ErrorMessage = _configurationError;
        }
    }

    public ThemeService ThemeService { get; }

    /// <summary>
    /// The authenticated V2 client, or null before the client configuration loads.
    /// </summary>
    /// <remarks>
    /// Exposed so a page can build its own HTTP client against the same certificate pin,
    /// rather than constructing a second <see cref="V2ApiClient"/> from settings and
    /// duplicating the pinning rule.
    /// </remarks>
    public V2ApiClient? ApiClient => _api;

    public AsyncRelayCommand InstallUpdateNowCommand { get; }
    public bool IsUpdateReady => _updateCoordinator?.ReadyUpdate is not null;
    public bool HasPreparedUpdateHandoff => _preparedHandoffPath is not null && _preparedUpdaterPath is not null;
    public void EnsureClientConfigurationValid()
    {
        if (_sessionService is null || _configurationError is not null)
            throw new InvalidOperationException("The client service configuration is unavailable.");
    }
    public string UpdateReadyVersion => _updateCoordinator?.ReadyUpdate?.Manifest.Value.Version ?? string.Empty;
    public string UpdateCloseErrorMessage
    {
        get => _updateCloseErrorMessage;
        private set => SetProperty(ref _updateCloseErrorMessage, value);
    }
    public event EventHandler? CloseRequested;

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public void ShowLogin() => CurrentPage = new LoginViewModel(this);

    public void ShowRegister() => CurrentPage = new RegisterViewModel(this);

    /// <summary>Enters the login-free tour. No credentials are involved.</summary>
    public async Task EnterVisitorAsync()
    {
        if (_api is null)
        {
            // A configuration problem belongs on the login page, not in a silent no-op.
            if (CurrentPage is LoginViewModel unavailable)
                unavailable.ErrorMessage = _configurationError ?? "客户端尚未配置 NAS 服务，请联系管理员。";
            return;
        }

        // The tour uses the same shell as a signed-in employee, so it is presented as a
        // workspace with one navigation entry and three read-only spaces. The spaces are
        // only known once the server session exists, hence the ordering.
        var browser = new VisitorViewModel(this, _api);
        await browser.InitializeAsync().ConfigureAwait(true);

        // The tour may post feedback anonymously; the server reports CanManage false
        // for a visitor session, so the maintenance controls never appear.
        var feedback = new FeedbackViewModel(_api, browser.SessionToken ?? string.Empty);
        var workspace = WorkspaceViewModel.ForVisitor(
            this,
            browser,
            browser.SidebarSpaces,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["文件"] = browser,
                ["反馈"] = feedback
            },
            key => browser.SelectSpaceByKeyAsync(key),
            () => ExitVisitorAsync(browser),
            feedback,
            disposeExtra: () => browser.DisposeAsync());
        CurrentPage = workspace;
    }

    /// <summary>Leaves the tour, revoking the visitor session and clearing its cache.</summary>
    public async Task ExitVisitorAsync(VisitorViewModel visitor)
    {
        await visitor.EndAsync();
        ShowLogin();
    }

    public Task InitializeAsync() => _initializeTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (_sessionService is null)
            return;

        if (CurrentPage is LoginViewModel login)
            login.IsBusy = true;
        try
        {
            var session = await _sessionService.RestoreAsync();
            if (session is not null)
                await EnterWorkspaceAsync(session);
        }
        catch (V2ApiException exception)
        {
            if (CurrentPage is LoginViewModel currentLogin)
                currentLogin.ErrorMessage = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (CurrentPage is LoginViewModel currentLogin)
                currentLogin.ErrorMessage = "无法恢复登录状态，请重新登录或检查 NAS 连接。";
        }
        finally
        {
            if (CurrentPage is LoginViewModel currentLogin)
                currentLogin.IsBusy = false;
        }
    }

    public void Dispose()
    {
        if (_shutdownComplete) return;
        _shutdownComplete = true;
        _sessionService?.Dispose();
    }

    public async Task SignOutAsync(DriveSession session)
    {
        if (_sessionService is null)
            return;
        try
        {
            var workspace = CurrentPage as WorkspaceViewModel;
            if (workspace?.Transfers is { } transfers)
                await transfers.PauseAllAsync();
            await _sessionService.SignOutAsync(session);
            if (workspace is not null)
                await workspace.DisposeAsync();
            ShowLogin();
        }
        catch (V2ApiException exception)
        {
            MessageBox.Show(exception.Message, "无法退出登录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task SignInAsync(string userName, string password)
    {
        if (_sessionService is null)
            throw new V2ApiException(_configurationError ?? "客户端尚未配置 NAS 服务，请联系管理员。");
        var session = await _sessionService.LoginAsync(userName, password);
        await EnterWorkspaceAsync(session);
    }

    public async Task RegisterAsync(string userName, string password, string department)
    {
        if (_sessionService is null)
            throw new V2ApiException(_configurationError ?? "客户端尚未配置 NAS 服务，请联系管理员。");
        var groupType = department == "影视资源库" ? "film" : department == "文学创作库" ? "writing" : string.Empty;
        var session = await _sessionService.RegisterAsync(userName, password, groupType);
        await EnterWorkspaceAsync(session);
    }

    public async Task ShutdownAsync()
    {
        if (_shutdownComplete) return;
        if (_initializeTask is not null)
        {
            try { await _initializeTask; }
            catch { /* Startup errors are already surfaced on the login view. */ }
        }
        if (CurrentPage is WorkspaceViewModel workspace)
            await workspace.DisposeAsync();
        if (_updateCoordinator is not null)
        {
            _updateCoordinator.Changed -= HandleUpdateChanged;
            await _updateCoordinator.DisposeAsync();
            _updateCoordinator = null;
        }
        _sessionService?.Dispose();
        _shutdownComplete = true;
    }

    public async Task<bool> PrepareForShutdownAsync()
    {
        UpdateCloseErrorMessage = string.Empty;
        if (!_installUpdateOnExit || !IsUpdateReady)
            return true;

        if (CurrentPage is WorkspaceViewModel { Transfers: { } transfers } && !await transfers.PrepareForUpdateAsync())
        {
            UpdateCloseErrorMessage = "有传输任务未能安全保存检查点。请稍后重试；沐影盘仍保持打开。";
            return false;
        }

        try
        {
            await PrepareHandoffAsync();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Security.Cryptography.CryptographicException or JsonException)
        {
            _installUpdateOnExit = false;
            _preparedHandoffPath = null;
            _preparedUpdaterPath = null;
            UpdateCloseErrorMessage = exception switch
            {
                UnauthorizedAccessException => "当前用户无权更新此安装目录。本次更新已取消，沐影盘将按常规方式关闭。",
                System.Security.Cryptography.CryptographicException => "更新签名配置或更新包验证失败。本次更新已取消，沐影盘将按常规方式关闭。",
                _ => "无法安全准备独立更新程序。本次更新已取消，沐影盘将按常规方式关闭。"
            };
            return true;
        }
    }

    public bool LaunchPreparedUpdater()
    {
        if (_preparedHandoffPath is null || _preparedUpdaterPath is null)
            return false;
        try
        {
            var start = new ProcessStartInfo(_preparedUpdaterPath) { UseShellExecute = false };
            start.ArgumentList.Add("--request");
            start.ArgumentList.Add(_preparedHandoffPath);
            return Process.Start(start) is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task InstallUpdateNowAsync()
    {
        if (!IsUpdateReady) return;
        // A visitor workspace carries no transfers, so there is nothing to prepare.
        if (CurrentPage is WorkspaceViewModel { Transfers: { } transfers } && !await transfers.PrepareForUpdateAsync())
        {
            MessageBox.Show("有传输任务未能安全保存检查点。请稍后重试。", "暂时无法更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _installUpdateOnExit = true;
        InstallUpdateNowCommand.RaiseCanExecuteChanged();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task PrepareHandoffAsync()
    {
        var prepared = _updateCoordinator?.ReadyUpdate ?? throw new InvalidOperationException("The update is no longer ready.");
        var manifestInfo = new FileInfo(prepared.ManifestPath);
        if (!manifestInfo.Exists || manifestInfo.Length is <= 0 or > 64 * 1024)
            throw new InvalidDataException("The cached signed manifest has an invalid size.");
        var manifestBytes = await File.ReadAllBytesAsync(prepared.ManifestPath);
        var verified = UpdateManifestVerifier.Verify(manifestBytes);
        if (!manifestBytes.AsSpan().SequenceEqual(prepared.Manifest.EnvelopeBytes))
            throw new System.Security.Cryptography.CryptographicException("The cached signed manifest changed.");
        await PackageIntegrity.VerifySha256Async(prepared.PackagePath, verified.Value);

        var updateId = Guid.NewGuid().ToString("N");
        var cacheDirectory = Path.GetDirectoryName(prepared.PackagePath)!;
        var updaterSource = Path.Combine(AppContext.BaseDirectory, "Updater", "MuYingDrive.Updater.exe");
        var publisherThumbprint = UpdateTrustAnchor.LoadCodeSigningThumbprint();
        PackageIntegrity.VerifyAuthenticodePublisher(updaterSource, publisherThumbprint);

        var runnerDirectory = Path.Combine(UpdatePathPolicy.UpdateRoot, "runner", updateId);
        Directory.CreateDirectory(runnerDirectory);
        if ((File.GetAttributes(runnerDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("更新程序缓存不能位于重解析路径中。");
        _preparedUpdaterPath = Path.Combine(runnerDirectory, "MuYingDrive.Updater.exe");
        File.Copy(updaterSource, _preparedUpdaterPath, overwrite: false);
        PackageIntegrity.VerifyAuthenticodePublisher(_preparedUpdaterPath, publisherThumbprint);

        var currentProcess = Process.GetCurrentProcess();
        var handoff = new UpdateHandoffRequest
        {
            UpdateId = updateId,
            ClientProcessId = Environment.ProcessId,
            ClientProcessStartUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks,
            CurrentVersion = UpdateCoordinator.GetCurrentVersion(),
            ManifestPath = prepared.ManifestPath,
            PackagePath = prepared.PackagePath,
            InstallRoot = UpdatePathPolicy.InstallRoot
        };
        _preparedHandoffPath = Path.Combine(cacheDirectory, "handoff.json");
        var temporary = _preparedHandoffPath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, handoff);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _preparedHandoffPath, overwrite: true);
    }

    private void HandleUpdateChanged(object? sender, EventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(IsUpdateReady));
            OnPropertyChanged(nameof(UpdateReadyVersion));
            InstallUpdateNowCommand.RaiseCanExecuteChanged();
            if (CurrentPage is WorkspaceViewModel workspace)
                workspace.RefreshUpdateState();
        });
    }

    private async Task EnterWorkspaceAsync(DriveSession session)
    {
        if (_settings is null)
            throw new V2ApiException("客户端尚未配置 NAS 服务，请联系管理员。");
        var workspace = WorkspaceViewModel.ForEmployee(this, session, _settings);
        CurrentPage = workspace;
        await workspace.InitializeAsync();
    }
}

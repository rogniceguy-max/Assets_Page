using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using System.Windows;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly V2SessionService? _sessionService;
    private NasClientSettings? _settings;
    private object _currentPage;
    private readonly string? _configurationError;
    private Task? _initializeTask;

    public MainViewModel()
    {
        ThemeService = new ThemeService();
        var login = new LoginViewModel(this);
        _currentPage = login;
        try
        {
            var settings = NasClientSettings.Load();
            _settings = settings;
            var api = new V2ApiClient(new V2ApiClientOptions(settings.ApiBaseUri, settings.CertificateSha256));
            var profileSuffix = settings.CredentialProfile == "default" ? string.Empty : "." + settings.CredentialProfile;
            var vault = new WindowsCredentialVault(new WindowsCredentialVaultOptions(
                "MuYingDrive.V2" + profileSuffix + ".ActiveSession",
                "MuYingDrive.V2" + profileSuffix + ".ActiveSmbCredential"));
            var smb = new WindowsSmbConnectionService(new WindowsSmbConnectionOptions(settings.ShareRoot), vault);
            _sessionService = new V2SessionService(api, vault, smb);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _configurationError = exception.Message;
            login.ErrorMessage = _configurationError;
        }
    }

    public ThemeService ThemeService { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public void ShowLogin() => CurrentPage = new LoginViewModel(this);

    public void ShowRegister() => CurrentPage = new RegisterViewModel(this);

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

    public void Dispose() => _sessionService?.Dispose();

    public async Task SignOutAsync(DriveSession session)
    {
        if (_sessionService is null)
            return;
        try
        {
            var workspace = CurrentPage as WorkspaceViewModel;
            if (workspace is not null)
                await workspace.Transfers.PauseAllAsync();
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
        if (_initializeTask is not null)
        {
            try { await _initializeTask; }
            catch { /* Startup errors are already surfaced on the login view. */ }
        }
        if (CurrentPage is WorkspaceViewModel workspace)
            await workspace.DisposeAsync();
        Dispose();
    }

    private async Task EnterWorkspaceAsync(DriveSession session)
    {
        if (_settings is null)
            throw new V2ApiException("客户端尚未配置 NAS 服务，请联系管理员。");
        var workspace = new WorkspaceViewModel(this, session, _settings);
        CurrentPage = workspace;
        await workspace.InitializeAsync();
    }
}

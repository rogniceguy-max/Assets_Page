using System.Net;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Coordinates V2 authentication and protected local token persistence.</summary>
public sealed class V2SessionService
{
    private readonly V2ApiClient _api;
    private readonly WindowsCredentialVault _vault;

    public V2SessionService(V2ApiClient api, WindowsCredentialVault vault)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public async Task<DriveSession?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!_vault.TryReadV2SessionToken(out var savedSession) || savedSession is null)
            return null;

        V2SessionResponse restored;
        try
        {
            restored = await _api.GetSessionAsync(savedSession.Token, cancellationToken).ConfigureAwait(true);
        }
        catch (V2ApiException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            ClearLocalCredentials();
            return null;
        }

        var expectedIdentity = FormatIdentity(restored.User);
        if (!string.Equals(expectedIdentity, savedSession.UserName, StringComparison.Ordinal))
        {
            ClearLocalCredentials();
            throw new V2ApiException("本机保存的登录身份与服务器记录不一致，请重新登录。");
        }

        return new DriveSession(restored.User, savedSession.Token);
    }

    public async Task<DriveSession> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var response = await _api.LoginAsync(userName, password, cancellationToken).ConfigureAwait(true);
        return await SaveSessionAsync(response.User, response.SessionToken).ConfigureAwait(true);
    }

    public async Task<DriveSession> RegisterAsync(
        string userName, string password, string groupType, CancellationToken cancellationToken = default)
    {
        var response = await _api.RegisterAsync(userName, password, groupType, cancellationToken).ConfigureAwait(true);
        return await SaveSessionAsync(response.User, response.SessionToken).ConfigureAwait(true);
    }

    public async Task SignOutAsync(DriveSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _api.LogoutAsync(session.SessionToken, cancellationToken).ConfigureAwait(true);
        ClearLocalCredentials();
    }

    public void Dispose() => _api.Dispose();

    private async Task<DriveSession> SaveSessionAsync(V2UserSession user, string token)
    {
        _vault.DeleteV2SessionToken();
        try
        {
            _vault.SaveV2SessionToken(FormatIdentity(user), token);
        }
        catch
        {
            try { await _api.LogoutAsync(token).ConfigureAwait(true); }
            catch { /* A later login can replace the unpersisted server session. */ }
            ClearLocalCredentials();
            throw new V2ApiException("无法安全保存登录状态，请检查 Windows 凭据管理器设置。");
        }
        return new DriveSession(user, token);
    }

    private void ClearLocalCredentials() => _vault.DeleteV2SessionToken();

    private static string FormatIdentity(V2UserSession user) => $"{user.Id}|{user.UserName}";
}

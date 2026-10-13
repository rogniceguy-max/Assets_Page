using System.Net;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Coordinates V2 authentication, protected local persistence, and native SMB setup.</summary>
public sealed class V2SessionService
{
    private readonly V2ApiClient _api;
    private readonly WindowsCredentialVault _vault;
    private readonly WindowsSmbConnectionService _smb;

    public V2SessionService(V2ApiClient api, WindowsCredentialVault vault, WindowsSmbConnectionService smb)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _smb = smb ?? throw new ArgumentNullException(nameof(smb));
    }

    public async Task<DriveSession?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!_vault.TryReadV2SessionToken(out var savedSession) || savedSession is null)
            return null;

        if (!_vault.TryReadNasPassword(out var savedNas) || savedNas is null)
        {
            return null;
        }

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

        var expectedSmbUserName = restored.User.SmbUserName;
        if (!string.Equals(savedNas.UserName, expectedSmbUserName, StringComparison.Ordinal))
        {
            // Migrate credentials saved by the earlier client, which used the
            // Chinese display name as the SMB identity. The display name and
            // API session identity remain unchanged.
            if (!string.Equals(savedNas.UserName, restored.User.UserName, StringComparison.Ordinal))
            {
                ClearLocalCredentials();
                return null;
            }

            _vault.SaveNasPassword(expectedSmbUserName, savedNas.Password);
        }

        return new DriveSession(restored.User, savedSession.Token, _smb.Connect());
    }

    public async Task<DriveSession> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var response = await _api.LoginAsync(userName, password, cancellationToken).ConfigureAwait(true);
        return await SaveAndConnectAsync(response.User, response.SessionToken, password).ConfigureAwait(true);
    }

    public async Task<DriveSession> RegisterAsync(
        string userName, string password, string groupType, CancellationToken cancellationToken = default)
    {
        var response = await _api.RegisterAsync(userName, password, groupType, cancellationToken).ConfigureAwait(true);
        return await SaveAndConnectAsync(response.User, response.SessionToken, password).ConfigureAwait(true);
    }

    public async Task SignOutAsync(DriveSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Keep the local token and credentials if the server cannot revoke the
        // session; the user can retry logout when the NAS is reachable again.
        await _api.LogoutAsync(session.SessionToken, cancellationToken).ConfigureAwait(true);

        // WNet connections can be shared with Explorer and other applications
        // in this Windows logon session. Removing the app credential must not
        // cancel the process-wide UNC connection; Windows owns its lifetime.
        ClearLocalCredentials();
    }

    public void Dispose() => _api.Dispose();

    private async Task<DriveSession> SaveAndConnectAsync(V2UserSession user, string token, string password)
    {
        // Every new account starts from a clean local state. Drop the previous
        // account's saved credentials and this process's SMB connection to the
        // share first: Windows rejects a second connection to the same server
        // under a different user name (WNet error 1219), so without the
        // disconnect the newly signed-in user cannot reach the share at all.
        // This runs only after the server accepted the new login, so a failed
        // sign-in leaves the current account's credentials intact.
        ClearLocalCredentials();
        _smb.Disconnect();

        try
        {
            _vault.SaveV2SessionToken(FormatIdentity(user), token);
            _vault.SaveNasPassword(user.SmbUserName, password);
        }
        catch
        {
            try { await _api.LogoutAsync(token).ConfigureAwait(true); }
            catch { /* The next explicit login can replace this server session. */ }
            ClearLocalCredentials();
            throw new V2ApiException("无法安全保存登录凭据，请检查 Windows 凭据管理器设置。");
        }

        return new DriveSession(user, token, _smb.Connect());
    }

    private void ClearLocalCredentials()
    {
        _vault.DeleteV2SessionToken();
        _vault.DeleteNasPassword();
    }

    private static string FormatIdentity(V2UserSession user) => $"{user.Id}|{user.UserName}";

}

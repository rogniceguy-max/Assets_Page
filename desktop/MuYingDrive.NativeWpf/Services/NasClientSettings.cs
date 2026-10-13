using System.Text.Json;
using MuYingDrive.UpdateCore;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>Non-secret V2 API, certificate pin, and SMB share settings.</summary>
public sealed record NasClientSettings(Uri ApiBaseUri, string CertificateSha256, string ShareRoot)
{
    public static NasClientSettings Load()
    {
        var stableSettingsPath = UpdatePathPolicy.ClientSettingsPath;
        try { UpdatePathPolicy.EnsureNoReparsePointComponents(stableSettingsPath); }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("客户端配置目录不安全。", exception);
        }
        var settingsPath = File.Exists(stableSettingsPath)
            ? stableSettingsPath
            : Path.Combine(AppContext.BaseDirectory, "nas-client.json");
        try { UpdatePathPolicy.EnsureNoReparsePointComponents(settingsPath); }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("客户端配置文件路径不安全。", exception);
        }
        NasClientSettings? settings = null;
        if (File.Exists(settingsPath))
        {
            try
            {
                settings = JsonSerializer.Deserialize<NasClientSettings>(File.ReadAllText(settingsPath));
            }
            catch (IOException)
            {
                throw new InvalidOperationException("客户端 NAS 配置无法读取。", null);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("客户端 NAS 配置无法读取。", null);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("客户端 NAS 配置格式无效。", null);
            }
        }

        if (settings is null)
        {
            var api = Environment.GetEnvironmentVariable("MUYING_V2_API_BASE_URI");
            var pin = Environment.GetEnvironmentVariable("MUYING_V2_CERT_SHA256");
            var share = Environment.GetEnvironmentVariable("MUYING_NAS_SHARE_ROOT");
            if (Uri.TryCreate(api, UriKind.Absolute, out var apiUri) && !string.IsNullOrWhiteSpace(pin) && !string.IsNullOrWhiteSpace(share))
                settings = new NasClientSettings(apiUri, pin, share);
        }

        if (settings is null || settings.ApiBaseUri is null || settings.ApiBaseUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(settings.CertificateSha256) ||
            string.IsNullOrWhiteSpace(settings.ShareRoot) ||
            !settings.ShareRoot.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException("客户端尚未配置 NAS 服务，请联系管理员。", null);

        return settings;
    }
}

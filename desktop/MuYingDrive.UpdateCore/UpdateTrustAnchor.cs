using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MuYingDrive.UpdateCore;

public static class UpdateTrustAnchor
{
    private const string PublicKeyResource = "MuYingDrive.UpdateCore.Trust.trusted-update-publisher.pem";
    private const string PublisherThumbprintResource = "MuYingDrive.UpdateCore.Trust.trusted-code-signing-thumbprint.txt";

    public static ECDsa LoadManifestPublicKey()
    {
        var pem = ReadResource(PublicKeyResource).Trim();
        if (!pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
            throw new CryptographicException("更新发布公钥尚未配置；已安全停用更新功能。");

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.KeySize != 256)
                throw new CryptographicException("更新发布公钥必须使用 ECDSA P-256。");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns the pinned Authenticode publisher SHA-256 fingerprint, or an empty
    /// string when no publisher pin is configured. An empty pin disables Authenticode
    /// verification (see <see cref="PackageIntegrity.VerifyAuthenticodePublisher"/>);
    /// the ECDSA manifest signature and the whole-package SHA-256 remain the trust
    /// anchor. A non-empty but malformed value still fails closed.
    /// </summary>
    public static string LoadCodeSigningThumbprint()
    {
        var configuredLine = ReadResource(PublisherThumbprintResource)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('#', 2)[0].Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        if (configuredLine.Length == 0)
            return string.Empty;
        if (configuredLine.Any(character => !Uri.IsHexDigit(character) && character is not ':' and not '-' && !char.IsWhiteSpace(character)))
            throw new CryptographicException("Authenticode 发布者指纹格式无效；已安全停用更新功能。");
        var value = new string(configuredLine.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (!Regex.IsMatch(value, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant))
            throw new CryptographicException("Authenticode 发布者指纹格式无效；已安全停用更新功能。");
        return value;
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new CryptographicException("更新信任配置缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

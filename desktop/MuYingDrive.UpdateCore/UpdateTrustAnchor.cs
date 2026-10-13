using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MuYingDrive.UpdateCore;

public static class UpdateTrustAnchor
{
    private const string PublicKeyResource = "MuYingDrive.UpdateCore.Trust.trusted-update-publisher.pem";
    private const string PublisherThumbprintResource = "MuYingDrive.UpdateCore.Trust.trusted-code-signing-thumbprint.txt";

    /// <summary>
    /// Returns every release public key embedded in the trust anchor, in file order.
    /// </summary>
    /// <remarks>
    /// More than one key is supported so a release key can be rotated without
    /// reinstalling every client: embed the next public key, ship a release signed by
    /// the current key, wait for clients to take it, then start signing with the new
    /// key. Verifying against a single key would make rotation and recovery from key
    /// loss impossible in the field. The caller owns the returned keys and must
    /// dispose them.
    /// </remarks>
    public static IReadOnlyList<ECDsa> LoadManifestPublicKeys()
    {
        var pem = ReadResource(PublicKeyResource);
        var keys = new List<ECDsa>();
        try
        {
            foreach (var block in ExtractPemBlocks(pem, "-----BEGIN PUBLIC KEY-----", "-----END PUBLIC KEY-----"))
            {
                var key = ECDsa.Create();
                try
                {
                    key.ImportFromPem(block);
                    if (key.KeySize != 256)
                        throw new CryptographicException("更新发布公钥必须使用 ECDSA P-256。");
                    keys.Add(key);
                }
                catch
                {
                    key.Dispose();
                    throw;
                }
            }

            if (keys.Count == 0)
                throw new CryptographicException("更新发布公钥尚未配置；已安全停用更新功能。");

            return keys;
        }
        catch
        {
            foreach (var key in keys)
                key.Dispose();
            throw;
        }
    }

    private static IEnumerable<string> ExtractPemBlocks(string pem, string begin, string end)
    {
        var index = 0;
        while (true)
        {
            var start = pem.IndexOf(begin, index, StringComparison.Ordinal);
            if (start < 0)
                yield break;
            var stop = pem.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0)
                yield break;
            var finish = stop + end.Length;
            yield return pem[start..finish];
            index = finish;
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

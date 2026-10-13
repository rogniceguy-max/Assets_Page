using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MuYingDrive.UpdateCore;

public sealed record SignedUpdateManifest
{
    [JsonPropertyName("payload")]
    public required string Payload { get; init; }

    [JsonPropertyName("signature")]
    public required string Signature { get; init; }
}

public sealed record UpdateManifest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }

    [JsonPropertyName("package_id")]
    public required string PackageId { get; init; }

    [JsonPropertyName("package_size")]
    public long PackageSize { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("published_at_utc")]
    public required string PublishedAtUtc { get; init; }
}

public sealed record VerifiedUpdateManifest(UpdateManifest Value, byte[] EnvelopeBytes, byte[] PayloadBytes)
{
    public Version ParsedVersion => Version.Parse(Value.Version);
    public string PackageFileName => $"{Value.PackageId}.zip";
}

public static class UpdateManifestVerifier
{
    public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private static readonly Regex VersionPattern = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PackageIdPattern = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HashPattern = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static VerifiedUpdateManifest Verify(ReadOnlySpan<byte> envelopeBytes)
    {
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > 64 * 1024)
            throw new InvalidDataException("更新清单大小无效。");

        SignedUpdateManifest envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedUpdateManifest>(envelopeBytes, JsonOptions)
                ?? throw new InvalidDataException("更新清单格式无效。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("更新清单格式无效。", exception);
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("更新清单签名格式无效。", exception);
        }

        if (payload.Length is 0 or > 32 * 1024 || signature.Length is 0 or > 256)
            throw new InvalidDataException("更新清单签名大小无效。");

        // Try every embedded release key so a rotated key can coexist with the current
        // one during the overlap window.
        var verifiers = UpdateTrustAnchor.LoadManifestPublicKeys();
        try
        {
            var verified = verifiers.Any(verifier => verifier.VerifyData(
                payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            if (!verified)
                throw new CryptographicException("更新清单签名验证失败。");
        }
        finally
        {
            foreach (var verifier in verifiers)
                verifier.Dispose();
        }

        UpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, JsonOptions)
                ?? throw new InvalidDataException("更新清单内容无效。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("更新清单内容无效。", exception);
        }

        Validate(manifest);
        return new VerifiedUpdateManifest(manifest, envelopeBytes.ToArray(), payload);
    }

    public static bool IsNewer(string candidate, string current)
    {
        if (!VersionPattern.IsMatch(candidate) || !VersionPattern.IsMatch(current))
            return false;
        return Version.Parse(candidate).CompareTo(Version.Parse(current)) > 0;
    }

    private static void Validate(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Channel != "stable" ||
            manifest.Platform != "windows" || manifest.Architecture != "x64")
            throw new InvalidDataException("更新清单与此客户端的平台或发布频道不匹配。");
        if (!VersionPattern.IsMatch(manifest.Version))
            throw new InvalidDataException("更新版本号格式无效。");
        if (!PackageIdPattern.IsMatch(manifest.PackageId))
            throw new InvalidDataException("更新包标识无效。");
        if (manifest.PackageSize is <= 0 or > MaximumPackageBytes)
            throw new InvalidDataException("更新包大小超出允许范围。");
        if (!HashPattern.IsMatch(manifest.Sha256))
            throw new InvalidDataException("更新包校验值无效。");
        if (!DateTimeOffset.TryParse(manifest.PublishedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var published) ||
            published > DateTimeOffset.UtcNow.AddHours(24))
            throw new InvalidDataException("更新发布时间无效。");
    }
}

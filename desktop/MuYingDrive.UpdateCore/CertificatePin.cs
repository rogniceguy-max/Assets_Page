using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MuYingDrive.UpdateCore;

/// <summary>
/// Validation for the pinned NAS TLS leaf and the CA carried by this application.
/// </summary>
/// <remarks>
/// Shared by the desktop client and the first-install bootstrap so both accept exactly
/// the same certificates. A second, drifting copy of this rule would be a silent
/// security regression, so it lives here rather than in either consumer.
/// </remarks>
public static class CertificatePin
{
    private const string NasRootCertificateResource =
        "MuYingDrive.UpdateCore.Trust.nas-root-ca.pem";
    private const string TlsServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private static readonly Lazy<X509Certificate2> NasRootCertificate =
        new(LoadNasRootCertificate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Converts a user-supplied SHA-256 fingerprint (with optional colons, hyphens or
    /// spaces) into the raw pin bytes.
    /// </summary>
    public static byte[] ParseSha256Pin(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var normalized = new string(fingerprint.Where(Uri.IsHexDigit).ToArray());
        if (normalized.Length != 64 ||
            fingerprint.Any(c => !Uri.IsHexDigit(c) && c is not ':' and not '-' && !char.IsWhiteSpace(c)))
        {
            throw new ArgumentException(
                "The certificate fingerprint must contain exactly 32 SHA-256 bytes in hexadecimal form.",
                nameof(fingerprint));
        }

        try
        {
            return Convert.FromHexString(normalized);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The certificate fingerprint is invalid.", nameof(fingerprint), exception);
        }
    }

    /// <summary>
    /// Accepts a certificate only when the name matches, the validity window covers now,
    /// it chains to the NAS CA carried by this application, and the SHA-256 leaf pin matches.
    /// </summary>
    public static bool IsTrusted(
        X509Certificate2? certificate,
        X509Chain? _,
        SslPolicyErrors errors,
        ReadOnlySpan<byte> expectedPin)
    {
        if (certificate is null ||
            (errors & (SslPolicyErrors.RemoteCertificateNotAvailable |
                       SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
        {
            return false;
        }

        try
        {
            var now = DateTime.UtcNow;
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
                return false;

            const SslPolicyErrors chainErrors = SslPolicyErrors.RemoteCertificateChainErrors;
            if ((errors & ~chainErrors) != SslPolicyErrors.None)
                return false;
            if (!BuildsToEmbeddedNasRoot(certificate))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(
                certificate.GetCertHash(HashAlgorithmName.SHA256), expectedPin);
        }
        catch (Exception exception) when (
            exception is CryptographicException or InvalidOperationException or IOException or FormatException)
        {
            return false;
        }
    }

    private static bool BuildsToEmbeddedNasRoot(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(NasRootCertificate.Value);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(TlsServerAuthenticationOid));
        // This private NAS CA does not publish an online revocation list. The pinned
        // leaf hash, its validity window, and this app-scoped CA are the revocation anchor.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    private static X509Certificate2 LoadNasRootCertificate()
    {
        using var stream = typeof(CertificatePin).Assembly
            .GetManifestResourceStream(NasRootCertificateResource)
            ?? throw new InvalidOperationException("The embedded NAS CA certificate is missing.");
        using var reader = new StreamReader(stream);
        return X509Certificate2.CreateFromPem(reader.ReadToEnd());
    }
}

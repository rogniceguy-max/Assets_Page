using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MuYingDrive.UpdateCore;

/// <summary>
/// Validation for the pinned self-signed TLS leaf the V2 API presents.
/// </summary>
/// <remarks>
/// Shared by the desktop client and the first-install bootstrap so both accept exactly
/// the same certificates. A second, drifting copy of this rule would be a silent
/// security regression, so it lives here rather than in either consumer.
/// </remarks>
public static class CertificatePin
{
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
    /// chain failures are limited to an untrusted root (the leaf is self-signed), and the
    /// SHA-256 pin matches.
    /// </summary>
    public static bool IsTrusted(
        X509Certificate2? certificate,
        X509Chain? chain,
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
            if ((errors & chainErrors) != 0 &&
                (chain is null || chain.ChainStatus.Any(status =>
                    (status.Status & ~X509ChainStatusFlags.UntrustedRoot) != X509ChainStatusFlags.NoError)))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(
                certificate.GetCertHash(HashAlgorithmName.SHA256), expectedPin);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

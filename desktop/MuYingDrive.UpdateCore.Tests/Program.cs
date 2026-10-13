using System.Security.Cryptography;
using System.Text;
using MuYingDrive.UpdateCore;

// Console test harness for the update trust path. Runs self-contained checks by
// default; pass a signed manifest envelope path to additionally verify it against
// the embedded release public key (that is how the release pipeline is validated).

var failures = 0;

void Check(string name, bool condition)
{
    Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + name);
    if (!condition) failures++;
}

void Throws<T>(string name, Action action) where T : Exception
{
    try
    {
        action();
        Console.WriteLine("  FAIL  " + name + " (no exception)");
        failures++;
    }
    catch (T)
    {
        Console.WriteLine("  PASS  " + name);
    }
    catch (Exception exception)
    {
        Console.WriteLine($"  FAIL  {name} (expected {typeof(T).Name}, got {exception.GetType().Name}: {exception.Message})");
        failures++;
    }
}

Console.WriteLine("VersionStamp.NormalizeProductVersion");
Check("strips the SDK source-revision suffix", VersionStamp.NormalizeProductVersion("1.0.0+bbc42ea") == "1.0.0");
Check("truncates a four-part FileVersion", VersionStamp.NormalizeProductVersion("1.2.3.0") == "1.2.3");
Check("keeps a plain three-part version", VersionStamp.NormalizeProductVersion("2.3.4") == "2.3.4");
Check("rejects a two-part version", VersionStamp.NormalizeProductVersion("1.0") is null);
Check("rejects a prerelease suffix", VersionStamp.NormalizeProductVersion("1.0.0-beta") is null);
Check("rejects null and empty", VersionStamp.NormalizeProductVersion(null) is null && VersionStamp.NormalizeProductVersion(" ") is null);
Check("rejects non-ASCII digits", VersionStamp.NormalizeProductVersion("١.٠.٠") is null);

Console.WriteLine("UpdateTrustAnchor");
var keys = UpdateTrustAnchor.LoadManifestPublicKeys();
Check("loads exactly one embedded release key", keys.Count == 1);
Check("release key is ECDSA P-256", keys[0].KeySize == 256);
foreach (var key in keys) key.Dispose();
// An empty Authenticode pin means signing is intentionally skipped; the ECDSA
// manifest signature and the package SHA-256 are the anchor instead.
Check("Authenticode pin is empty (signing skipped by design)", UpdateTrustAnchor.LoadCodeSigningThumbprint().Length == 0);

Console.WriteLine("UpdateManifestVerifier.Verify rejects malformed input");
Throws<InvalidDataException>("rejects an empty envelope", () => UpdateManifestVerifier.Verify(Array.Empty<byte>()));
Throws<InvalidDataException>("rejects non-JSON bytes", () => UpdateManifestVerifier.Verify(Encoding.UTF8.GetBytes("not json")));
Throws<InvalidDataException>("rejects an envelope with no signature field", () =>
    UpdateManifestVerifier.Verify(Encoding.UTF8.GetBytes("{\"payload\":\"AAAA\"}")));
Throws<CryptographicException>("rejects a well-formed but unsigned envelope", () =>
{
    var payload = Encoding.UTF8.GetBytes(
        "{\"schema_version\":1,\"channel\":\"stable\",\"version\":\"9.9.9\",\"platform\":\"windows\"," +
        "\"architecture\":\"x64\",\"package_id\":\"0123456789abcdef0123456789abcdef\",\"package_size\":1," +
        "\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"," +
        "\"published_at_utc\":\"2026-01-01T00:00:00.000Z\"}");
    var envelope = "{\"payload\":\"" + Convert.ToBase64String(payload) + "\",\"signature\":\"" +
        Convert.ToBase64String(new byte[64]) + "\"}";
    UpdateManifestVerifier.Verify(Encoding.UTF8.GetBytes(envelope));
});

if (args.Length > 0)
{
    Console.WriteLine("Signed release verification: " + args[0]);
    try
    {
        var verified = UpdateManifestVerifier.Verify(File.ReadAllBytes(args[0]));
        Console.WriteLine($"  PASS  envelope verifies against the embedded release key (version {verified.Value.Version})");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"  FAIL  envelope did not verify: {exception.GetType().Name}: {exception.Message}");
        failures++;
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

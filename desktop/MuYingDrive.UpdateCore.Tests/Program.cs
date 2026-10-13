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

Console.WriteLine("ClientConfigurationMerge.TryRefreshConnectionFields");
{
    // Shaped like a real nas-client.json: the connection fields the installer owns, plus
    // the profile that belongs to whoever is signed in on this machine.
    const string existing = """
        {
          "ApiBaseUri": "https://old-nas:8443/",
          "CertificateSha256": "1111111111111111111111111111111111111111111111111111111111111111",
          "ShareRoot": "\\\\old-nas\\Assets",
          "CredentialProfile": "colleague-b"
        }
        """;

    // A rotated certificate is the case this exists for: the installer must overwrite the
    // pin, and must not touch the profile that names the user's stored credentials.
    Check("a rotated certificate rewrites the pin and share root",
        ClientConfigurationMerge.TryRefreshConnectionFields(existing,
            "https://new-nas:8443/", new string('2', 64), @"\\new-nas\Assets", out var rotated));
    Check("the rotated pin is written", rotated.Contains(new string('2', 64)));
    Check("the rotated address is written", rotated.Contains("https://new-nas:8443/"));
    Check("the old pin is gone", !rotated.Contains(new string('1', 64)));
    Check("the credential profile survives a certificate rotation",
        rotated.Contains("colleague-b"));

    Check("an unchanged installer config is reported as a no-op",
        ClientConfigurationMerge.TryRefreshConnectionFields(existing,
            "https://old-nas:8443/", new string('1', 64), @"\\old-nas\Assets", out var unchanged));
    // Byte-identical means the caller can skip writing the file entirely, which keeps an
    // ordinary reinstall from touching a file the client reads at every start.
    Check("a no-op leaves the text byte-identical", ReferenceEquals(unchanged, existing));

    Check("unknown fields are preserved",
        ClientConfigurationMerge.TryRefreshConnectionFields(
            """{"ApiBaseUri":"https://a/","CertificateSha256":"00","ShareRoot":"\\\\a\\B","SomethingElse":7}""",
            "https://b/", "11", @"\\b\B", out var extra) && extra.Contains("SomethingElse"));

    // Returning false sends the installer down its overwrite path: a file the client
    // cannot parse supplies no credential profile worth preserving either.
    Check("a non-object JSON document is rejected",
        !ClientConfigurationMerge.TryRefreshConnectionFields("[]", "https://a/", "00", @"\\a\B", out _));
    Check("malformed JSON is rejected",
        !ClientConfigurationMerge.TryRefreshConnectionFields("{not json", "https://a/", "00", @"\\a\B", out _));
    Check("an empty document is rejected",
        !ClientConfigurationMerge.TryRefreshConnectionFields("", "https://a/", "00", @"\\a\B", out _));
}

Console.WriteLine("UpdateFailureText.DescribeConnectionFailure");
{
    // The whole point of this function: these two failures look identical on the wire and
    // want opposite responses, so they must not read identically to the person seeing them.
    // .NET 8 puts the error kind first in this overload.
    var pinMismatch = new HttpRequestException(
        HttpRequestError.SecureConnectionError, "The SSL connection could not be established.");
    var unreachable = new HttpRequestException(
        HttpRequestError.NameResolutionError, "No such host is known.");

    var pinText = UpdateFailureText.DescribeConnectionFailure(pinMismatch);
    var unreachableText = UpdateFailureText.DescribeConnectionFailure(unreachable);

    Check("a pin mismatch is not reported as a network fault", !pinText.Contains("请检查网络"));
    Check("a pin mismatch names the certificate as the cause", pinText.Contains("证书"));
    Check("a pin mismatch warns against continuing", pinText.Contains("不要") || pinText.Contains("请勿")
        || pinText.Contains("再继续"));
    Check("an unreachable server still reports the plain network text", unreachableText.Contains("请检查网络"));
    Check("the two failures do not share their wording", pinText != unreachableText);
    Check("the download prefix is honoured",
        UpdateFailureText.DescribeConnectionFailure(unreachable, "无法下载客户端安装包")
            .Contains("无法下载客户端安装包"));
}

if (args.Length > 0 && args[0] == "--check-resources")
{
    // A StaticResource key that does not exist compiles fine and throws
    // XamlParseException the moment the view is loaded — which is how the tour view
    // crashed on its first click. WPF resolves keys against the file itself, then
    // Application.Resources (including its merged theme dictionaries), so this mirrors
    // exactly that lookup.
    var clientDirectory = args[1];
    var keyPattern = new System.Text.RegularExpressions.Regex("x:Key=\"([^\"]+)\"");
    var usePattern = new System.Text.RegularExpressions.Regex("\\{StaticResource ([^}]+)\\}");

    var appKeys = new HashSet<string>(StringComparer.Ordinal);
    var appPath = Path.Combine(clientDirectory, "App.xaml");
    foreach (System.Text.RegularExpressions.Match match in keyPattern.Matches(File.ReadAllText(appPath)))
        appKeys.Add(match.Groups[1].Value.Trim());

    var mergedKeys = new HashSet<string>(StringComparer.Ordinal);
    foreach (var source in System.Text.RegularExpressions.Regex.Matches(
        File.ReadAllText(appPath), "Source=\"([^\"]+\\.xaml)\"").Select(m => m.Groups[1].Value))
    {
        var themePath = Path.Combine(clientDirectory, source.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(themePath)) continue;
        foreach (System.Text.RegularExpressions.Match match in keyPattern.Matches(File.ReadAllText(themePath)))
            mergedKeys.Add(match.Groups[1].Value.Trim());
    }
    Console.WriteLine($"App.xaml defines {appKeys.Count} keys; merged themes define {mergedKeys.Count}");

    // Keys from a dictionary a view merges into itself count for that view.
    HashSet<string> KeysOf(string xamlPath)
    {
        var text = File.ReadAllText(xamlPath);
        var keys = keyPattern.Matches(text).Select(m => m.Groups[1].Value.Trim())
            .ToHashSet(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match source in System.Text.RegularExpressions.Regex.Matches(
            text, "Source=\"([^\"]+\\.xaml)\""))
        {
            var directory = Path.GetDirectoryName(xamlPath) ?? clientDirectory;
            var referenced = Path.Combine(directory, source.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(referenced))
                foreach (System.Text.RegularExpressions.Match match in keyPattern.Matches(File.ReadAllText(referenced)))
                    keys.Add(match.Groups[1].Value.Trim());
        }
        return keys;
    }

    var missing = 0;
    foreach (var xaml in Directory.EnumerateFiles(clientDirectory, "*.xaml", SearchOption.AllDirectories))
    {
        var localKeys = KeysOf(xaml);
        foreach (System.Text.RegularExpressions.Match match in usePattern.Matches(File.ReadAllText(xaml)))
        {
            var key = match.Groups[1].Value.Trim();
            // A nested markup extension such as {x:Type TextBlock} is not a key lookup.
            if (key.Contains('{') || key.Contains('}')) continue;
            if (localKeys.Contains(key) || appKeys.Contains(key) || mergedKeys.Contains(key)) continue;
            Console.WriteLine($"  FAIL  {Path.GetFileName(xaml)} uses {{StaticResource {key}}} but nothing defines it");
            missing++;
        }
    }

    Check($"every StaticResource key resolves ({missing} missing)", missing == 0);
    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "ALL RESOURCE KEYS RESOLVE" : $"{failures} UNRESOLVED RESOURCE PROBLEM(S)");
    return failures == 0 ? 0 : 1;
}

if (args.Length > 0 && args[0] == "--check-icons")
{
    // XAML names icons as strings, and LucideIcon draws nothing for an unknown name —
    // a silent blank control. That is how the file view's navigate-up button and the
    // transfer view's cancel button shipped without icons. Keep the two lists in sync.
    var clientDirectory = args[1];
    var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
        File.ReadAllText(Path.Combine(clientDirectory, "Controls", "LucideIcon.cs")),
        "\\[\"([A-Za-z0-9]+)\"\\]\\s*="))
    {
        defined.Add(match.Groups[1].Value);
    }

    Console.WriteLine($"LucideIcon defines {defined.Count} icons");
    var used = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var xaml in Directory.EnumerateFiles(clientDirectory, "*.xaml", SearchOption.AllDirectories))
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            File.ReadAllText(xaml), "LucideIcon Icon=\"([A-Za-z0-9]+)\""))
        {
            used.TryAdd(match.Groups[1].Value, Path.GetFileName(xaml));
        }
    }

    foreach (var (name, file) in used)
    {
        Check($"{name,-16} (used in {file}) is defined", defined.Contains(name));
    }

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "ALL ICONS RESOLVE" : $"{failures} UNRESOLVED ICON(S)");
    return failures == 0 ? 0 : 1;
}

if (args.Length > 0 && args[0] == "--probe-update")
{
    // Exercises the client's exact update plumbing — settings file, TLS pin, manifest
    // fetch, signature verification, version comparison and package download — without
    // the UI, so a failure here isolates the network path from the client's wiring.
    Console.WriteLine("Update probe using " + args[1]);
    try
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1]));
        var root = document.RootElement;
        var apiBase = new Uri(root.GetProperty("ApiBaseUri").GetString()!);
        var pin = root.GetProperty("CertificateSha256").GetString()!;
        var currentVersion = args.Length > 2 ? args[2] : "1.0.0";
        Console.WriteLine($"  settings: {apiBase}  current={currentVersion}");

        using var downloader = new ReleaseDownloader(apiBase, pin);
        var verified = await downloader.GetStableManifestAsync();
        Console.WriteLine($"  PASS  manifest fetched and signature verified (server version {verified.Value.Version})");

        if (!UpdateManifestVerifier.IsNewer(verified.Value.Version, currentVersion))
        {
            Console.WriteLine("  PASS  no newer version available");
        }
        else
        {
            Console.WriteLine($"  update available: {currentVersion} -> {verified.Value.Version}, downloading");
            var target = Path.Combine(Path.GetTempPath(), "muying-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            var partial = Path.Combine(target, "package.partial");
            var started = DateTime.UtcNow;
            var package = await downloader.DownloadPackageAsync(verified, partial, null);
            var info = new FileInfo(package);
            Console.WriteLine($"  PASS  package downloaded ({info.Length} bytes, {Math.Round((DateTime.UtcNow - started).TotalSeconds, 1)}s), SHA-256 verified");
            Directory.Delete(target, recursive: true);
        }
    }
    catch (Exception exception)
    {
        Console.WriteLine($"  FAIL  {exception.GetType().Name}: {exception.Message}");
        failures++;
    }
    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "PROBE PASSED" : $"{failures} PROBE FAILURE(S)");
    return failures == 0 ? 0 : 1;
}

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

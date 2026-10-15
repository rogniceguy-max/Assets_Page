using System.Security.Cryptography;
using System.Text;

namespace MuYingDrive.NativeWpf.Services;

public static class FileClientIdentity
{
    public static string GetEmployeeAgentNamespace(Uri apiBaseUri, long accountId)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        return "employee-" + HashNamespace("employee\n" + ServiceKey(apiBaseUri) + "\n" + accountId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
    }

    public static string GetVisitorAgentNamespace(Uri apiBaseUri, string sessionToken)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        return "visitor-" + HashNamespace("visitor\n" + ServiceKey(apiBaseUri) + "\n" + sessionToken);
    }

    public static string GetEmployeeSyncConfigPath(Uri apiBaseUri, long accountId)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        var accountKey = HashNamespace("sync-config\n" + ServiceKey(apiBaseUri) + "\n" + accountId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MuYingDrive", "Sync", accountKey, "sync_config.json");
    }

    public static string GetOrCreate(Uri apiBaseUri, long accountId)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuYingDrive", "FileClients");
        Directory.CreateDirectory(root);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            ServiceKey(apiBaseUri) + "\n" + accountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)))).ToLowerInvariant();
        var path = Path.Combine(root, key + ".id");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing))
            return existing.ToString("D");

        var clientId = Guid.NewGuid().ToString("D");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, clientId, new UTF8Encoding(false));
        try { File.Move(temporary, path, overwrite: true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
        return clientId;
    }

    private static string HashNamespace(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ServiceKey(Uri apiBaseUri)
    {
        if (!apiBaseUri.IsAbsoluteUri || apiBaseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(apiBaseUri.UserInfo) || !string.IsNullOrEmpty(apiBaseUri.Query) ||
            !string.IsNullOrEmpty(apiBaseUri.Fragment))
            throw new ArgumentException("A normalized HTTPS API base URI is required.", nameof(apiBaseUri));
        var authority = apiBaseUri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        var path = apiBaseUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        return path.Length == 0 ? authority : authority + "/" + path;
    }
}

public sealed class LogicalFileScope
{
    public LogicalFileScope(string scopeId, string? projectId = null, string rootPath = "",
        bool canWrite = true, string mediaKind = "all", bool flatten = false)
    {
        ScopeId = string.IsNullOrWhiteSpace(scopeId) ? throw new ArgumentException("Scope ID is required.", nameof(scopeId)) : scopeId;
        ProjectId = projectId;
        RootPath = Normalize(rootPath);
        CanWrite = canWrite;
        MediaKind = mediaKind;
        Flatten = flatten;
    }

    public string ScopeId { get; }
    public string? ProjectId { get; }
    public string RootPath { get; }
    public bool CanWrite { get; }
    public string MediaKind { get; }
    public bool Flatten { get; }

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".") return string.Empty;
        if (path.Contains('\\') || path.StartsWith("/", StringComparison.Ordinal) || path.Contains('\0'))
            throw new InvalidOperationException("逻辑路径格式无效。");
        var pieces = path.Split('/');
        if (pieces.Any(piece => piece is "" or "." or ".."))
            throw new InvalidOperationException("逻辑路径格式无效。");
        return string.Join('/', pieces);
    }

    public static string Combine(string parent, string child) => Normalize(string.IsNullOrEmpty(parent)
        ? child : parent + "/" + child);

    public static string Parent(string path)
    {
        var normalized = Normalize(path);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? string.Empty : normalized[..index];
    }
}

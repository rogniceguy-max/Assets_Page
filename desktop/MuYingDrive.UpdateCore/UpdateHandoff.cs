using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuYingDrive.UpdateCore;

public sealed class UpdateRecoveryRequiredException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record UpdateHandoffRequest
{
    public required string UpdateId { get; init; }
    public int ClientProcessId { get; init; }
    public long ClientProcessStartUtcTicks { get; init; }
    public required string ManifestPath { get; init; }
    public required string PackagePath { get; init; }
    public required string InstallRoot { get; init; }
}

public static class UpdatePathPolicy
{
    public static string UserDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuYingDrive");
    public static string UpdateRoot => Path.Combine(UserDataRoot, "Updates");
    public static string SettingsDirectory => Path.Combine(UserDataRoot, "Settings");
    public static string ClientSettingsPath => Path.Combine(SettingsDirectory, "nas-client.json");
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MuYingDrive");

    public static string GetPackageCacheDirectory(string packageId) => Path.Combine(UpdateRoot, packageId);

    public static string NormalizeAndRequireInstallRoot(string path)
    {
        var expected = Path.GetFullPath(InstallRoot).TrimEnd(Path.DirectorySeparatorChar);
        var supplied = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(expected, supplied, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("更新器只允许修改当前用户的沐影盘安装目录。");
        EnsureNoReparsePointComponents(expected);
        return expected;
    }

    public static string RequireUnderUpdateRoot(string path)
    {
        var root = Path.GetFullPath(UpdateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("更新文件必须位于本地更新暂存目录。");
        EnsureNoReparsePointComponents(full);
        return full;
    }

    public static UpdateHandoffRequest LoadAndValidateRequest(string path)
    {
        var requestPath = RequireUnderUpdateRoot(path);
        var requestInfo = new FileInfo(requestPath);
        if (!requestInfo.Exists || requestInfo.Length is <= 0 or > 16 * 1024 ||
            (File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新请求无效。");

        UpdateHandoffRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<UpdateHandoffRequest>(File.ReadAllBytes(requestPath), new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 8
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("更新请求格式无效。", exception);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.UpdateId) || request.UpdateId.Length != 32 ||
            !request.UpdateId.All(Uri.IsHexDigit) || request.ClientProcessId <= 0 || request.ClientProcessStartUtcTicks <= 0 ||
            string.IsNullOrWhiteSpace(request.ManifestPath) || string.IsNullOrWhiteSpace(request.PackagePath) ||
            string.IsNullOrWhiteSpace(request.InstallRoot) ||
            !string.Equals(Path.GetFileName(requestPath), "handoff.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新请求无效。");

        var package = RequireUnderUpdateRoot(request.PackagePath);
        var manifest = RequireUnderUpdateRoot(request.ManifestPath);
        var cacheDirectory = Path.GetFullPath(Path.GetDirectoryName(package)!);
        if (!string.Equals(Path.GetFileName(package), "package.zip", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(manifest), "manifest.json", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(manifest), cacheDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(requestPath), cacheDirectory, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("更新包路径与本地暂存策略不一致。");
        foreach (var filePath in new[] { package, manifest })
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("本地更新文件不存在或路径不安全。");
            var maximumLength = string.Equals(filePath, package, StringComparison.OrdinalIgnoreCase)
                ? UpdateManifestVerifier.MaximumPackageBytes : 64 * 1024;
            if (info.Length is <= 0 || info.Length > maximumLength)
                throw new InvalidDataException("本地更新文件大小超出允许范围。");
        }
        _ = NormalizeAndRequireInstallRoot(request.InstallRoot);
        return request;
    }

    public static void EnsureNoReparsePointComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException("本地更新路径无效。");
        var current = root;
        foreach (var component in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("更新路径不能包含符号链接或重解析目录。");
        }
    }
}

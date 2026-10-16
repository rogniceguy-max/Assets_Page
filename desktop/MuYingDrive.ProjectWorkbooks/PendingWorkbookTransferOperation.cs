using System.IO;
using System.Text.Json;

namespace MuYingDrive.ProjectWorkbooks;

/// <summary>
/// Durable intent for an upload that is not yet linked to its workbook row. It contains no
/// credentials or media bytes; the Rust agent owns transfer state and the workbook service
/// owns the eventual business link.
/// </summary>
public sealed record PendingWorkbookTransferOperation(
    string Scope,
    string OperationId,
    string Kind,
    string ProjectId,
    string WorkbookId,
    string RowId,
    string LocalPath,
    string DisplayName,
    bool SelectedTarget,
    long SourceLength,
    long SourceLastWriteUtcTicks,
    string State,
    string? Error,
    ImageAsset? UploadedImage,
    ProjectFileTransferResult? UploadedVideo)
{
    private static string DirectoryPath(string scope)
    {
        if (scope.Length != 64 || scope.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("待恢复上传的服务器/账号范围无效。");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuYingDrive", "ProjectWorkbooks", "pending-transfers", scope);
    }

    private static string ManifestPath(string scope, string operationId)
    {
        if (!Guid.TryParseExact(operationId, "D", out _))
            throw new InvalidDataException("待恢复上传标识无效。");
        return Path.Combine(DirectoryPath(scope), operationId + ".json");
    }

    public static IReadOnlyList<PendingWorkbookTransferOperation> LoadAll(string scope)
    {
        var directory = DirectoryPath(scope);
        if (!Directory.Exists(directory)) return [];
        var results = new List<PendingWorkbookTransferOperation>();
        string[] paths;
        try { paths = Directory.EnumerateFiles(directory, "*.json").ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return results; }
        foreach (var path in paths)
        {
            try
            {
                if (new FileInfo(path).Length > 64 * 1024) continue;
                var operation = JsonSerializer.Deserialize<PendingWorkbookTransferOperation>(File.ReadAllText(path));
                if (operation is null) continue;
                if (operation.Scope != scope ||
                    !Path.GetFullPath(path).Equals(Path.GetFullPath(ManifestPath(scope, operation.OperationId)),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    operation.Kind is not ("asset_image" or "scene_video") ||
                    string.IsNullOrWhiteSpace(operation.ProjectId) || string.IsNullOrWhiteSpace(operation.WorkbookId) ||
                    string.IsNullOrWhiteSpace(operation.RowId) || !Path.IsPathFullyQualified(operation.LocalPath))
                    continue;
                results.Add(operation);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // A damaged operation must not hide the remaining recoverable uploads.
            }
        }
        return results;
    }

    public void Save()
    {
        var manifest = ManifestPath(Scope, OperationId);
        var directory = Path.GetDirectoryName(manifest)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(manifest))
        {
            var existing = JsonSerializer.Deserialize<PendingWorkbookTransferOperation>(File.ReadAllText(manifest));
            if (existing is null || existing.Scope != Scope || existing.OperationId != OperationId ||
                existing.Kind != Kind || existing.ProjectId != ProjectId || existing.WorkbookId != WorkbookId ||
                existing.RowId != RowId || existing.LocalPath != LocalPath)
                throw new InvalidDataException("此上传操作 ID 已属于另一条工作簿关联，不能覆盖待恢复记录。");
        }

        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, manifest, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Remove()
    {
        var manifest = ManifestPath(Scope, OperationId);
        if (File.Exists(manifest)) File.Delete(manifest);
    }
}

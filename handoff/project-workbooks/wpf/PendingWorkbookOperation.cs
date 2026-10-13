using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MuYingDrive.ProjectWorkbooks;

// Stores no credential. The exact source fingerprint prevents a changed file being retried as the old operation.
public sealed record PendingWorkbookOperation(string Scope, string OperationId, string ProjectId, string? TemplateKind,
    string? SourcePath, string? SourceSha256, string? RevisionOf)
{
    public string Description => TemplateKind == null ? Path.GetFileName(SourcePath) + (RevisionOf == null ? "（导入）" : "（修订）") : TemplateKind == "scene_beats" ? "新建节拍表" : "新建资产表";
    private static string DirectoryPath(string scope)
    {
        if (scope.Length != 64 || scope.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("待重试操作的服务器/账号范围无效。");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuYingDrive", "ProjectWorkbooks", "pending-operations", scope);
    }
    private static string Manifest(string scope, string operationId)
    {
        if (!Guid.TryParseExact(operationId, "D", out _)) throw new InvalidDataException("待处理操作标识无效。");
        return Path.Combine(DirectoryPath(scope), operationId + ".json");
    }
    public static PendingWorkbookOperation? Load(string scope)
    {
        try
        {
            if (!Directory.Exists(DirectoryPath(scope))) return null;
            foreach (var path in Directory.EnumerateFiles(DirectoryPath(scope), "*.json").OrderBy(File.GetLastWriteTimeUtc))
            {
                try {
                var pending = JsonSerializer.Deserialize<PendingWorkbookOperation>(File.ReadAllText(path));
                if (pending != null && pending.Scope == scope && Path.GetFullPath(path) == Path.GetFullPath(Manifest(scope, pending.OperationId))) return pending;
                } catch { /* One damaged manifest must not hide the other pending operations. */ }
            }
            return null;
        }
        catch { return null; }
    }
    public static void Remove(string scope, string operationId)
    {
        var manifest = Manifest(scope, operationId); if (File.Exists(manifest)) File.Delete(manifest);
    }
    public static void Save(PendingWorkbookOperation operation)
    {
        var manifest = Manifest(operation.Scope, operation.OperationId); Directory.CreateDirectory(DirectoryPath(operation.Scope));
        if (File.Exists(manifest))
        {
            var existing = JsonSerializer.Deserialize<PendingWorkbookOperation>(File.ReadAllText(manifest));
            if (existing != operation) throw new InvalidDataException("该操作已有不同内容，不能覆盖待重试记录。");
            return;
        }
        var temporary = Path.Combine(DirectoryPath(operation.Scope), Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(operation)); File.Move(temporary, manifest); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<PendingWorkbookOperation> ForImportAsync(IProjectWorkbookService service, string projectId, string path, string? revisionOf)
    {
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("仅接收 .xlsx 文件。");
        var scope = service.OperationScope;
        await using var source = File.OpenRead(path);
        if (source.Length > service.MaximumUploadBytes) throw new InvalidDataException("文件超过宿主配置的上传上限。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(source, deadline.Token));
        if (scope != service.OperationScope) throw new InvalidDataException("账号已切换，请重新发起导入。");
        return new(scope, Guid.NewGuid().ToString("D"), projectId, null, Path.GetFullPath(path), digest, revisionOf);
    }
    public async Task<ProjectFile> SubmitAsync(IProjectWorkbookService service)
    {
        if (Scope != service.OperationScope) throw new InvalidDataException("该待重试操作属于不同服务器或账号，请切回原账号后处理。");
        if (TemplateKind != null) return await service.CreateInProjectAsync(ProjectId, TemplateKind, OperationId);
        if (SourcePath == null || SourceSha256 == null) throw new InvalidDataException("待重试记录缺少原文件信息，请检查项目列表后取消此操作。");
        await using var source = File.OpenRead(SourcePath);
        if (source.Length > service.MaximumUploadBytes) throw new InvalidDataException("文件超过宿主配置的上传上限。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(source, deadline.Token));
        if (!string.Equals(digest, SourceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("原文件已改变，不能重试这次提交。请重新进入项目确认结果后取消此操作。");
        source.Position = 0;
        if (Scope != service.OperationScope) throw new InvalidDataException("账号已切换，已阻止续传。");
        return await service.ImportRevisionAsync(ProjectId, Path.GetFileName(SourcePath), source, OperationId, RevisionOf);
    }
}

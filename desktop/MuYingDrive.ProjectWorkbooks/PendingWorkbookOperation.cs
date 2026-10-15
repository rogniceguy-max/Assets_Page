using System.IO;
using System.Text.Json;

namespace MuYingDrive.ProjectWorkbooks;

// Stores only pending template creation requests; no credentials or workbook file contents.
public sealed record PendingWorkbookOperation(string Scope, string OperationId, string ProjectId,
    string? TemplateKind, string? Name = null)
{
    public string Description => !string.IsNullOrWhiteSpace(Name)
        ? $"新建 {Name}"
        : TemplateKind == "scene_beats" ? "新建节拍表" : "新建资产表";

    private static string DirectoryPath(string scope)
    {
        if (scope.Length != 64 || scope.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("待重试操作的服务器/账号范围无效。");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuYingDrive", "ProjectWorkbooks", "pending-operations", scope);
    }

    private static string Manifest(string scope, string operationId)
    {
        if (!Guid.TryParseExact(operationId, "D", out _))
            throw new InvalidDataException("待处理操作标识无效。");
        return Path.Combine(DirectoryPath(scope), operationId + ".json");
    }

    private static bool IsTemplateKind(string? value) => value is "scene_beats" or "asset_review";

    public static PendingWorkbookOperation? Load(string scope)
    {
        try
        {
            var directory = DirectoryPath(scope);
            if (!Directory.Exists(directory)) return null;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var pending = JsonSerializer.Deserialize<PendingWorkbookOperation>(File.ReadAllText(path));
                    if (pending == null || pending.Scope != scope
                        || Path.GetFullPath(path) != Path.GetFullPath(Manifest(scope, pending.OperationId)))
                        continue;

                    if (!IsTemplateKind(pending.TemplateKind))
                    {
                        File.Delete(path); // Discard unsupported or obsolete operation records.
                        continue;
                    }
                    return pending;
                }
                catch { /* One stale or damaged manifest must not hide other pending creates. */ }
            }
            return null;
        }
        catch { return null; }
    }

    public static void Remove(string scope, string operationId)
    {
        var manifest = Manifest(scope, operationId);
        if (File.Exists(manifest)) File.Delete(manifest);
    }

    public static void Save(PendingWorkbookOperation operation)
    {
        if (!IsTemplateKind(operation.TemplateKind))
            throw new InvalidDataException("只允许保存工作簿模板创建操作。");
        var manifest = Manifest(operation.Scope, operation.OperationId);
        Directory.CreateDirectory(DirectoryPath(operation.Scope));
        if (File.Exists(manifest))
        {
            var existing = JsonSerializer.Deserialize<PendingWorkbookOperation>(File.ReadAllText(manifest));
            if (existing != operation)
                throw new InvalidDataException("该操作已有不同内容，不能覆盖待重试记录。");
            return;
        }

        var temporary = Path.Combine(DirectoryPath(operation.Scope), Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(operation));
            File.Move(temporary, manifest);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<ProjectFile> SubmitAsync(IProjectWorkbookService service)
    {
        if (Scope != service.OperationScope)
            throw new InvalidDataException("该待重试操作属于不同服务器或账号，请切回原账号后处理。");
        var templateKind = TemplateKind;
        if (!IsTemplateKind(templateKind))
            throw new InvalidDataException("待重试操作类型无效，请取消后重新发起。");
        return await service.CreateInProjectAsync(ProjectId, templateKind!, OperationId, Name);
    }
}

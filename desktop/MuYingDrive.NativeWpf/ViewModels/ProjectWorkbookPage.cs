using System.Net.Http;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.ProjectWorkbooks;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>
/// The project-workbook page as one disposable unit: its HTTP client, its service and its
/// view model.
/// </summary>
/// <remarks>
/// The three are inseparable — the view model holds the service and the service holds the
/// client — and the client is created per sign-in rather than shared. The workbook library
/// scopes its retry records to a hash of the server origin and the signed-in account's stable
/// id, so reusing a page across a sign-out would let one person's pending operation be
/// resumed by the next. Signing out disposes this, and signing in builds a new one.
/// </remarks>
public sealed class ProjectWorkbookPage : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpProjectWorkbookService _service;

    public ProjectWorkbookPage(V2ApiClient api, string sessionToken, long accountId)
    {
        _http = api.CreateProjectWorkbookClient();
        _service = new HttpProjectWorkbookService(
            _http,
            api.ProjectWorkbookRoot,
            () => sessionToken,
            new WorkbookTransportOptions { MaxUploadBytes = 64L * 1024 * 1024 },
            // Required even for reading: the library computes its operation scope from the
            // account id and refuses to construct a service without it.
            () => accountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ViewModel = new ProjectWorkbooksViewModel(_service);
    }

    public ProjectWorkbooksViewModel ViewModel { get; }

    /// <summary>
    /// The projects and their workbooks, for the sidebar tree.
    /// </summary>
    /// <remarks>
    /// Delegated rather than re-implemented over a second HTTP client: the library already
    /// knows these two routes, the JSON shape and the pinned certificate, and a parallel
    /// implementation is how the tree and the page start disagreeing about what a project is.
    /// </remarks>
    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        _service.ListProjectsAsync(cancellationToken);

    public Task<IReadOnlyList<ProjectFile>> ListProjectFilesAsync(
        string projectId, CancellationToken cancellationToken = default) =>
        _service.ListProjectFilesAsync(projectId, cancellationToken);

    public Action? ProjectFilesChanged
    {
        get => ViewModel.ProjectFilesChanged;
        set => ViewModel.ProjectFilesChanged = value;
    }

    /// <summary>
    /// Opens one workbook by id, the way the 项目 tree asks for it.
    /// </summary>
    /// <remarks>
    /// The tree knows the project and the file; this page owns the list the file objects come
    /// from, so the lookup happens here rather than the tree being handed a second, parallel
    /// idea of what a file is. Both ids come from the backend, so neither depends on a
    /// display name — which is the point, because a project can be renamed and this has to
    /// keep working when it is.
    /// </remarks>
    public Task OpenWorkbookAsync(string projectId, string fileId, string? fileName = null, Func<Task>? onNotFound = null) =>
        ViewModel.ExecuteAsync(async () =>
        {
            // The page loads its lists lazily, and opening a workbook from the sidebar is
            // allowed to be the first thing that ever happens to it. Without this the lookup
            // below runs against an empty list: the first click failed and the second worked,
            // which reads as "clicking twice fixes it" rather than as a race.
            if (ViewModel.Projects.Count == 0) await ViewModel.InitializeAsync();

            var project = ViewModel.Projects.FirstOrDefault(p => p.Id == projectId)
                ?? throw new InvalidOperationException("找不到该项目，可能已被删除。");
            if (!ReferenceEquals(project, ViewModel.SelectedProject))
            {
                ViewModel.SelectedProject = project;
                await ViewModel.RefreshFilesAsync();
            }

            var file = ViewModel.Files.FirstOrDefault(f => f.Id == fileId);
            if (file == null)
            {
                await ViewModel.RefreshFilesAsync();
                file = ViewModel.Files.FirstOrDefault(f => f.Id == fileId);
            }
            if (file == null && !string.IsNullOrWhiteSpace(fileName))
            {
                file = ViewModel.Files.FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
            }
            if (file == null)
            {
                if (onNotFound != null) await onNotFound();
                throw new InvalidOperationException("该工作簿在 NAS 上已不存在或已被改名，已刷新列表。");
            }

            try
            {
                await ViewModel.OpenAsync(file);
            }
            catch (Exception)
            {
                if (onNotFound != null) await onNotFound();
                throw;
            }
        });

    /// <summary>
    /// Opens the project's workbook home view (showing workbook list and creation options).
    /// </summary>
    public Task OpenProjectHomeAsync(string projectId) =>
        ViewModel.ExecuteAsync(async () =>
        {
            if (ViewModel.Projects.Count == 0) await ViewModel.InitializeAsync();

            var project = ViewModel.Projects.FirstOrDefault(p => p.Id == projectId)
                ?? throw new InvalidOperationException("找不到该项目，可能已被删除。");
            if (!ReferenceEquals(project, ViewModel.SelectedProject))
            {
                ViewModel.SelectedProject = project;
            }
            await ViewModel.RefreshFilesAsync();
            ViewModel.Back();
        });

    /// <summary>
    /// Opens a workbook by its filename within a project (used when double clicking a file in browser).
    /// </summary>
    public Task OpenWorkbookByNameAsync(string projectId, string fileName) =>
        ViewModel.ExecuteAsync(async () =>
        {
            if (ViewModel.Projects.Count == 0) await ViewModel.InitializeAsync();

            var project = ViewModel.Projects.FirstOrDefault(p => p.Id == projectId)
                ?? throw new InvalidOperationException("找不到该项目，可能已被删除。");
            if (!ReferenceEquals(project, ViewModel.SelectedProject))
            {
                ViewModel.SelectedProject = project;
            }
            await ViewModel.RefreshFilesAsync();

            var file = ViewModel.Files.FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"该项目里没有找到工作簿「{fileName}」。");
            await ViewModel.OpenAsync(file);
        });

    public void Dispose()
    {
        ViewModel.StopLiveUpdates();
        // Disposing the client cancels anything still in flight for this account.
        _http.Dispose();
    }
}

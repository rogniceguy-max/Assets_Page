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
    public Task OpenWorkbookAsync(string projectId, string fileId) =>
        ViewModel.ExecuteAsync(async () =>
        {
            var project = ViewModel.Projects.FirstOrDefault(p => p.Id == projectId)
                ?? throw new InvalidOperationException("找不到该项目，可能已被删除。");
            if (!ReferenceEquals(project, ViewModel.SelectedProject))
            {
                ViewModel.SelectedProject = project;
                await ViewModel.RefreshFilesAsync();
            }

            var file = ViewModel.Files.FirstOrDefault(f => f.Id == fileId)
                ?? throw new InvalidOperationException("该项目里没有这个工作簿，可能已被删除。");
            await ViewModel.OpenAsync(file);
        });

    public void Dispose()
    {
        // Disposing the client cancels anything still in flight for this account.
        _http.Dispose();
    }
}

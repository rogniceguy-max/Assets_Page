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

    public ProjectWorkbookPage(V2ApiClient api, string sessionToken, long accountId)
    {
        _http = api.CreateProjectWorkbookClient();
        var service = new HttpProjectWorkbookService(
            _http,
            api.ProjectWorkbookRoot,
            () => sessionToken,
            new WorkbookTransportOptions { MaxUploadBytes = 64L * 1024 * 1024 },
            // Required even for reading: the library computes its operation scope from the
            // account id and refuses to construct a service without it.
            () => accountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ViewModel = new ProjectWorkbooksViewModel(service);
    }

    public ProjectWorkbooksViewModel ViewModel { get; }

    public void Dispose()
    {
        // Disposing the client cancels anything still in flight for this account.
        _http.Dispose();
    }
}

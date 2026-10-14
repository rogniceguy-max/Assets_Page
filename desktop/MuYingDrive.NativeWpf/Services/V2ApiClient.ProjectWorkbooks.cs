using System.Net.Http;
namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// The pinned HTTP client the project-workbook library is handed.
/// </summary>
/// <remarks>
/// A partial of <see cref="V2ApiClient"/> rather than a class of its own, for the same reason
/// the feedback and visitor endpoints are: it needs the certificate pin and the validation
/// callback that already live here. Building a separate client elsewhere would mean a second
/// copy of the pinning rule, which is exactly the drift this codebase has been bitten by
/// before — and a copy that got it wrong would fail open.
///
/// The library takes a raw <see cref="HttpClient"/> and forbids reflection into this one, so
/// this is the seam that exists rather than the one that would be tidiest.
/// </remarks>
public sealed partial class V2ApiClient
{
    /// <summary>
    /// A client for the workbook endpoints: the same certificate pinning, and no overall
    /// timeout.
    /// </summary>
    /// <remarks>
    /// The infinite timeout is deliberate. This client's default is 30 seconds, and the
    /// workbook library sets its own deadlines per request — 90 seconds for a JSON call, 180
    /// for an upload or download. A 30-second ceiling on the shared client would abort every
    /// one of those before the library's own limit was reached, and the failure would look
    /// like a network problem.
    ///
    /// Callers own the result and must dispose it.
    /// </remarks>
    public HttpClient CreateProjectWorkbookClient() => new(
        new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = ValidateServerCertificate
        },
        disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>The absolute root the workbook endpoints live under.</summary>
    public Uri ProjectWorkbookRoot => new(_baseUri, "api/v2/project-workbooks/");
}

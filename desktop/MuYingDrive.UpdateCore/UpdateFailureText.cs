using System.Net.Http;

namespace MuYingDrive.UpdateCore;

/// <summary>
/// Wording for the update failures a person can actually act on.
/// </summary>
/// <remarks>
/// Shared by the setup executable and the in-app updater so that the same failure reads
/// the same way wherever it surfaces, and so the decide-which-cause logic below can be
/// tested without a network.
/// </remarks>
public static class UpdateFailureText
{
    /// <summary>
    /// .NET surfaces a response body that stops part-way as <c>HttpIOException</c>, whose
    /// Message is raw English: "The response ended prematurely, with at least 71220830
    /// additional bytes expected." To anyone installing, that reads like a crash rather
    /// than a dropped link.
    /// </summary>
    public const string DownloadInterrupted = "安装包下载中断，请检查网络后重试。";

    /// <summary>
    /// Explains a connection failure in terms of its likely cause.
    /// </summary>
    /// <remarks>
    /// A certificate-pin mismatch fails inside the TLS handshake, which surfaces as an
    /// <see cref="HttpRequestException"/> exactly as an unreachable host does. Reporting
    /// both as "check your network" sends the reader hunting for a cabling problem while
    /// the real cause is a rotated server certificate or something impersonating the
    /// server. Those two situations want opposite responses, so they get different text.
    /// </remarks>
    public static string DescribeConnectionFailure(
        HttpRequestException exception, string prefix = "无法连接更新服务器")
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.HttpRequestError == HttpRequestError.SecureConnectionError
            ? "无法与服务器建立安全连接：内置的证书校验未通过。服务器证书可能已被更换，"
              + "或当前连接正在被冒充。请联系管理员后再继续。"
            : $"{prefix}，请检查网络或联系管理员。";
    }
}

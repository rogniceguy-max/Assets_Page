using System.Text.Json;
using System.Text.Json.Nodes;

namespace MuYingDrive.UpdateCore;

/// <summary>
/// Refreshes the connection fields of an existing <c>nas-client.json</c>.
/// </summary>
/// <remarks>
/// The file carries a <c>CredentialProfile</c> naming which Credential Manager entries
/// hold the session token and SMB password, so a reinstall must never replace it
/// wholesale with whatever the installer was built with: that would point the client at a
/// different profile and silently drop the user's stored login, forcing them to sign in
/// again.
///
/// The connection fields are the opposite case. They hold the NAS address and the TLS
/// pin, and they are the one thing a new installer carries that the update channel cannot
/// deliver — <c>nas-client.json</c> is not part of any update package and is never
/// rewritten by one. If a rotated server certificate never reaches this file, every
/// already-installed client keeps pinning the old certificate and can never update again,
/// with no way out short of handing everyone a new installer.
/// </remarks>
public static class ClientConfigurationMerge
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Rewrites the address, pin and share root of an existing configuration, leaving
    /// every other field — <c>CredentialProfile</c> included — exactly as it was.
    /// </summary>
    /// <param name="updatedJson">
    /// The text to write. Identical to <paramref name="existingJson"/> when nothing
    /// changed, so the caller can skip the write entirely.
    /// </param>
    /// <returns>
    /// False when the existing text is not a JSON object. The caller should overwrite it
    /// in that case: a file that cannot be parsed supplies no credential profile either,
    /// and the client would reject it at every start.
    /// </returns>
    public static bool TryRefreshConnectionFields(
        string existingJson, string apiBaseUri, string certificateSha256, string shareRoot,
        out string updatedJson)
    {
        updatedJson = existingJson;
        if (string.IsNullOrWhiteSpace(existingJson)) return false;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(existingJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is not JsonObject document) return false;

        // Deliberately not short-circuiting: every field is compared so a change to any of
        // them is noticed, and || would skip the later comparisons once one already differs.
        var changed =
            SetField(document, "ApiBaseUri", apiBaseUri) |
            SetField(document, "CertificateSha256", certificateSha256) |
            SetField(document, "ShareRoot", shareRoot);

        if (changed) updatedJson = document.ToJsonString(WriteOptions);
        return true;
    }

    private static bool SetField(JsonObject document, string name, string value)
    {
        if (document[name] is JsonValue current && current.TryGetValue<string>(out var text) &&
            string.Equals(text, value, StringComparison.Ordinal))
        {
            return false;
        }

        document[name] = value;
        return true;
    }
}

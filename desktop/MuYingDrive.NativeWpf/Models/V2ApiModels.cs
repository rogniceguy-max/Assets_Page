using System.Text.Json.Serialization;

namespace MuYingDrive.NativeWpf.Models;

public sealed record V2CredentialsRequest(
    [property: JsonPropertyName("username")] string UserName,
    [property: JsonPropertyName("password")] string Password);

public sealed record V2RegistrationRequest(
    [property: JsonPropertyName("username")] string UserName,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("group_type")] string GroupType);

public sealed class V2AuthenticationResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("session_token")]
    public string SessionToken { get; init; } = string.Empty;

    [JsonPropertyName("user")]
    public V2UserSession User { get; init; } = new();
}

public sealed class V2SessionResponse
{
    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; init; }

    [JsonPropertyName("user")]
    public V2UserSession User { get; init; } = new();
}

/// <summary>Authenticated user and share paths returned by the V2 API.</summary>
public sealed class V2UserSession
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("username")]
    public string UserName { get; init; } = string.Empty;

    [JsonPropertyName("department")]
    public string Department { get; init; } = string.Empty;

    [JsonPropertyName("group_type")]
    public string GroupType { get; init; } = string.Empty;

    [JsonPropertyName("spaces")]
    public Dictionary<string, string> Spaces { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("smb_username")]
    public string? ExplicitSmbUserName { get; init; }

    /// <summary>
    /// Internal Samba identity encoded in the server-provided personal path.
    /// The display name remains <see cref="UserName"/>.
    /// </summary>
    [JsonIgnore]
    public string SmbUserName
    {
        get
        {
            if (ExplicitSmbUserName is not null)
            {
                if (string.IsNullOrWhiteSpace(ExplicitSmbUserName) || ExplicitSmbUserName.Length > 255 ||
                    ExplicitSmbUserName.Any(char.IsControl) || ExplicitSmbUserName.Contains('/') || ExplicitSmbUserName.Contains('\\'))
                    throw new InvalidDataException("The V2 API returned an invalid SMB identity.");
                return ExplicitSmbUserName;
            }

            const string personalPrefix = "/03_个人空间/";
            if (!Spaces.TryGetValue("personal", out var personalPath) ||
                !personalPath.StartsWith(personalPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The V2 API did not return a valid personal-space identity.");

            var candidate = personalPath[personalPrefix.Length..];
            if (candidate.Length != 29 || !candidate.StartsWith("myv2_", StringComparison.Ordinal) ||
                candidate.AsSpan(5).IndexOfAnyExcept("0123456789abcdef") >= 0)
                throw new InvalidDataException("The V2 API returned an invalid internal SMB identity.");

            return candidate;
        }
    }
}

internal sealed class V2ApiErrorResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed class V2LogoutResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }
}

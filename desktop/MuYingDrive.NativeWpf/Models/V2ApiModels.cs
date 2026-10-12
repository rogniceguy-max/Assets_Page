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

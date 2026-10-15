using System.Text.Json.Serialization;

namespace MuYingDrive.NativeWpf.Models;

/// <summary>Response of the login-free visitor session endpoint.</summary>
public sealed class VisitorSessionResponse
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("session_token")]
    public string SessionToken { get; init; } = string.Empty;
}

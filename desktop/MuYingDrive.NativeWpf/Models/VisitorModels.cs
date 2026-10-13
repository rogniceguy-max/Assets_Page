using System.Text.Json.Serialization;

namespace MuYingDrive.NativeWpf.Models;

/// <summary>One read-only space offered to a visitor.</summary>
public sealed class VisitorSpace
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
}

/// <summary>Response of the login-free visitor session endpoint.</summary>
public sealed class VisitorSessionResponse
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("session_token")]
    public string SessionToken { get; init; } = string.Empty;

    [JsonPropertyName("spaces")]
    public List<VisitorSpace> Spaces { get; init; } = [];

    [JsonPropertyName("capabilities")]
    public VisitorCapabilities Capabilities { get; init; } = new();
}

/// <summary>
/// What a visitor is allowed to do. Read-only by contract; the client uses this to
/// decide whether to offer any write affordance at all.
/// </summary>
public sealed class VisitorCapabilities
{
    [JsonPropertyName("read")]
    public bool Read { get; init; }

    [JsonPropertyName("write")]
    public bool Write { get; init; }
}

/// <summary>A directory entry returned by the visitor channel.</summary>
public sealed class VisitorEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("modifiedUtc")]
    public long ModifiedUtc { get; init; }
}

/// <summary>One listing response, including the path it describes.</summary>
public sealed class VisitorEntriesResponse
{
    [JsonPropertyName("space")]
    public string Space { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<VisitorEntry> Entries { get; init; } = [];

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}

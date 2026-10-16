using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuYingDrive.NativeWpf.Models;

public sealed record TransferAgentTaskSnapshot
{
    [JsonPropertyName("task_id")] public string TaskId { get; init; } = string.Empty;
    [JsonPropertyName("operation_id")] public string OperationId { get; init; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("root_id")] public string? RootId { get; init; }
    [JsonPropertyName("source_path")] public string? SourcePath { get; init; }
    [JsonPropertyName("destination_path")] public string? DestinationPath { get; init; }
    [JsonPropertyName("scope_id")] public string? ScopeId { get; init; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    [JsonPropertyName("commit_started")] public bool CommitStarted { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("folder_group")] public string? FolderGroup { get; init; }
    [JsonPropertyName("logical_path")] public string? LogicalPath { get; init; }
    [JsonPropertyName("bytes_total")] public long? BytesTotal { get; init; }
    [JsonPropertyName("bytes_done")] public long BytesDone { get; init; }
    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(UnixEpochSecondsDateTimeOffsetConverter))]
    public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")]
    [JsonConverter(typeof(UnixEpochSecondsDateTimeOffsetConverter))]
    public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("attempt")] public int Attempt { get; init; }
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("file_id")] public string? FileId { get; init; }
    [JsonPropertyName("revision_id")] public string? RevisionId { get; init; }
    [JsonPropertyName("object_id")] public string? ObjectId { get; init; }
    [JsonPropertyName("expected_version")] public long? ExpectedVersion { get; init; }
    [JsonPropertyName("version")] public long? Version { get; init; }
    [JsonPropertyName("items_total")] public int? ItemsTotal { get; init; }
    [JsonPropertyName("items_done")] public int? ItemsDone { get; init; }
}

public sealed class TransferAgentTaskListResponse
{
    [JsonPropertyName("tasks")] public List<TransferAgentTaskSnapshot> Tasks { get; init; } = [];
}

public sealed class UnixEpochSecondsDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException exception) { throw new JsonException("Agent timestamp is outside the supported range.", exception); }
        }

        if (reader.TokenType == JsonTokenType.String &&
            DateTimeOffset.TryParse(reader.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            return parsed;

        throw new JsonException("Agent timestamp must be Unix epoch seconds or an ISO 8601 string.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.ToUnixTimeSeconds());
}

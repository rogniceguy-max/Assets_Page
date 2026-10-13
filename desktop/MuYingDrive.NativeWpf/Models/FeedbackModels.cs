using System.Text.Json.Serialization;
using System.Windows.Media;

namespace MuYingDrive.NativeWpf.Models;

public sealed record FeedbackAttachment(
    [property: JsonPropertyName("attachment_id")] string AttachmentId,
    string Mime, int Width, int Height, int Size);

public sealed record FeedbackItem(
    [property: JsonPropertyName("feedback_id")] string FeedbackId,
    string Kind, string Category, string Title, string Description, string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    IReadOnlyList<FeedbackAttachment> Attachments)
{
    public string KindLabel => Kind == "problem" ? "问题反馈" : "功能建议";
    public string StatusLabel => FeedbackLabels.Status(Status);
    public string Summary => $"{KindLabel} · {UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}" + (Attachments.Count > 0 ? " · 有截图" : "");
}

public sealed record FeedbackPage(IReadOnlyList<FeedbackItem> Items, int Page,
    [property: JsonPropertyName("has_more")] bool HasMore);
public sealed record FeedbackCapabilities([property: JsonPropertyName("can_manage")] bool CanManage);
public sealed record FeedbackCreated([property: JsonPropertyName("feedback_id")] string FeedbackId,
    [property: JsonPropertyName("pending_review")] bool PendingReview);
public sealed record FeedbackImagePayload(string Mime, string Data);
public sealed record FeedbackSubmission(string Kind, string Category, string Title, string Description,
    IReadOnlyList<FeedbackImagePayload> Images,
    [property: JsonPropertyName("privacy_confirmed")] bool PrivacyConfirmed);
public sealed record FeedbackChoice(string Value, string Label)
{
    public override string ToString() => Label;
}
public sealed record FeedbackPreview(ImageSource Image, byte[] Bytes, string Mime = "image/png");

public static class FeedbackLabels
{
    public static string Status(string value) => value switch
    {
        "pending" => "待处理", "in_progress" => "处理中", "resolved" => "已解决",
        "evaluating" => "待评估", "planned" => "计划中", "accepted" => "已采纳",
        "declined" => "暂不考虑", _ => "未知状态"
    };
}

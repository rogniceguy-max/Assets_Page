using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;

namespace MuYingDrive.ProjectWorkbooks;

public static class ReviewText
{
    public static string Validate(string text)
    {
        text = (text ?? "").Trim();
        int count = 0;
        for (int i = 0; i < text.Length; i++, count++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) throw new InvalidDataException("评论含无效字符。");
                i++;
            }
            else if (char.IsLowSurrogate(text[i])) throw new InvalidDataException("评论含无效字符。");
        }
        if (count is < 1 or > 4000) throw new InvalidDataException("评论需为 1–4000 个字符，不能只有空格。");
        return text;
    }
}

// Target is immutable for this window. Row-general discussion never follows the selected image.
public sealed class ReviewViewModel(IProjectWorkbookService service, string fileId, string rowId, ImageItem? image, string rowKind = "asset-rows") : ObservableModel
{
    public string FileId { get; } = fileId;
    public string RowId { get; } = rowId;
    public string RowKind { get; } = rowKind is "asset-rows" or "scene-rows" ? rowKind : throw new ArgumentException("不支持的行类型。");
    public string? ImageId { get; } = image?.Asset.Id;
    public string Title => image?.Name ?? (RowKind == "scene-rows" ? "批注" : "资产批注");
    public bool IsImage => ImageId != null;
    public ObservableCollection<ReviewComment> Comments { get; } = [];
    private bool _busy;
    public bool Busy { get => _busy; private set { _busy = value; Changed(); Changed(nameof(Ready)); } }
    public bool Ready => !Busy;
    private string? _error;
    public string? Error { get => _error; private set { _error = value; Changed(); } }
    private string? _imageError;
    public string? ImageError { get => _imageError; private set { _imageError = value; Changed(); Changed(nameof(CanReload)); } }
    private ImageSource? _preview = image?.Preview;
    public ImageSource? Preview { get => _preview; private set { _preview = value; Changed(); } }
    private ReviewThread? _thread;
    public string SummaryText => _thread is { } t ? IsImage ? $"好 {t.Good} · 不好 {t.Bad} · 批注 {t.CommentCount}" : $"批注 {t.CommentCount}" : "统计暂不可用";
    /// <summary>An empty list once the thread is known, never a claim made before it is read.</summary>
    public bool HasNoComments => _thread != null && Comments.Count == 0;
    public string MyVerdictText => _thread == null ? "评价状态未知" : _thread.MyVerdict switch { "good" => "我的评价：好", "bad" => "我的评价：不好", _ => "尚未评价" };
    private string _draft = "";
    public string Draft { get => _draft; set { _draft = value; Changed(); } }
    private ReviewComment? _editing;
    public bool IsEditing => _editing != null;
    public string ComposerTitle => IsEditing ? "编辑批注" : "添加批注";
    public string SendLabel => IsEditing ? "保存" : "发送";
    private bool _mustRefresh;
    public bool CanReload => _mustRefresh || ImageError != null;
    private bool _editingNeedsReselect;
    private bool _loadedThread;
    private string _refreshNotice = "";
    public string RefreshNotice { get => _refreshNotice; private set { _refreshNotice = value; Changed(); } }
    private readonly Dictionary<string, string> _resolutionDrafts = new(StringComparer.Ordinal);
    public bool HasDraft => !string.IsNullOrWhiteSpace(Draft) || _resolutionDrafts.Count > 0;
    public string ResolutionDraft(ReviewComment comment) => _resolutionDrafts.GetValueOrDefault(comment.Id) ?? comment.ResolutionNote ?? "";

    public async Task ExecuteAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; Error = null;
        try { await action(); }
        catch (WorkbookApiException ex) when (ex.StatusCode == 409)
        { _mustRefresh = true; Error = "评论已变更，输入已保留。请重新加载并确认后再操作。"; }
        catch (OperationCanceledException)
        { _mustRefresh = true; Error = "请求取消或超时。请重新加载确认结果，避免重复提交。"; }
        catch (InvalidDataException ex) { if (_thread == null) _mustRefresh = true; Error = ex.Message; }
        catch (Exception ex)
        { _mustRefresh = true; Error = ex is WorkbookApiException or InvalidDataException ? ex.Message : "操作未完成，请重新加载确认结果。"; }
        finally { Busy = false; Changed(nameof(CanReload)); }
    }
    private void Apply(ReviewThread thread)
    {
        if (thread.ImageId != ImageId) throw new InvalidDataException("接口返回了不同的讨论目标，已拒绝显示。");
        _thread = thread; Comments.Clear();
        foreach (var comment in thread.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal)) Comments.Add(comment);
        Changed(nameof(SummaryText)); Changed(nameof(MyVerdictText)); Changed(nameof(HasNoComments));
    }
    public async Task RefreshAsync()
    {
        // A failed refresh leaves previous data explicitly stale; counters are not invented as zero.
        var previousIds = Comments.Select(comment => comment.Id).ToHashSet(StringComparer.Ordinal);
        _thread = null; Changed(nameof(SummaryText)); Changed(nameof(MyVerdictText)); Changed(nameof(HasNoComments));
        Apply(await service.GetReviewThreadForRowAsync(FileId, RowId, RowKind, ImageId));
        var newCount = Comments.Count(comment => !previousIds.Contains(comment.Id));
        RefreshNotice = _loadedThread && newCount > 0 ? $"新增 {newCount} 条评论。" : "";
        _loadedThread = true;
        _mustRefresh = false;
        if (_editing != null)
        {
            _editingNeedsReselect = true;
            Error = "草稿已保留。请重新点击该评论的编辑按钮，确认后再保存。";
        }
        if (ImageId != null)
        {
            try { Preview = ProjectWorkbooksViewModel.Decode(await service.GetImageContentAsync(ImageId, false)); ImageError = null; }
            catch { ImageError = "大图读取失败，可重新加载。"; }
        }
    }
    private void EnsureFresh()
    {
        if (_mustRefresh || _thread == null) throw new InvalidDataException("请先重新加载讨论。");
    }
    public async Task VoteAsync(string? verdict)
    {
        EnsureFresh();
        if (ImageId == null || RowKind != "asset-rows") throw new InvalidDataException("整条记录和节拍讨论不提供图片评价。");
        Apply(await service.VoteAsync(FileId, RowId, new ReviewVoteRequest(ImageId, verdict)));
    }
    public void BeginEdit(ReviewComment comment)
    {
        if (!comment.CanEdit || Busy) return;
        var sameTarget = _editing?.Id == comment.Id;
        _editing = comment;
        if (!sameTarget) Draft = comment.Text;
        _editingNeedsReselect = false; Error = null; ComposerChanged();
    }
    public bool WouldReplaceDraft(ReviewComment comment) => !string.IsNullOrWhiteSpace(Draft) && _editing?.Id != comment.Id && Draft != comment.Text;
    public void CancelEdit(bool keepDraft = false)
    {
        _editing = null; _editingNeedsReselect = false; if (!keepDraft) Draft = ""; ComposerChanged();
    }
    private void ComposerChanged() { Changed(nameof(IsEditing)); Changed(nameof(ComposerTitle)); Changed(nameof(SendLabel)); }
    public async Task SendAsync()
    {
        EnsureFresh(); var text = ReviewText.Validate(Draft);
        ReviewThread updated;
        if (_editing is { } comment)
        {
            if (_editingNeedsReselect) throw new InvalidDataException("请重新点击目标评论的编辑按钮，确认采用最新版本；当前草稿仍保留。");
            if (!comment.CanEdit) throw new InvalidDataException("只能编辑自己的评论。");
            updated = await service.EditCommentForRowAsync(FileId, RowId, RowKind, comment.Id, new EditReviewCommentRequest(comment.Version, text));
        }
        else updated = await service.AddCommentForRowAsync(FileId, RowId, RowKind, new AddReviewCommentRequest(ImageId, text));
        Apply(updated); CancelEdit();
    }
    public async Task DeleteAsync(ReviewComment comment)
    {
        EnsureFresh(); if (!comment.CanDelete) throw new InvalidDataException("只能删除自己的评论。");
        Apply(await service.DeleteCommentForRowAsync(FileId, RowId, RowKind, comment.Id, comment.Version));
        if (_editing?.Id == comment.Id) CancelEdit();
    }
    public async Task ResolveAsync(ReviewComment comment, bool resolved, string? note)
    {
        if (resolved && note != null) _resolutionDrafts[comment.Id] = note;
        EnsureFresh(); if (!comment.CanResolve) throw new InvalidDataException("当前账号没有处理此意见的权限。");
        if (resolved) note = ReviewText.Validate(note ?? "");
        Apply(await service.ResolveCommentAsync(FileId, RowId, RowKind, comment.Id, new ResolveCommentRequest(comment.Version, resolved, note)));
        _resolutionDrafts.Remove(comment.Id);
    }
}

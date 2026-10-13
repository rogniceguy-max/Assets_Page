using System.Collections.ObjectModel;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class FeedbackDraftViewModel : ObservableObject
{
    private readonly V2ApiClient _api;
    private readonly string _token;
    private readonly CancellationToken _ct;
    private string _kind = "problem", _category = "general", _title = "", _description = "", _error = "";
    private bool _confirmed, _busy, _adding;

    public FeedbackDraftViewModel(V2ApiClient api, string token, CancellationToken ct)
    {
        _api = api; _token = token; _ct = ct;
        SubmitCommand = new AsyncRelayCommand(_ => SubmitAsync(), _ => CanSubmit);
        RemoveImageCommand = new RelayCommand(p => { if (p is FeedbackPreview image) Images.Remove(image); }, _ => !IsBusy);
    }

    public event EventHandler<FeedbackCreated>? Submitted;
    public ObservableCollection<FeedbackPreview> Images { get; } = [];
    public IReadOnlyList<FeedbackChoice> KindChoices { get; } = [new("problem", "问题反馈"), new("suggestion", "功能建议")];
    public IReadOnlyList<FeedbackChoice> Categories { get; } = [new("general", "其他"), new("upload", "上传"), new("download", "下载"), new("permission", "访问权限"), new("display", "文件显示"), new("interaction", "交互体验")];
    public string Kind { get => _kind; set { SetProperty(ref _kind, value); OnPropertyChanged(nameof(DescriptionHint)); } }
    public string Category { get => _category; set => SetProperty(ref _category, value); }
    public string Title { get => _title; set { SetProperty(ref _title, value); SubmitCommand.RaiseCanExecuteChanged(); } }
    public string Description { get => _description; set { SetProperty(ref _description, value); SubmitCommand.RaiseCanExecuteChanged(); } }
    public string Error { get => _error; set => SetProperty(ref _error, value); }
    public string DescriptionHint => Kind == "suggestion" ? "描述使用场景、期望功能和预期收益" : "描述发生经过、复现步骤和预期结果";
    public bool PrivacyConfirmed { get => _confirmed; set { SetProperty(ref _confirmed, value); SubmitCommand.RaiseCanExecuteChanged(); } }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(IsEditable)); SubmitCommand.RaiseCanExecuteChanged(); RemoveImageCommand.RaiseCanExecuteChanged(); } }
    public bool IsAdding { get => _adding; set { SetProperty(ref _adding, value); OnPropertyChanged(nameof(IsEditable)); SubmitCommand.RaiseCanExecuteChanged(); } }
    public bool IsEditable => !IsBusy && !IsAdding;
    public bool CanSubmit => IsEditable && PrivacyConfirmed && !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Description);
    public AsyncRelayCommand SubmitCommand { get; }
    public RelayCommand RemoveImageCommand { get; }

    public void AddImage(FeedbackPreview preview)
    {
        if (IsBusy) return;
        if (Images.Count >= 4 || preview.Bytes.Length > FeedbackImageService.MaxBytes || Images.Sum(i => (long)i.Bytes.Length) + preview.Bytes.Length > 6 * 1024 * 1024)
        { Error = "最多四张截图，每张 2 MiB，总计 6 MiB。"; return; }
        Images.Add(preview); PrivacyConfirmed = false; Error = "";
    }

    private async Task SubmitAsync()
    {
        if (!CanSubmit) return;

        // The server collapses all of these into one generic "反馈内容无效" that names no
        // field, so they are checked here first. The trimmed values are what get sent,
        // because the server validates the trimmed text rather than what the boxes held.
        var title = Title.Trim();
        var description = Description.Trim();
        if (title.Length == 0) { Error = "请填写反馈标题。"; return; }
        if (title.Length > 120) { Error = "标题最多 120 个字。"; return; }
        if (description.Length == 0) { Error = "请填写反馈描述。"; return; }
        if (description.Length > 4000) { Error = "描述最多 4000 个字。"; return; }
        if (title.Concat(description).Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
        {
            Error = "内容包含无法提交的控制字符，请重新输入。";
            return;
        }

        IsBusy = true; Error = "";
        try
        {
            var images = Images.Select(i => new FeedbackImagePayload(i.Mime, Convert.ToBase64String(i.Bytes))).ToArray();
            var result = await _api.CreateFeedbackAsync(_token, new FeedbackSubmission(Kind, Category, title, description, images, PrivacyConfirmed), _ct);
            Title = ""; Description = ""; Images.Clear(); PrivacyConfirmed = false;
            Submitted?.Invoke(this, result);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { }
        catch (V2ApiException error) { Error = error.Message; }
        catch (Exception) { Error = "提交结果未确认，内容已保留。请刷新列表确认后再重试。"; }
        finally { IsBusy = false; }
    }
}

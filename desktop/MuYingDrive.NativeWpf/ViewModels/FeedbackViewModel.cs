using System.Collections.ObjectModel;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class FeedbackViewModel : ObservableObject, IDisposable
{
    private readonly V2ApiClient _api;
    private readonly string _token;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _canManage, _hasMore, _loaded;
    private int _page = 1;
    private string _query = "", _kind = "all", _scope = "public", _message = "";
    private FeedbackItem? _detail;
    private string _status = "", _visibility = "public";
    private string? _submissionMessage;

    public FeedbackViewModel(DriveSession session, NasClientSettings settings)
        : this(new V2ApiClient(new V2ApiClientOptions(settings.ApiBaseUri, settings.CertificateSha256)),
               session.SessionToken)
    {
    }

    /// <summary>
    /// Feedback over an already-configured client. A visitor session can read only what
    /// the service allows; the shared workspace disables the feedback entry for read-only users.
    /// </summary>
    public FeedbackViewModel(V2ApiClient api, string sessionToken)
    {
        _api = api;
        _token = sessionToken;
        Draft = new FeedbackDraftViewModel(_api, _token, _lifetime.Token);
        Draft.Submitted += (_, result) =>
        {
            _submissionMessage = result.PendingReview ? "已提交。截图经维护人员检查后，反馈将公开给团队。" : "已提交，团队成员现在可以查看。";
            Message = _submissionMessage;
            _loaded = false;
        };
        RefreshCommand = new AsyncRelayCommand(_ => RunAsync(async () => await LoadAsync()), _ => !IsBusy);
        SearchCommand = new AsyncRelayCommand(_ => RunAsync(async () => { _page = 1; await LoadAsync(); }), _ => !IsBusy);
        FilterCommand = new AsyncRelayCommand(p => RunAsync(async () =>
        {
            _kind = p as string ?? "all"; _page = 1; OnPropertyChanged(nameof(FilterLabel)); await LoadAsync();
        }), _ => !IsBusy);
        PageCommand = new AsyncRelayCommand(p => RunAsync(async () =>
        {
            _page += p as string == "next" ? 1 : -1; await LoadAsync();
        }), p => !IsBusy && (p as string == "next" ? _hasMore : _page > 1));
        ScopeCommand = new AsyncRelayCommand(p => RunAsync(async () =>
        {
            _scope = p as string ?? "public"; _page = 1; OnPropertyChanged(nameof(FilterLabel)); await LoadAsync();
        }), _ => !IsBusy && CanManage);
        OpenCommand = new AsyncRelayCommand(p => RunAsync(async () =>
        {
            if (p is not FeedbackItem item) return;
            Detail = await _api.FeedbackDetailAsync(_token, item.FeedbackId, _lifetime.Token);
            SelectedStatus = Detail.Status;
            SelectedVisibility = _scope;
            OnPropertyChanged(nameof(StatusChoices));
            DetailImages.Clear();
            foreach (var attachment in Detail.Attachments)
            {
                var bytes = await _api.FeedbackImageAsync(_token, attachment.AttachmentId, _lifetime.Token);
                var preview = await Task.Run(() => FeedbackImageService.Decode(bytes), _lifetime.Token);
                DetailImages.Add(preview);
            }
        }), _ => !IsBusy);
        BackCommand = new RelayCommand(_ => { Detail = null; DetailImages.Clear(); });
        SaveStatusCommand = new AsyncRelayCommand(_ => RunAsync(async () =>
        {
            if (Detail is null) return;
            Detail = await _api.UpdateFeedbackAsync(_token, Detail.FeedbackId, SelectedStatus, SelectedVisibility, _lifetime.Token);
            Message = "处理状态已保存。";
            _loaded = false;
        }), _ => CanManage && Detail is not null && !IsBusy);
    }

    public ObservableCollection<FeedbackItem> Items { get; } = [];
    public ObservableCollection<FeedbackPreview> DetailImages { get; } = [];
    public FeedbackDraftViewModel Draft { get; }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); RaiseCommands(); } }
    public bool CanManage { get => _canManage; private set { SetProperty(ref _canManage, value); RaiseCommands(); } }
    public string Query { get => _query; set => SetProperty(ref _query, value); }
    public string Message { get => _message; set => SetProperty(ref _message, value); }
    public bool IsEmpty => _loaded && Items.Count == 0 && !IsBusy;
    public string PageLabel => $"第 {_page} 页";
    public string FilterLabel => $"{(_kind == "problem" ? "问题反馈" : _kind == "suggestion" ? "功能建议" : "全部")} · {(_scope == "pending" ? "待审核" : _scope == "hidden" ? "已隐藏" : "公开反馈")}";
    public FeedbackItem? Detail { get => _detail; private set { SetProperty(ref _detail, value); OnPropertyChanged(nameof(IsDetail)); RaiseCommands(); } }
    public bool IsDetail => Detail is not null;
    public string SelectedStatus { get => _status; set => SetProperty(ref _status, value); }
    public string SelectedVisibility { get => _visibility; set => SetProperty(ref _visibility, value); }
    public IReadOnlyList<FeedbackChoice> StatusChoices => (Detail?.Kind == "suggestion"
        ? new[] { "evaluating", "planned", "accepted", "declined" }
        : new[] { "pending", "in_progress", "resolved" }).Select(s => new FeedbackChoice(s, FeedbackLabels.Status(s))).ToArray();
    public IReadOnlyList<FeedbackChoice> VisibilityChoices { get; } = [new("public", "公开"), new("pending", "待审核"), new("hidden", "隐藏")];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand FilterCommand { get; }
    public AsyncRelayCommand ScopeCommand { get; }
    public AsyncRelayCommand PageCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public RelayCommand BackCommand { get; }
    public AsyncRelayCommand SaveStatusCommand { get; }

    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : RunAsync(async () =>
    {
        CanManage = (await _api.FeedbackCapabilitiesAsync(_token, _lifetime.Token)).CanManage;
        await LoadAsync();
    });

    private async Task LoadAsync()
    {
        var result = await _api.FeedbackListAsync(_token, _kind, Query, _page, _scope, _lifetime.Token);
        Items.Clear();
        foreach (var item in result.Items) Items.Add(item);
        _hasMore = result.HasMore; _loaded = true;
        OnPropertyChanged(nameof(PageLabel));
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy || _lifetime.IsCancellationRequested) return;
        IsBusy = true; Message = "";
        try { await operation(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (V2ApiException error) { Message = error.Message; }
        catch (Exception) { Message = "反馈暂时无法加载，请重试。"; }
        finally
        {
            if (_loaded && _submissionMessage is not null) { Message = _submissionMessage; _submissionMessage = null; }
            IsBusy = false; OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void RaiseCommands()
    {
        RefreshCommand?.RaiseCanExecuteChanged(); SearchCommand?.RaiseCanExecuteChanged();
        FilterCommand?.RaiseCanExecuteChanged(); PageCommand?.RaiseCanExecuteChanged();
        ScopeCommand?.RaiseCanExecuteChanged(); OpenCommand?.RaiseCanExecuteChanged(); SaveStatusCommand?.RaiseCanExecuteChanged();
    }

    public void Dispose() { _lifetime.Cancel(); _api.Dispose(); }
}

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.ProjectWorkbooks;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class ProjectAssetImageItem(ImageAsset asset, string projectId, string projectName,
    string assetLabel, bool isSelected) : ObservableObject
{
    private ImageSource? _preview;
    private bool _isLoading;
    private string? _previewError;

    public ImageAsset Asset { get; } = asset;
    public string ProjectId { get; } = projectId;
    public string ProjectName { get; } = projectName;
    public string AssetLabel { get; } = assetLabel;
    public bool IsSelected { get; } = isSelected;
    public string RelationshipLabel => IsSelected ? "选定图片" : "备选图片";
    public ImageSource? Preview { get => _preview; private set => SetProperty(ref _preview, value); }
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(PreviewStatus));
        }
    }
    public string PreviewStatus => PreviewError ?? (IsLoading ? "正在加载预览……" : "");
    private string? PreviewError { get => _previewError; set { if (SetProperty(ref _previewError, value)) OnPropertyChanged(nameof(PreviewStatus)); } }

    internal void BeginLoading()
    {
        IsLoading = true;
        PreviewError = null;
    }

    internal void SetPreview(ImageSource? preview, string? error = null)
    {
        Preview = preview;
        IsLoading = false;
        PreviewError = error;
    }
}

/// <summary>Shows only images linked from active asset rows in project workbooks.</summary>
public sealed class ProjectImagesViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 60;
    private readonly ProjectWorkbookPage _workbooks;
    private readonly IProjectWorkbookService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _projectReadSlots = new(4, 4);
    private readonly SemaphoreSlim _thumbnailSlots = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _watchedProjects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectRefreshGates = new(StringComparer.Ordinal);
    private List<ProjectAssetImageItem> _allItems = [];
    private CancellationTokenSource? _loadLifetime;
    private CancellationTokenSource? _previewLifetime;
    private CancellationTokenSource? _watchLifetime;
    private string? _projectId;
    private string? _projectName;
    private int _visibleCount;
    private int _loadGeneration;
    private bool _isLoading;
    private bool _isLoaded;
    private bool _isActive;
    private string _error = string.Empty;

    public ProjectImagesViewModel(ProjectWorkbookPage workbooks)
    {
        _workbooks = workbooks ?? throw new ArgumentNullException(nameof(workbooks));
        _service = workbooks.ViewModel.Service;
    }

    public ObservableCollection<ProjectAssetImageItem> Items { get; } = [];
    public string ScopeTitle => _projectName ?? "项目图片";
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value)) return;
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(CanRefresh));
        }
    }
    public bool CanRefresh => !IsLoading;
    public bool IsLoaded { get => _isLoaded; private set => SetProperty(ref _isLoaded, value); }
    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }
    public string Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasItems => Items.Count > 0;
    public bool HasMore => _visibleCount < _allItems.Count;
    public bool ShowEmpty => !IsLoading && !HasItems && !HasError;
    public string EmptyMessage => _projectName == null
        ? "资产表中还没有关联图片。"
        : $"“{_projectName}”的资产表中还没有关联图片。";

    public void ShowAllProjects()
    {
        IsActive = true;
        _projectId = null;
        _projectName = null;
        OnPropertyChanged(nameof(ScopeTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        _ = LoadAsync();
    }

    public void ShowProject(string projectId, string projectName)
    {
        IsActive = true;
        _projectId = projectId;
        _projectName = projectName;
        OnPropertyChanged(nameof(ScopeTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        _ = LoadAsync();
    }

    public void Deactivate()
    {
        IsActive = false;
        var load = Interlocked.Exchange(ref _loadLifetime, null);
        load?.Cancel();
        load?.Dispose();
        var previews = Interlocked.Exchange(ref _previewLifetime, null);
        previews?.Cancel();
        previews?.Dispose();
        var watchers = Interlocked.Exchange(ref _watchLifetime, null);
        watchers?.Cancel();
        watchers?.Dispose();
        _watchedProjects.Clear();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loadLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var previous = Interlocked.Exchange(ref _loadLifetime, loadLifetime);
        previous?.Cancel();
        previous?.Dispose();
        var generation = Interlocked.Increment(ref _loadGeneration);
        var previewLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var oldPreviewLifetime = Interlocked.Exchange(ref _previewLifetime, previewLifetime);
        oldPreviewLifetime?.Cancel();
        oldPreviewLifetime?.Dispose();

        IsLoading = true;
        Error = string.Empty;
        try
        {
            var projects = await _workbooks.ListProjectsAsync(loadLifetime.Token);
            var scopedProjects = _projectId == null
                ? projects
                : projects.Where(project => project.Id == _projectId).ToArray();

            var snapshots = await Task.WhenAll(scopedProjects.Select(project => ReadProjectAsync(project, loadLifetime.Token)));
            if (generation != Volatile.Read(ref _loadGeneration)) return;

            _allItems = Sort(snapshots.SelectMany(snapshot => snapshot));
            await OnUiAsync(() =>
            {
                _visibleCount = 0;
                ReplaceVisibleItems(PageSize);
                IsLoaded = true;
            });
            if (IsActive)
            {
                _watchLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                foreach (var project in scopedProjects) StartWatcher(project, _watchLifetime.Token);
            }
            _ = LoadPreviewsAsync(Items.ToArray(), previewLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _loadGeneration))
                Error = exception is WorkbookApiException ? exception.Message : "项目图片加载失败，请检查连接后重试。";
        }
        finally
        {
            if (generation == Volatile.Read(ref _loadGeneration)) IsLoading = false;
            Interlocked.CompareExchange(ref _loadLifetime, null, loadLifetime);
            loadLifetime.Dispose();
        }
    }

    public void LoadMore()
    {
        var start = _visibleCount;
        var end = Math.Min(start + PageSize, _allItems.Count);
        for (var index = start; index < end; index++) Items.Add(_allItems[index]);
        _visibleCount = end;
        NotifyItemsChanged();
        if (_previewLifetime is { IsCancellationRequested: false } lifetime)
            _ = LoadPreviewsAsync(_allItems.Skip(start).Take(end - start).ToArray(), lifetime.Token);
    }

    private async Task<List<ProjectAssetImageItem>> ReadProjectAsync(ProjectInfo project, CancellationToken ct)
    {
        await _projectReadSlots.WaitAsync(ct);
        try
        {
            var files = await _workbooks.ListProjectFilesAsync(project.Id, ct);
            var assetWorkbook = files.FirstOrDefault(file => file.TemplateKind == "asset_review");
            if (assetWorkbook == null) return [];

            var document = await _service.OpenAsync(assetWorkbook.Id, ct);
            var result = new List<ProjectAssetImageItem>();
            foreach (var row in document.Sheets.SelectMany(sheet => sheet.AssetRows))
            {
                var assetLabel = string.Join(" / ", new[] { row.Category, row.Name }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                if (string.IsNullOrWhiteSpace(assetLabel)) assetLabel = "未命名资产";
                var selectedId = row.SelectedImage?.Id;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (row.SelectedImage is { } selected)
                {
                    result.Add(new ProjectAssetImageItem(selected, project.Id, project.Name, assetLabel, true));
                    seen.Add(selected.Id);
                }
                foreach (var candidate in row.CandidateImages)
                {
                    if (!seen.Add(candidate.Id)) continue;
                    result.Add(new ProjectAssetImageItem(candidate, project.Id, project.Name, assetLabel,
                        candidate.Id == selectedId));
                }
            }
            return result;
        }
        finally { _projectReadSlots.Release(); }
    }

    private void StartWatcher(ProjectInfo project, CancellationToken ct)
    {
        if (!_watchedProjects.TryAdd(project.Id, 0)) return;
        _ = Task.Run(() => WatchProjectAsync(project, ct));
    }

    private async Task WatchProjectAsync(ProjectInfo project, CancellationToken ct)
    {
        long? sequence = null;
        var retryDelay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in _service.SubscribeProjectEventsAsync(project.Id, sequence, ct))
                {
                    if (change.EventKind == "ready")
                    {
                        sequence = change.Sequence;
                        await RefreshProjectAsync(project, ct);
                        retryDelay = TimeSpan.FromSeconds(1);
                        continue;
                    }
                    sequence = change.Sequence;
                    if (change.EntityKind is "row" or "image" or "workbook" or "sheet")
                    {
                        await Task.Delay(150, ct);
                        await RefreshProjectAsync(project, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch
            {
                try { await Task.Delay(retryDelay, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 15));
            }
        }
    }

    private async Task RefreshProjectAsync(ProjectInfo project, CancellationToken ct)
    {
        if (_projectId != null && _projectId != project.Id) return;
        var gate = _projectRefreshGates.GetOrAdd(project.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = await ReadProjectAsync(project, ct);
            if (ct.IsCancellationRequested || (_projectId != null && _projectId != project.Id)) return;
            ProjectAssetImageItem[] visibleProjectItems = [];
            await OnUiAsync(() =>
            {
                _allItems = Sort(_allItems.Where(item => item.ProjectId != project.Id).Concat(snapshot));
                var targetCount = Math.Max(_visibleCount, Math.Min(PageSize, _allItems.Count));
                ReplaceVisibleItems(targetCount);
                visibleProjectItems = _allItems.Take(_visibleCount).Where(item => item.ProjectId == project.Id).ToArray();
            });
            if (_previewLifetime is { IsCancellationRequested: false } lifetime)
                _ = LoadPreviewsAsync(visibleProjectItems, lifetime.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { /* Keep the last gallery snapshot while the event stream retries. */ }
        finally { gate.Release(); }
    }

    private async Task LoadPreviewsAsync(IReadOnlyList<ProjectAssetImageItem> items, CancellationToken ct)
    {
        await Task.WhenAll(items.Select(item => LoadPreviewAsync(item, ct)));
    }

    private async Task LoadPreviewAsync(ProjectAssetImageItem item, CancellationToken ct)
    {
        if (item.Preview != null || ct.IsCancellationRequested) return;
        await _thumbnailSlots.WaitAsync(ct);
        try
        {
            if (item.Preview != null || ct.IsCancellationRequested) return;
            await OnUiAsync(item.BeginLoading);
            var bytes = await _service.GetImageContentAsync(item.Asset.Id, thumbnail: true, ct);
            var preview = await Task.Run(() => ProjectWorkbooksViewModel.Decode(bytes, thumbnail: true), ct);
            await OnUiAsync(() => item.SetPreview(preview));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            App.Record(exception, "ProjectImagesViewModel.LoadPreview");
            await OnUiAsync(() => item.SetPreview(null, "预览加载失败"));
        }
        finally { _thumbnailSlots.Release(); }
    }

    private void ReplaceVisibleItems(int count)
    {
        Items.Clear();
        _visibleCount = Math.Min(count, _allItems.Count);
        foreach (var item in _allItems.Take(_visibleCount)) Items.Add(item);
        NotifyItemsChanged();
    }

    private void NotifyItemsChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private static List<ProjectAssetImageItem> Sort(IEnumerable<ProjectAssetImageItem> items) => items
        .OrderBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(item => item.AssetLabel, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(item => item.IsSelected ? 0 : 1)
        .ThenBy(item => item.Asset.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        var load = Interlocked.Exchange(ref _loadLifetime, null);
        load?.Cancel();
        load?.Dispose();
        var previews = Interlocked.Exchange(ref _previewLifetime, null);
        previews?.Cancel();
        previews?.Dispose();
        var watchers = Interlocked.Exchange(ref _watchLifetime, null);
        watchers?.Cancel();
        watchers?.Dispose();
        _lifetime.Dispose();
    }
}

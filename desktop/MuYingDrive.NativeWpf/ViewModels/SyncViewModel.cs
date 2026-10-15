using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.ProjectWorkbooks;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class SyncViewModel : ObservableObject, IDisposable
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MuYingDrive",
        "sync_config.json");

    private readonly ProjectWorkbookPage? _workbooks;
    private readonly EpisodeSyncService? _syncService;

    private ProjectInfo? _selectedProject;
    private ProjectFile? _selectedWorkbook;
    private string _localDirectory = string.Empty;
    private bool _isLoadingProjects;
    private bool _isLoadingWorkbooks;
    private bool _isLoadingEpisodes;
    private string _validationMessage = string.Empty;

    public SyncViewModel(ProjectWorkbookPage? workbooks, string shareRoot)
    {
        _workbooks = workbooks;
        if (workbooks != null)
        {
            _syncService = new EpisodeSyncService(workbooks.ViewModel.Service, shareRoot);
            _syncService.PropertyChanged += (s, e) =>
            {
                void Update()
                {
                    if (e.PropertyName == nameof(EpisodeSyncService.StatusText))
                        OnPropertyChanged(nameof(StatusText));
                    else if (e.PropertyName == nameof(EpisodeSyncService.IsSyncing))
                        OnPropertyChanged(nameof(IsSyncing));
                    else if (e.PropertyName == nameof(EpisodeSyncService.IsWatching))
                    {
                        OnPropertyChanged(nameof(IsWatching));
                        OnPropertyChanged(nameof(ToggleButtonLabel));
                    }
                }

                if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                {
                    dispatcher.InvokeAsync(Update);
                }
                else
                {
                    Update();
                }
            };
        }

        Projects = [];
        Workbooks = [];
        Episodes = [];

        BrowseLocalDirectoryCommand = new RelayCommand(_ => BrowseLocalDirectory());
        SelectAllEpisodesCommand = new RelayCommand(_ => SelectAllEpisodes());
        DeselectAllEpisodesCommand = new RelayCommand(_ => DeselectAllEpisodes());
        ToggleSyncCommand = new RelayCommand(_ => ToggleSync());
        SyncNowCommand = new RelayCommand(async _ => await SyncNowAsync(), _ => CanSyncNow);

        LoadSavedConfig();
        _ = InitializeAsync();
    }

    public ObservableCollection<ProjectInfo> Projects { get; }
    public ObservableCollection<ProjectFile> Workbooks { get; }
    public ObservableCollection<SyncEpisodeItem> Episodes { get; }

    public ProjectInfo? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                SaveConfig();
                _ = LoadWorkbooksAsync(value?.Id);
            }
        }
    }

    public ProjectFile? SelectedWorkbook
    {
        get => _selectedWorkbook;
        set
        {
            if (SetProperty(ref _selectedWorkbook, value))
            {
                SaveConfig();
                _ = LoadEpisodesAsync(value);
            }
        }
    }

    public string LocalDirectory
    {
        get => _localDirectory;
        set
        {
            if (SetProperty(ref _localDirectory, value))
            {
                SaveConfig();
                ClearValidation();
            }
        }
    }

    public bool IsLoadingProjects
    {
        get => _isLoadingProjects;
        private set => SetProperty(ref _isLoadingProjects, value);
    }

    public bool IsLoadingWorkbooks
    {
        get => _isLoadingWorkbooks;
        private set => SetProperty(ref _isLoadingWorkbooks, value);
    }

    public bool IsLoadingEpisodes
    {
        get => _isLoadingEpisodes;
        private set => SetProperty(ref _isLoadingEpisodes, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public string StatusText => _syncService?.StatusText ?? "未连接同步服务";

    public bool IsSyncing => _syncService?.IsSyncing ?? false;

    public bool IsWatching => _syncService?.IsWatching ?? false;

    public string ToggleButtonLabel => IsWatching ? "停止同步" : "开启同步";

    public bool CanSyncNow => !IsSyncing && SelectedProject != null && SelectedWorkbook != null && Episodes.Any(e => e.IsSelected) && !string.IsNullOrWhiteSpace(LocalDirectory);

    public ICommand BrowseLocalDirectoryCommand { get; }
    public ICommand SelectAllEpisodesCommand { get; }
    public ICommand DeselectAllEpisodesCommand { get; }
    public ICommand ToggleSyncCommand { get; }
    public RelayCommand SyncNowCommand { get; }

    public async Task InitializeAsync()
    {
        if (_workbooks == null || Projects.Count > 0) return;
        IsLoadingProjects = true;
        try
        {
            var projectList = await _workbooks.ListProjectsAsync();
            Projects.Clear();
            foreach (var p in projectList)
            {
                Projects.Add(p);
            }

            var savedId = _savedProjectId;
            if (!string.IsNullOrWhiteSpace(savedId) && Projects.FirstOrDefault(p => p.Id == savedId) is { } matched)
            {
                SelectedProject = matched;
            }
            else if (Projects.Count > 0)
            {
                SelectedProject = Projects[0];
            }
        }
        catch
        {
        }
        finally
        {
            IsLoadingProjects = false;
        }
    }

    public async Task LoadWorkbooksAsync(string? projectId)
    {
        Workbooks.Clear();
        Episodes.Clear();
        SelectedWorkbook = null;
        if (_workbooks == null || string.IsNullOrWhiteSpace(projectId)) return;

        IsLoadingWorkbooks = true;
        try
        {
            var files = await _workbooks.ListProjectFilesAsync(projectId);
            var beatFiles = files.Where(f =>
                f.TemplateKind == "scene_beats" ||
                f.TemplateKind == "scene_review" ||
                f.Name.Contains("节拍", StringComparison.OrdinalIgnoreCase)).ToList();

            var displayFiles = beatFiles.Count > 0 ? beatFiles : files.ToList();
            foreach (var file in displayFiles)
            {
                Workbooks.Add(file);
            }

            var savedWbId = _savedWorkbookId;
            if (!string.IsNullOrWhiteSpace(savedWbId) && Workbooks.FirstOrDefault(w => w.Id == savedWbId) is { } matched)
            {
                SelectedWorkbook = matched;
            }
            else if (Workbooks.Count > 0)
            {
                SelectedWorkbook = Workbooks[0];
            }
        }
        catch
        {
        }
        finally
        {
            IsLoadingWorkbooks = false;
            SyncNowCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task LoadEpisodesAsync(ProjectFile? workbook)
    {
        Episodes.Clear();
        if (_workbooks == null || workbook == null) return;

        IsLoadingEpisodes = true;
        try
        {
            var doc = await _workbooks.ViewModel.Service.OpenAsync(workbook.Id);
            if (doc?.Sheets != null)
            {
                var sceneSheets = doc.Sheets.Where(s =>
                    s.Kind == "scene_beats" ||
                    s.Kind == "scene_review" ||
                    s.SceneRows.Count > 0).ToList();
                var sheets = sceneSheets.Count > 0 ? sceneSheets : doc.Sheets;

                foreach (var sheet in sheets)
                {
                    var isSelected = _savedEpisodes.Contains(sheet.Name) || _savedEpisodes.Count == 0;
                    var item = new SyncEpisodeItem
                    {
                        Name = sheet.Name,
                        FileId = workbook.Id,
                        SheetId = sheet.Id,
                        IsSelected = isSelected
                    };
                    item.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(SyncEpisodeItem.IsSelected))
                        {
                            SaveConfig();
                            ClearValidation();
                            SyncNowCommand.RaiseCanExecuteChanged();
                        }
                    };
                    Episodes.Add(item);
                }
            }

            SyncNowCommand.RaiseCanExecuteChanged();
        }
        catch
        {
        }
        finally
        {
            IsLoadingEpisodes = false;
        }
    }

    private void BrowseLocalDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择剪辑电脑本地目录",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            LocalDirectory = dialog.FolderName;
        }
    }

    private void SelectAllEpisodes()
    {
        foreach (var ep in Episodes) ep.IsSelected = true;
    }

    private void DeselectAllEpisodes()
    {
        foreach (var ep in Episodes) ep.IsSelected = false;
    }

    private void ToggleSync()
    {
        try
        {
            if (_syncService == null) return;

            if (_syncService.IsWatching)
            {
                _syncService.Stop();
                ValidationMessage = string.Empty;
                return;
            }

            if (SelectedProject == null)
            {
                ValidationMessage = "请先选择需要同步的项目。";
                return;
            }

            if (SelectedWorkbook == null)
            {
                ValidationMessage = "请先选择项目节拍表。";
                return;
            }

            var selectedEpisodes = Episodes.Where(e => e.IsSelected).ToList();
            if (selectedEpisodes.Count == 0)
            {
                ValidationMessage = "请至少勾选一个集数。";
                return;
            }

            if (string.IsNullOrWhiteSpace(LocalDirectory))
            {
                ValidationMessage = "请先选择剪辑电脑本地目录。";
                return;
            }

            ValidationMessage = string.Empty;
            _syncService.Start(SelectedProject.Name, selectedEpisodes, LocalDirectory);
            SaveConfig();
        }
        catch (Exception ex)
        {
            ValidationMessage = $"开启同步出错: {ex.Message}";
        }
    }

    private async Task SyncNowAsync()
    {
        try
        {
            if (_syncService == null || SelectedProject == null || SelectedWorkbook == null) return;
            var selectedEpisodes = Episodes.Where(e => e.IsSelected).ToList();
            if (selectedEpisodes.Count == 0 || string.IsNullOrWhiteSpace(LocalDirectory)) return;

            if (!_syncService.IsWatching)
            {
                _syncService.Start(SelectedProject.Name, selectedEpisodes, LocalDirectory);
            }
            else
            {
                await _syncService.SyncNowAsync();
            }
        }
        catch (Exception ex)
        {
            ValidationMessage = $"同步出错: {ex.Message}";
        }
    }

    private void ClearValidation()
    {
        if (!string.IsNullOrWhiteSpace(ValidationMessage))
            ValidationMessage = string.Empty;
    }

    private string? _savedProjectId;
    private string? _savedWorkbookId;
    private readonly HashSet<string> _savedEpisodes = new(StringComparer.OrdinalIgnoreCase);

    private void LoadSavedConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var json = File.ReadAllText(ConfigPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("LocalDirectory", out var dirElem))
                _localDirectory = dirElem.GetString() ?? string.Empty;
            if (root.TryGetProperty("ProjectId", out var idElem))
                _savedProjectId = idElem.GetString();
            if (root.TryGetProperty("WorkbookId", out var wbElem))
                _savedWorkbookId = wbElem.GetString();
            if (root.TryGetProperty("Episodes", out var epElem) && epElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in epElem.EnumerateArray())
                {
                    if (item.GetString() is { } name) _savedEpisodes.Add(name);
                }
            }
        }
        catch
        {
        }
    }

    private void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var data = new
            {
                LocalDirectory = _localDirectory,
                ProjectId = SelectedProject?.Id,
                WorkbookId = SelectedWorkbook?.Id,
                Episodes = Episodes.Where(e => e.IsSelected).Select(e => e.Name).ToArray()
            };

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data));
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _syncService?.Dispose();
    }
}

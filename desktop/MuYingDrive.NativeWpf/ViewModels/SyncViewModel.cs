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
    private string _localDirectory = string.Empty;
    private bool _isLoadingProjects;
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
                if (e.PropertyName == nameof(EpisodeSyncService.StatusText))
                    OnPropertyChanged(nameof(StatusText));
                else if (e.PropertyName == nameof(EpisodeSyncService.IsSyncing))
                    OnPropertyChanged(nameof(IsSyncing));
                else if (e.PropertyName == nameof(EpisodeSyncService.IsWatching))
                {
                    OnPropertyChanged(nameof(IsWatching));
                    OnPropertyChanged(nameof(ToggleButtonLabel));
                }
            };
        }

        Projects = [];
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
    public ObservableCollection<SyncEpisodeItem> Episodes { get; }

    public ProjectInfo? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                SaveConfig();
                _ = LoadEpisodesAsync(value?.Id);
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

    public bool CanSyncNow => !IsSyncing && SelectedProject != null && Episodes.Any(e => e.IsSelected) && !string.IsNullOrWhiteSpace(LocalDirectory);

    public ICommand BrowseLocalDirectoryCommand { get; }
    public ICommand SelectAllEpisodesCommand { get; }
    public ICommand DeselectAllEpisodesCommand { get; }
    public ICommand ToggleSyncCommand { get; }
    public RelayCommand SyncNowCommand { get; }

    public async Task InitializeAsync()
    {
        if (_workbooks == null) return;
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

    public async Task LoadEpisodesAsync(string? projectId)
    {
        Episodes.Clear();
        if (_workbooks == null || string.IsNullOrWhiteSpace(projectId)) return;

        IsLoadingEpisodes = true;
        try
        {
            var files = await _workbooks.ListProjectFilesAsync(projectId);
            var sceneFiles = files.Where(f =>
                f.TemplateKind == "scene_review" ||
                f.TemplateKind == "scene_beats" ||
                f.Name.Contains("节拍表", StringComparison.OrdinalIgnoreCase)).ToList();

            if (sceneFiles.Count == 0)
            {
                // Fallback: create default episode row if none found
                sceneFiles = files.ToList();
            }

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in sceneFiles)
            {
                var epName = file.Name
                    .Replace("_场景节拍表", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("_节拍表", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(".xlsx", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();

                if (string.IsNullOrWhiteSpace(epName) || epName.Equals("节拍表", StringComparison.OrdinalIgnoreCase))
                    epName = "第1集";

                if (!seenNames.Add(epName)) continue;

                var isSelected = _savedEpisodes.Contains(epName) || _savedEpisodes.Count == 0;
                var item = new SyncEpisodeItem
                {
                    Name = epName,
                    FileId = file.Id,
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

    private async Task SyncNowAsync()
    {
        if (_syncService == null || SelectedProject == null) return;
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

    private void ClearValidation()
    {
        if (!string.IsNullOrWhiteSpace(ValidationMessage))
            ValidationMessage = string.Empty;
    }

    private string? _savedProjectId;
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

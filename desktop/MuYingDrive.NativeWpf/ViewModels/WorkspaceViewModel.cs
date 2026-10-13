using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using System.Windows.Threading;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class WorkspaceViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _selectedNavigation = "文件";
    private string _selectedSpace = "本部门";
    private object _currentContent;
    private bool _isSidebarExpanded = true;
    private readonly DispatcherTimer _updateNoticeTimer;
    private bool _wasUpdateReady;
    private bool _showUpdateNotice;

    public WorkspaceViewModel(MainViewModel main, DriveSession session, NasClientSettings settings)
    {
        _main = main;
        Session = session;
        Account = session.User;
        Transfers = new TransferViewModel(session.User.Id);
        Files = new FilesViewModel(session, settings, Transfers);
        _currentContent = Files;
        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarExpanded = !IsSidebarExpanded);
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string ?? "文件"));
        SelectSpaceCommand = new RelayCommand(parameter => SelectSpace(parameter as string ?? "本部门"));
        SignOutCommand = new AsyncRelayCommand(async _ => await _main.SignOutAsync(Session));
        ThemeChangedCommand = new RelayCommand(parameter => _main.ThemeService.SetPreference(parameter as string ?? "跟随系统"));
        _updateNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateNoticeTimer.Tick += HideUpdateNotice;
        _wasUpdateReady = _main.IsUpdateReady;
        _showUpdateNotice = _wasUpdateReady;
        if (_showUpdateNotice) _updateNoticeTimer.Start();
        _main.PropertyChanged += HandleMainPropertyChanged;
    }

    public DriveSession Session { get; }
    public V2UserSession Account { get; }
    public FilesViewModel Files { get; }
    public TransferViewModel Transfers { get; }
    public IReadOnlyList<string> ThemeChoices { get; } = ["跟随系统", "浅色", "深色"];

    public string CurrentTheme
    {
        get => _main.ThemeService.Preference;
        set
        {
            if (_main.ThemeService.Preference != value)
            {
                _main.ThemeService.SetPreference(value);
                OnPropertyChanged();
            }
        }
    }

    public string SelectedNavigation
    {
        get => _selectedNavigation;
        private set => SetProperty(ref _selectedNavigation, value);
    }

    public string SelectedSpace
    {
        get => _selectedSpace;
        private set
        {
            if (SetProperty(ref _selectedSpace, value))
            {
                Files.SetSpace(value);
                OnPropertyChanged(nameof(PageTitle));
            }
        }
    }

    public string PageTitle => SelectedNavigation == "传输" ? "传输" : "文件";

    public object CurrentContent
    {
        get => _currentContent;
        private set => SetProperty(ref _currentContent, value);
    }

    public bool IsSidebarExpanded
    {
        get => _isSidebarExpanded;
        set => SetProperty(ref _isSidebarExpanded, value);
    }

    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand SelectSpaceCommand { get; }
    public AsyncRelayCommand SignOutCommand { get; }
    public RelayCommand ThemeChangedCommand { get; }
    public AsyncRelayCommand InstallUpdateNowCommand => _main.InstallUpdateNowCommand;
    public RelayCommand InstallAfterExitCommand => _main.InstallAfterExitCommand;
    public bool IsUpdateReady => _main.IsUpdateReady && _showUpdateNotice;
    public string UpdateReadyVersion => _main.UpdateReadyVersion;
    public string UpdateScheduleText => _main.UpdateScheduleText;

    public async Task InitializeAsync()
    {
        await Transfers.InitializeAsync();
        await Files.InitializeAsync();
    }

    public ValueTask DisposeAsync()
    {
        _updateNoticeTimer.Stop();
        _updateNoticeTimer.Tick -= HideUpdateNotice;
        _main.PropertyChanged -= HandleMainPropertyChanged;
        return Transfers.DisposeAsync();
    }

    public void RefreshUpdateState()
    {
        if (_main.IsUpdateReady && !_wasUpdateReady)
        {
            _wasUpdateReady = true;
            _showUpdateNotice = true;
            _updateNoticeTimer.Stop();
            _updateNoticeTimer.Start();
        }
        else if (!_main.IsUpdateReady)
        {
            _wasUpdateReady = false;
            _showUpdateNotice = false;
            _updateNoticeTimer.Stop();
        }
        OnPropertyChanged(nameof(IsUpdateReady));
        OnPropertyChanged(nameof(UpdateReadyVersion));
        OnPropertyChanged(nameof(UpdateScheduleText));
    }

    private void HideUpdateNotice(object? sender, EventArgs args)
    {
        _updateNoticeTimer.Stop();
        if (!_showUpdateNotice) return;
        _showUpdateNotice = false;
        OnPropertyChanged(nameof(IsUpdateReady));
    }

    private void Navigate(string destination)
    {
        SelectedNavigation = destination == "传输" ? "传输" : "文件";
        CurrentContent = SelectedNavigation == "传输" ? Transfers : Files;
        OnPropertyChanged(nameof(PageTitle));
    }

    private void SelectSpace(string space)
    {
        SelectedSpace = space;
        SelectedNavigation = "文件";
        CurrentContent = Files;
        OnPropertyChanged(nameof(PageTitle));
    }

    private void HandleMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.IsUpdateReady) or nameof(MainViewModel.UpdateReadyVersion) or nameof(MainViewModel.UpdateScheduleText))
            RefreshUpdateState();
    }
}

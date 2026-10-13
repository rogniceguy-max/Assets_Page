using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>
/// What the shared file browser view needs from whatever is driving it.
/// </summary>
/// <remarks>
/// The employee file page and the visitor tour render the same view. They differ only
/// in the data source and in whether write affordances exist: <see cref="CanUpload"/>
/// and <see cref="CanModify"/> let the view omit them entirely rather than showing
/// disabled buttons. The tour's view model returns false for both, and the backend
/// offers no write route at all, so this is presentation, not the security boundary.
/// </remarks>
public interface IFileBrowser
{
    ObservableCollection<DriveFileItem> Files { get; }
    ICollectionView ItemsView { get; }

    string SearchText { get; }
    bool IsGridView { get; }
    string SpaceTitle { get; }
    string CurrentPathLabel { get; }
    string LoadingMessage { get; }
    string EmptyMessage { get; }

    /// <summary>True when the current location is read-only for this kind of user.</summary>
    bool IsReadOnly { get; }

    /// <summary>True when upload is offered here at all.</summary>
    bool CanUpload { get; }

    /// <summary>True when rename / move / copy / delete are offered here at all.</summary>
    bool CanModify { get; }

    bool CanNavigateUp { get; }
    int SelectedCount { get; }
    string SelectedLabel { get; }
    bool HasSelection { get; }
    bool IsLoading { get; }
    bool IsEmpty { get; }
    string ErrorMessage { get; }
    bool HasError { get; }

    ICommand OpenCommand { get; }
    ICommand DownloadCommand { get; }
    ICommand NavigateUpCommand { get; }
    ICommand ToggleGridCommand { get; }
    ICommand ToggleListCommand { get; }
    ICommand UploadCommand { get; }
    ICommand UnsupportedEditCommand { get; }
    ICommand DetailsCommand { get; }

    event Action<SelectionRequest>? SelectionRequested;

    /// <summary>Called by the view whenever the list selection changes.</summary>
    void UpdateSelectionCount(int count);
}

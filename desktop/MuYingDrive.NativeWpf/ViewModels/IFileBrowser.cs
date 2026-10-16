using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MuYingDrive.NativeWpf.Models;

namespace MuYingDrive.NativeWpf.ViewModels;

/// <summary>
/// What the shared file browser view needs from whatever is driving it.
/// </summary>
/// <remarks>
/// Employees and read-only visitors render the same browser. They differ only
/// in the data source and in whether write affordances exist: <see cref="CanUpload"/>
/// and <see cref="CanModify"/> let the view omit them entirely rather than showing
/// disabled buttons. A read-only session returns false for both.
///
/// WPF binds against the runtime type rather than this interface, so every member here
/// must exist on both implementations or the shared view raises a binding error — the
/// render harness fails the build for exactly that reason. Adding a command to this
/// interface is therefore what forces every browser to give an answer for it, rather
/// than letting one be forgotten and discovered at runtime. A read-only browser's answer is a no-op whose
/// CanExecute is false; the file API remains the authorization boundary.
/// </remarks>
public interface IFileBrowser
{
    ObservableCollection<DriveFileItem> Files { get; }
    ObservableCollection<InlineUploadCard> InlineUploadCards { get; }
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
    bool CanAcceptFileDrop { get; }
    bool IsDropTargetActive { get; }
    string InlineUploadMessage { get; }

    /// <summary>True when file mutations are offered here at all.</summary>
    bool CanModify { get; }
    bool CanShowRecycleBin { get; }
    string RecycleBinButtonLabel { get; }

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
    ICommand UploadFolderCommand { get; }
    ICommand RenameCommand { get; }
    ICommand MoveCommand { get; }
    ICommand DeleteCommand { get; }
    ICommand HistoryCommand { get; }
    ICommand RestoreCommand { get; }
    ICommand ToggleRecycleBinCommand { get; }
    ICommand DetailsCommand { get; }
    ICommand RetryInlineUploadCommand { get; }
    ICommand CancelInlineUploadCommand { get; }
    ICommand RetryInlineDownloadCommand { get; }
    ICommand CancelInlineDownloadCommand { get; }

    event Action<SelectionRequest>? SelectionRequested;

    /// <summary>
    /// Called by the view whenever the list selection changes, with the items selected.
    /// </summary>
    /// <remarks>
    /// The items rather than a count: rename, duplicate and delete all act on what is
    /// selected, and a count alone would leave each of them reaching back into the view to
    /// find out what it was.
    /// </remarks>
    void UpdateSelection(IReadOnlyList<DriveFileItem> items);
    void SetDropTargetActive(bool active);
    Task HandleFileDropAsync(IReadOnlyList<string> paths, DriveFileItem? targetFolder);
}

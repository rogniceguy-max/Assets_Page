using Microsoft.Win32;

namespace MuYingDrive.NativeWpf.Services;

/// <summary>
/// The OS file dialogs this client uses.
/// </summary>
/// <remarks>
/// The <c>Microsoft.Win32</c> dialogs rather than the WinForms equivalents: this is a
/// native WPF application, and pulling in the WinForms stack for a folder picker would
/// drag a second UI framework into the build to save a few lines.
/// </remarks>
public sealed class WindowsFilePickerService
{
    /// <summary>Files to upload, in the order chosen. Empty when the user cancels.</summary>
    public IReadOnlyList<string> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要上传的文件",
            CheckFileExists = true,
            Multiselect = true,
            Filter = "所有文件|*.*"
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    /// <summary>A folder whose contents are to be uploaded, or null when the user cancels.</summary>
    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要上传的文件夹",
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickDownloadDestination(string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "下载文件",
            FileName = fileName,
            AddExtension = false,
            OverwritePrompt = false,
            CheckPathExists = true,
            ValidateNames = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

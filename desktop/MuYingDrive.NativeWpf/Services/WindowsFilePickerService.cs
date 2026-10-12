using Microsoft.Win32;

namespace MuYingDrive.NativeWpf.Services;

public sealed class WindowsFilePickerService
{
    public string? PickSingleFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要上传的文件",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "所有文件|*.*"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
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

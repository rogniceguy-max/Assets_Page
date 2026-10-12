using System.IO;
using System.Windows;

namespace AssetsPage.WpfCfHdropSpike;

public partial class ReceiverWindow : Window
{
    public ReceiverWindow()
    {
        InitializeComponent();
        DropLogTextBox.Text = "Receiver ready.\r\n";
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        bool hasFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false);
        e.Effects = hasFileDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        bool hasFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false);
        if (!hasFileDrop)
        {
            string[] formats = e.Data.GetFormats(autoConvert: false);
            AppendRecord($"{DateTimeOffset.Now:O} | DataFormats.FileDrop=false | formats=[{string.Join(", ", formats)}]");
            DropStatusText.Text = "Drop received, but DataFormats.FileDrop was not present.";
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] paths)
        {
            AppendRecord($"{DateTimeOffset.Now:O} | DataFormats.FileDrop=true | unexpected payload type");
            DropStatusText.Text = "FileDrop format present, but payload was not a string path array.";
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        DropStatusText.Text = $"Received DataFormats.FileDrop=true ({paths.Length} path(s)).";
        AppendRecord($"{DateTimeOffset.Now:O} | DataFormats.FileDrop=true | count={paths.Length}");
        foreach (string path in paths)
        {
            string fullPath = Path.GetFullPath(path);
            bool exists = File.Exists(fullPath) || Directory.Exists(fullPath);
            AppendRecord($"  absolute={Path.IsPathFullyQualified(fullPath)} exists={exists} path={fullPath}");
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void AppendRecord(string record)
    {
        DropLogTextBox.AppendText(record + Environment.NewLine);
        DropLogTextBox.ScrollToEnd();
    }
}

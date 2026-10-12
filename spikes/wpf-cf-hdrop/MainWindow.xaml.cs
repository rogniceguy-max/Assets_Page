using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace AssetsPage.WpfCfHdropSpike;

public partial class MainWindow : Window
{
    private readonly string _syntheticFilePath;
    private Point _dragStart;
    private bool _isPointerDown;

    public MainWindow()
    {
        InitializeComponent();
        _syntheticFilePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "synthetic-test-file.txt"));
        if (!File.Exists(_syntheticFilePath))
        {
            throw new FileNotFoundException("The synthetic test file should be copied next to the application.", _syntheticFilePath);
        }
        FileNameText.Text = Path.GetFileName(_syntheticFilePath);
        FilePathText.Text = _syntheticFilePath;
        StatusText.Text = "Ready. Open the separate receiver, then drag the card into it.";
    }

    private void DragCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(DragCard);
        _isPointerDown = true;
    }

    private void DragCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _isPointerDown = false;

    private void DragCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPointerDown || e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(DragCard);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _isPointerDown = false;
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { _syntheticFilePath }, autoConvert: false);
        DragDropEffects effect = DragDrop.DoDragDrop(DragCard, data, DragDropEffects.Copy);
        StatusText.Text = $"Drag finished. Effect: {effect}. Source format: DataFormats.FileDrop (CF_HDROP).";
    }

    private void OpenReceiverButton_Click(object sender, RoutedEventArgs e)
    {
        string receiverExecutable = Path.Combine(AppContext.BaseDirectory, "WpfCfHdropSpike.exe");
        if (!File.Exists(receiverExecutable))
        {
            StatusText.Text = $"Receiver executable not found: {receiverExecutable}";
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = receiverExecutable,
            UseShellExecute = false,
            ArgumentList = { "--receiver" }
        });
        StatusText.Text = "Separate receiver launched. Drag the synthetic file card into its window.";
    }
}

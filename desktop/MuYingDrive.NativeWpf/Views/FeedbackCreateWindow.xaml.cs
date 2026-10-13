using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.NativeWpf.ViewModels;

namespace MuYingDrive.NativeWpf.Views;

public partial class FeedbackCreateWindow : Window
{
    private readonly FeedbackDraftViewModel _model;
    private bool _submitted, _adding;
    public FeedbackCreateWindow(FeedbackDraftViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        model.Submitted += OnSubmitted;
        Closed += (_, _) => model.Submitted -= OnSubmitted;
        Loaded += (_, _) =>
        {
            var brush = Application.Current.TryFindResource("Brush.Surface.Canvas") as System.Windows.Media.SolidColorBrush;
            NativeWindowTheme.Apply(this, brush?.Color.R < 128);
            TitleBox.Focus();
        };
    }
    private void OnSubmitted(object? sender, FeedbackCreated result) { _submitted = true; DialogResult = true; }
    private void OnClosing(object? sender, CancelEventArgs e) { if ((_model.IsBusy || _adding) && !_submitted) e.Cancel = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private async void ChooseImages_Click(object sender, RoutedEventArgs e)
    {
        if (_model.IsBusy || _adding) return;
        var picker = new OpenFileDialog { Filter = "截图 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Multiselect = true };
        if (picker.ShowDialog(this) == true) await AddFilesAsync(picker.FileNames);
    }
    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (_model.IsBusy || _adding) return;
        _adding = true;
        _model.IsAdding = true;
        try
        {
            foreach (var path in paths.Take(5))
            {
                if (_model.Images.Count >= 4) { _model.Error = "最多上传四张截图。"; break; }
                _model.AddImage(await FeedbackImageService.ReadAsync(path));
            }
        }
        catch (InvalidOperationException error) { _model.Error = error.Message; }
        catch (Exception) { _model.Error = "无法读取截图，请检查图片后重试。"; }
        finally { _adding = false; _model.IsAdding = false; }
    }
    private void PasteImage_Click(object sender, RoutedEventArgs e) => PasteImage();
    private void PasteImage()
    {
        if (_model.IsBusy || _adding) return;
        try
        {
            var image = Clipboard.GetImage();
            if (image is null) { _model.Error = "剪贴板中没有截图。"; return; }
            _model.AddImage(FeedbackImageService.FromClipboard(image));
        }
        catch (InvalidOperationException error) { _model.Error = error.Message; }
        catch (Exception) { _model.Error = "无法粘贴截图，请使用文件选择。"; }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = !_model.IsBusy && !_adding ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { e.Handled = true; await AddFilesAsync(files); }
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        { e.Handled = true; if (!_adding && _model.SubmitCommand.CanExecute(null)) _model.SubmitCommand.Execute(null); }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V && Clipboard.ContainsImage())
        { e.Handled = true; PasteImage(); }
    }
}

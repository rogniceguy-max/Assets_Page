using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.Views;

/// <summary>
/// Asks before an action that cannot be undone, listing exactly what it will affect.
/// </summary>
/// <remarks>
/// Shown with <c>ShowDialog</c>, which returns true only when the user confirms. Closing
/// the window, pressing Escape, or pressing Enter all mean no.
/// </remarks>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string heading, string body, IReadOnlyList<string> items, string confirmLabel)
    {
        InitializeComponent();
        HeadingText.Text = heading;
        BodyText.Text = body;
        ItemList.ItemsSource = items;
        ConfirmButton.Content = confirmLabel;
        Loaded += (_, _) =>
        {
            var brush = Application.Current.TryFindResource("Brush.Surface.Canvas") as SolidColorBrush;
            NativeWindowTheme.Apply(this, brush?.Color.R < 128);
            CancelButton.Focus();
        };
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        DialogResult = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

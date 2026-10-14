using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.Views;

/// <summary>
/// Asks for one name. Used by rename, which has no inline editing.
/// </summary>
/// <remarks>
/// Shown with <c>ShowDialog</c>. <see cref="Value"/> is only meaningful when it returned
/// true; cancelling leaves it as whatever was typed, which the caller ignores.
/// </remarks>
public partial class TextInputWindow : Window
{
    /// <param name="selectStemLength">
    /// How much of the initial value to pre-select. For a file that is the name without its
    /// extension, matching Explorer: the common rename keeps the extension and changes the
    /// name, so starting with the extension excluded makes that a single typed word.
    /// </param>
    public TextInputWindow(string heading, string prompt, string initialValue, string confirmLabel,
        int selectStemLength = -1)
    {
        InitializeComponent();
        Title = heading;
        HeadingText.Text = heading;
        PromptText.Text = prompt;
        InputBox.Text = initialValue;
        ConfirmButton.Content = confirmLabel;
        HintText.Text = "不能包含 \\ / : * ? \" < > | ，也不能以点或空格结尾。同名时会自动加序号，不会覆盖。";
        Loaded += (_, _) =>
        {
            var brush = Application.Current.TryFindResource("Brush.Surface.Canvas") as SolidColorBrush;
            NativeWindowTheme.Apply(this, brush?.Color.R < 128);
            InputBox.Focus();
            var length = selectStemLength >= 0 ? Math.Min(selectStemLength, InputBox.Text.Length) : InputBox.Text.Length;
            InputBox.Select(0, length);
        };
    }

    /// <summary>The name the user settled on.</summary>
    public string Value => InputBox.Text;

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        switch (args.Key)
        {
            case Key.Escape:
                args.Handled = true;
                DialogResult = false;
                break;
            case Key.Enter:
                args.Handled = true;
                Accept();
                break;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e) => Accept();

    /// <summary>
    /// Closes with a result only when something was actually typed: an empty name would be
    /// rejected downstream, and it is clearer to simply leave the dialog open.
    /// </summary>
    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        DialogResult = true;
    }
}

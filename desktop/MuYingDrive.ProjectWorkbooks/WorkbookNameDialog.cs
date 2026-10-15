using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MuYingDrive.ProjectWorkbooks;

public sealed class WorkbookNameDialog : Window
{
    private readonly TextBox _input;

    public WorkbookNameDialog(Window? owner, ResourceDictionary resources, string heading, string prompt, string initialValue = "", string confirmLabel = "创建")
    {
        Owner = owner;
        Title = heading;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        WorkflowDialogs.Theme(this, resources);

        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headingBlock = new TextBlock
        {
            Text = heading,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(headingBlock, 0);
        grid.Children.Add(headingBlock);

        var promptBlock = new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 12, 0, 6),
            TextWrapping = TextWrapping.Wrap
        };
        promptBlock.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        Grid.SetRow(promptBlock, 1);
        grid.Children.Add(promptBlock);

        var inputStack = new StackPanel();
        Grid.SetRow(inputStack, 2);

        _input = new TextBox
        {
            Text = initialValue,
            MaxLength = 255,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 6, 8, 6),
            Height = 32
        };
        _input.SetResourceReference(Control.BackgroundProperty, "Brush.Surface.Panel");
        _input.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
        _input.SetResourceReference(Control.BorderBrushProperty, "Brush.Border.Subtle");
        _input.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Text.Primary");
        inputStack.Children.Add(_input);

        var hint = new TextBlock
        {
            Text = "不能包含 \\ / : * ? \" < > | ，也不能以点或空格结尾。同名时会自动加序号，不会覆盖。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        inputStack.Children.Add(hint);
        grid.Children.Add(inputStack);

        var buttonStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        Grid.SetRow(buttonStack, 3);

        var cancelButton = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0), Width = 70, Height = 32 };
        cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        cancelButton.Click += (_, _) => DialogResult = false;
        buttonStack.Children.Add(cancelButton);

        var confirmButton = new Button { Content = confirmLabel, Width = 70, Height = 32 };
        confirmButton.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        confirmButton.Click += (_, _) => Accept();
        buttonStack.Children.Add(confirmButton);

        grid.Children.Add(buttonStack);
        Content = grid;

        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                DialogResult = false;
            }
            else if (args.Key == Key.Enter)
            {
                args.Handled = true;
                Accept();
            }
        };

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    public string Value => _input.Text;

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_input.Text)) return;
        DialogResult = true;
    }
}

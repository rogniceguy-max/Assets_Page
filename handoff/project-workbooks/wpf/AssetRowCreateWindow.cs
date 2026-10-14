using System.Windows;
using System.Windows.Controls;

namespace MuYingDrive.ProjectWorkbooks;

internal sealed class AssetRowCreateWindow : Window
{
    private readonly long _expectedWorkbookVersion;
    private readonly TextBox _category;
    private readonly TextBox _name;
    private readonly TextBox _variant;
    private readonly TextBox _appearance;
    private readonly TextBlock _error;

    public CreateAssetRowRequest? Request { get; private set; }

    public AssetRowCreateWindow(long expectedWorkbookVersion, ResourceDictionary themeResources)
    {
        _expectedWorkbookVersion = expectedWorkbookVersion;
        Title = "新建资产";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        MaxWidth = 520;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkflowDialogs.Theme(this, themeResources);

        var root = new StackPanel { Margin = new Thickness(20) };
        Content = root;
        _category = AddField(root, "类别");
        _category.MaxLength = 200;
        _name = AddField(root, "名称");
        _name.MaxLength = 200;
        _variant = AddField(root, "形态");
        _variant.MaxLength = 200;
        _appearance = AddField(root, "出场", multiline: true);
        _appearance.MaxLength = 4000;
        _error = WorkflowDialogs.Text("", 12);
        _error.Foreground = (System.Windows.Media.Brush)(TryFindResource("Brush.Status.Danger")
            ?? System.Windows.Media.Brushes.IndianRed);
        root.Children.Add(_error);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(WorkflowDialogs.Button("取消", (_, _) => DialogResult = false));
        actions.Children.Add(WorkflowDialogs.Button("创建", (_, _) => Submit()));
        root.Children.Add(actions);
        Loaded += (_, _) => _category.Focus();
    }

    private static TextBox AddField(Panel parent, string label, bool multiline = false)
    {
        parent.Children.Add(WorkflowDialogs.Text(label, 12));
        var input = WorkflowDialogs.Input("", multiline);
        if (!multiline)
        {
            input.AcceptsReturn = false;
            input.MinHeight = 30;
            input.MaxHeight = 30;
        }
        parent.Children.Add(input);
        return input;
    }

    private void Submit()
    {
        var category = _category.Text.Trim();
        var name = _name.Text.Trim();
        if (category.Length == 0 || name.Length == 0)
        {
            _error.Text = "请填写类别和名称。";
            return;
        }

        Request = new CreateAssetRowRequest(
            _expectedWorkbookVersion,
            Guid.NewGuid().ToString("N"),
            category,
            name,
            string.IsNullOrWhiteSpace(_variant.Text) ? null : _variant.Text.Trim(),
            string.IsNullOrWhiteSpace(_appearance.Text) ? null : _appearance.Text.Trim());
        DialogResult = true;
    }
}

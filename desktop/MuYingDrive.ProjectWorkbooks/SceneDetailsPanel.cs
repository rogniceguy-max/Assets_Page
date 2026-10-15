using System.Windows;
using System.Windows.Controls;

namespace MuYingDrive.ProjectWorkbooks;

public sealed class SceneDetailsPanel : UserControl
{
    private readonly ProjectWorkbooksViewModel _model;
    private readonly string _fileId;
    private readonly SceneRowItem _item;
    private readonly ResourceDictionary _resources;
    private readonly TextBox _plan;
    private readonly TextBox _note;

    public event EventHandler? CloseRequested { add { } remove { } }
    public string Heading { get; }

    public SceneDetailsPanel(ProjectWorkbooksViewModel model, string fileId, SceneRowItem item, ResourceDictionary resources)
    {
        _model = model;
        _fileId = fileId;
        _item = item;
        _resources = resources;
        Heading = $"场景 {item.Row.SceneNumber} · 节拍 {item.Row.BeatNumber}";

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Scrollable body
        Content = root;

        // Header: Clean border + title
        var header = new Border
        {
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(0, 0, 0, 10),
        };
        header.SetResourceReference(Border.BorderBrushProperty, "Brush.Border.Subtle");
        header.BorderThickness = new Thickness(0, 0, 0, 1);

        var title = new TextBlock
        {
            Text = Heading,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Child = title;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Content
        var content = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // 1. 拆分规划
        var planHeader = new TextBlock
        {
            Text = "拆分规划",
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 6),
        };
        planHeader.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        content.Children.Add(planHeader);

        _plan = new TextBox
        {
            Text = item.BreakdownPlanDraft ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 18),
            ToolTip = "拆分规划（支持多行输入，失焦自动保存）",
            IsEnabled = item.Row.CanEdit,
        };
        _plan.SetResourceReference(TextBox.BackgroundProperty, "Brush.Surface.Canvas");
        _plan.SetResourceReference(TextBox.BorderBrushProperty, "Brush.Border.Subtle");
        _plan.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text.Primary");
        _plan.LostFocus += (_, _) => CommitChanges();
        content.Children.Add(_plan);

        // 2. 批注
        var noteHeader = new TextBlock
        {
            Text = "批注",
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 6),
        };
        noteHeader.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        content.Children.Add(noteHeader);

        _note = new TextBox
        {
            Text = item.NoteDraft ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 18),
            ToolTip = "批注内容（支持多行输入，失焦自动保存）",
            IsEnabled = item.Row.CanEdit,
        };
        _note.SetResourceReference(TextBox.BackgroundProperty, "Brush.Surface.Canvas");
        _note.SetResourceReference(TextBox.BorderBrushProperty, "Brush.Border.Subtle");
        _note.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text.Primary");
        _note.LostFocus += (_, _) => CommitChanges();
        content.Children.Add(_note);

        // 3. 本拍关联资产
        if (item.HasLinkedAssets)
        {
            var assetsPanel = new StackPanel { Margin = new Thickness(8) };
            var wrap = new WrapPanel();
            foreach (var asset in item.LinkedAssets)
            {
                var card = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 8, 8),
                    Width = 140,
                };
                card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface.Panel");
                card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border.Subtle");

                var cardStack = new StackPanel();
                var headerText = new TextBlock
                {
                    Text = $"[{asset.Category}] {asset.Name}",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                };
                headerText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent.Primary");
                cardStack.Children.Add(headerText);
                if (!string.IsNullOrWhiteSpace(asset.Appearance))
                {
                    var appText = new TextBlock
                    {
                        Text = asset.Appearance,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0),
                    };
                    appText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
                    cardStack.Children.Add(appText);
                }
                card.Child = cardStack;
                wrap.Children.Add(card);
            }
            assetsPanel.Children.Add(wrap);
            content.Children.Add(new Expander
            {
                Header = $"本拍关联资产 ({item.LinkedAssets.Count})",
                Content = assetsPanel,
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }
    }

    private void CommitChanges()
    {
        var planText = _plan.Text;
        var noteText = _note.Text;
        var dirty = !string.Equals(planText, _item.BreakdownPlanDraft, StringComparison.Ordinal)
            || !string.Equals(noteText, _item.NoteDraft, StringComparison.Ordinal);
        if (!dirty) return;
        _item.BreakdownPlanDraft = planText;
        _item.NoteDraft = noteText;
        _ = _model.ExecuteInlineAsync(() => _model.UpdateSceneFieldsAsync(_item));
    }

    public void ApplyRemote(SceneRow previous, SceneRow row)
    {
        if (row.Id != _item.Row.Id) return;
        if (!_plan.IsKeyboardFocused && !string.Equals(_plan.Text, row.BreakdownPlan ?? "", StringComparison.Ordinal))
        {
            _plan.Text = row.BreakdownPlan ?? "";
            _item.BreakdownPlanDraft = row.BreakdownPlan;
        }
        if (!_note.IsKeyboardFocused && !string.Equals(_note.Text, row.Note ?? "", StringComparison.Ordinal))
        {
            _note.Text = row.Note ?? "";
            _item.NoteDraft = row.Note;
        }
    }

    public bool TryLeave()
    {
        CommitChanges();
        return true;
    }
}

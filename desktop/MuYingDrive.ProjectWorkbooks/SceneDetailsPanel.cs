using System;
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
    private readonly ReviewPanel _review;

    public event EventHandler? CloseRequested { add { } remove { } }
    public string Heading { get; }
    public bool HasDraft => !string.Equals(_plan?.Text?.Trim() ?? string.Empty, (_item?.BreakdownPlanDraft ?? string.Empty).Trim(), StringComparison.Ordinal);

    public SceneDetailsPanel(ProjectWorkbooksViewModel model, string fileId, SceneRowItem item, ResourceDictionary resources)
    {
        _model = model;
        _fileId = fileId;
        _item = item;
        _resources = resources;
        Heading = $"场景 {item.Row.SceneNumber} · 节拍 {item.Row.BeatNumber}";

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Breakdown Plan
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // ReviewPanel
        Content = root;

        // Header: Clean border + title
        var header = new Border
        {
            Margin = new Thickness(0, 0, 0, 12),
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

        // Section 1: 拆分规划 (可多行编辑，失焦自动保存)
        var planSection = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var planHeader = new TextBlock
        {
            Text = "拆分规划",
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 6),
        };
        planHeader.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        planSection.Children.Add(planHeader);

        _plan = new TextBox
        {
            Text = item.BreakdownPlanDraft ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 85,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10, 8, 10, 8),
            ToolTip = "拆分规划（支持多行输入，失焦自动保存）",
            IsEnabled = item.Row.CanEdit,
        };
        _plan.SetResourceReference(TextBox.BackgroundProperty, "Brush.Surface.Canvas");
        _plan.SetResourceReference(TextBox.BorderBrushProperty, "Brush.Border.Subtle");
        _plan.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text.Primary");
        _plan.LostFocus += (_, _) => CommitChanges();
        planSection.Children.Add(_plan);

        Grid.SetRow(planSection, 1);
        root.Children.Add(planSection);

        // Section 2: 批注协作流 (使用与资产表完全一致的 ReviewPanel，带有发送人、时间戳与讨论流)
        _review = new ReviewPanel
        {
            DataContext = new ReviewViewModel(_model.Service, _fileId, _item.Row.Id, null, "scene-rows", _model.IsReadOnly),
        };
        Grid.SetRow(_review, 2);
        root.Children.Add(_review);
    }

    private void CommitChanges()
    {
        var planText = _plan.Text;
        var dirty = !string.Equals(planText, _item.BreakdownPlanDraft, StringComparison.Ordinal);
        if (!dirty) return;
        _item.BreakdownPlanDraft = planText;
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
    }

    public bool TryLeave()
    {
        CommitChanges();
        return _review.TryLeave();
    }
}

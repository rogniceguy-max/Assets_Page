using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MuYingDrive.ProjectWorkbooks;

// HOST ADJUSTMENT. This was SceneDetailsWindow: a modal dialog opened per beat. The
// walk-through asked for it to become a panel that stays beside the list instead, for two
// reasons. Reading one beat's notes in a separate window hides the list you were reading it
// against, so checking three beats in a row is open-read-close three times. And the window's
// own chrome — the title bar, the unstyled default buttons — was the one piece of this app
// that did not look like this app.
//
// The state machine is carried over unchanged, deliberately, because it is the part that was
// hard to get right and the part a rewrite would silently lose:
//   * only a successful save advances the baseline the draft is measured against; a version
//     refresh never does, so input typed while a request was pending stays "unsaved";
//   * a 409 keeps the draft, says so, and reveals a reload rather than retrying or
//     overwriting;
//   * leaving with unsaved input has to be confirmed.
// What changed is only how it is hosted. TryLeave() replaces the Closing handler, and
// Window.GetWindow(this) replaces the owner captured by the constructor — the panel's owner
// is whichever window the page happens to be in, and that is not known when it is built.
public sealed class SceneDetailsPanel : UserControl
{
    private readonly ProjectWorkbooksViewModel _model;
    private readonly string _fileId;
    private readonly ResourceDictionary _resources;
    private readonly WrapPanel _toolbar;
    private readonly TextBlock _status;
    private readonly TextBlock _serverPlan;
    private readonly TextBox _plan;
    private readonly ComboBox _level;
    private readonly StackPanel _body;
    private readonly Button _reload;
    private readonly ReviewPanel _review;
    private SceneRowItem _item;
    private string _savedPlan;
    private string _savedLevel;
    private bool _busy;
    private bool _needsReload;

    /// <summary>Raised by the panel's own 关闭 button. The host decides whether to act on it.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The beat being shown, as the panel labels it.</summary>
    public string Heading { get; }

    public SceneDetailsPanel(ProjectWorkbooksViewModel model, string fileId, SceneRowItem item, ResourceDictionary resources)
    {
        _model = model;
        _fileId = fileId;
        _item = item;
        _resources = resources;
        _savedPlan = item.Row.BreakdownPlan ?? "";
        _savedLevel = item.Row.LabelLevel ?? "未标注";
        Heading = $"场景 {item.Row.SceneNumber} · 节拍 {item.Row.BeatNumber}";

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // status
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });     // 本拍内容
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // 批注
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // toolbar
        Content = root;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = Heading, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 10),
        };
        header.Children.Add(title);
        var close = WorkflowDialogs.Button("关闭", (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty));
        close.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _status = WorkflowDialogs.Text("", 12);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);

        var content = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        // The six imported 成员批注 columns are gone from here. They name members this app has
        // no way to identify, and picking one for whoever is signed in would be a guess shown
        // as a fact. What replaces them is what the walk-through asked for: you write a note,
        // it is signed with the account that wrote it, and nobody's column is assumed.
        _review = new ReviewPanel
        {
            Height = 300,
            DataContext = new ReviewViewModel(_model.Service, _fileId, _item.Row.Id, null, "scene-rows"),
        };
        Grid.SetRow(_review, 3);
        root.Children.Add(_review);

        _toolbar = new WrapPanel();
        Grid.SetRow(_toolbar, 4);
        root.Children.Add(_toolbar);

        _body = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        _body.Children.Add(WorkflowDialogs.Text("制作难度", 16));
        _level = new ComboBox
        {
            ItemsSource = new[] { "未标注", "红", "黄", "绿" }, SelectedItem = item.Row.LabelLevel ?? "未标注",
            Width = 130, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12), IsEnabled = item.Row.CanEdit,
        };
        _body.Children.Add(_level);
        _body.Children.Add(WorkflowDialogs.Text("拆分规划", 16));
        _plan = WorkflowDialogs.Input(item.Row.BreakdownPlan);
        _plan.IsReadOnly = !item.Row.CanEdit;
        _body.Children.Add(_plan);
        _serverPlan = WorkflowDialogs.Text("", 12);
        _body.Children.Add(_serverPlan);
        content.Children.Add(_body);

        var source = new StackPanel { Margin = new Thickness(12) };
        source.Children.Add(WorkflowDialogs.Text("所属场景", 16)); source.Children.Add(WorkflowDialogs.Text(item.Row.SceneContext));
        source.Children.Add(WorkflowDialogs.Text("出场信息", 16)); source.Children.Add(WorkflowDialogs.Text(item.Row.CastContext));
        source.Children.Add(WorkflowDialogs.Text("原文", 16)); source.Children.Add(WorkflowDialogs.Text(item.Row.StoryContent));
        content.Children.Add(new Expander { Header = "原文与上下文", Content = source, Margin = new Thickness(0, 0, 0, 12) });

        if (item.HasLinkedAssets)
        {
            var assetsPanel = new StackPanel { Margin = new Thickness(12) };
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
                var thumb = item.LinkedThumbnails.FirstOrDefault(t => t.Asset.Name.Contains(asset.Name));
                if (thumb != null)
                {
                    var img = new Image { Height = 80, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 6) };
                    BindingOperations.SetBinding(img, Image.SourceProperty, new Binding(nameof(ImageItem.Preview)) { Source = thumb });
                    cardStack.Children.Add(img);
                }
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
                Margin = new Thickness(0, 0, 0, 12)
            });
        }

        _toolbar.Children.Add(WorkflowDialogs.Button("保存", async (_, _) => await Execute(async () =>
        {
            if (_needsReload) { _status.Text = "请先重新加载并核对，草稿仍保留。"; return; }
            if (!_item.Row.CanEdit) { _status.Text = "当前账号没有正文编辑权限。"; return; }
            var label = _level.SelectedItem as string;
            _item.Row = await _model.Service.UpdateSceneAsync(_fileId, _item.Row.Id, new UpdateSceneRequest(_item.Row.Version, label == "未标注" ? null : label, _plan.Text));
            // Only a successful save advances the local baseline. A version refresh never does.
            // Input typed while this request was pending remains unsaved and will trigger close protection.
            _savedPlan = _item.Row.BreakdownPlan ?? "";
            _savedLevel = _item.Row.LabelLevel ?? "未标注";
            _model.SceneChanged(); _status.Text = "已保存。";
        })));
        // Declared before the button so the handler can name it: the reload reveals itself
        // when a save conflicts, and that handler is written before the button exists.
        Button reload = null!;
        reload = WorkflowDialogs.Button("重新加载", async (_, _) => await Execute(async () =>
        {
            var document = await _model.Service.OpenAsync(_fileId);
            var latest = document.Sheets.SelectMany(sheet => sheet.SceneRows).SingleOrDefault(row => row.Id == _item.Row.Id)
                ?? throw new System.IO.InvalidDataException("原节拍已不存在，请关闭后重新打开工作簿。");
            _item.Row = latest; _plan.IsReadOnly = !latest.CanEdit; _level.IsEnabled = latest.CanEdit; _model.SceneChanged();
            _serverPlan.Text = $"服务器当前等级：{latest.LabelLevel ?? "未标注"}\n服务器当前规划：{latest.BreakdownPlan}";
            _needsReload = false; reload.Visibility = Visibility.Collapsed;
            _body.BringIntoView();
            _status.Text = "草稿已保留，请核对服务器当前内容后再保存。";
        }));
        reload.Visibility = Visibility.Collapsed;
        _reload = reload; _toolbar.Children.Add(reload);
        // No 讨论 button here any more: the discussion is the section above it, so a button
        // that opened the same thing in a window would be a second way to do one thing.
        _toolbar.Children.Add(WorkflowDialogs.Button("来源", (_, _) =>
        {
            new SceneOriginsWindow(_model, _fileId, _item, _resources) { Owner = Window.GetWindow(this) }.ShowDialog(); _model.SceneChanged();
        }));
    }

    /// <summary>True when the panel holds input the server has not accepted.</summary>
    public bool HasDraft => !string.Equals(_plan.Text, _savedPlan, StringComparison.Ordinal)
        || !string.Equals(_level.SelectedItem as string ?? "未标注", _savedLevel, StringComparison.Ordinal);

    /// <summary>
    /// The successor to the window's Closing handler: true when it is safe to take the panel
    /// away. False while a request is in flight — the same refusal the window made, because
    /// replacing a panel mid-save would drop the reply that is still coming — or when the
    /// user asked to keep an unsaved draft.
    /// </summary>
    public bool TryLeave()
    {
        if (_busy) return false;
        // The comment composer holds a draft of its own, and it is a separate refusal: a
        // half-written note is as easy to lose as a half-written plan.
        if (!_review.TryLeave()) return false;
        if (!HasDraft) return true;
        var owner = Window.GetWindow(this);
        return MessageBox.Show(owner, "规划或等级尚未保存，是否放弃当前修改？", "保留草稿", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    }

    private async Task Execute(Func<Task> action)
    {
        if (_busy) return; _busy = true; _toolbar.IsEnabled = false;
        try { await action(); }
        catch (WorkbookApiException ex) when (ex.StatusCode == 409)
        {
            _needsReload = true; _reload.Visibility = Visibility.Visible;
            _status.Text = "内容已变更，草稿已保留。请重新加载后核对，再保存。";
        }
        catch (System.IO.InvalidDataException ex) { _status.Text = ex.Message; }
        catch (Exception ex)
        {
            _needsReload = true; _reload.Visibility = Visibility.Visible;
            _status.Text = ex is WorkbookApiException ? ex.Message : "操作未完成，草稿已保留，请重新加载确认结果。";
        }
        finally { _toolbar.IsEnabled = true; _busy = false; }
    }
}

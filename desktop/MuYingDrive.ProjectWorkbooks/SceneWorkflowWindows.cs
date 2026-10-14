using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace MuYingDrive.ProjectWorkbooks;

public sealed class SceneDetailsWindow : Window
{
    public SceneDetailsWindow(ProjectWorkbooksViewModel model, string fileId, SceneRowItem item, ResourceDictionary resources)
    {
        Title = $"场景 {item.Row.SceneNumber} · 节拍 {item.Row.BeatNumber}"; Width = 880; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkflowDialogs.Theme(this, resources);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var status = WorkflowDialogs.Text("", 12); DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var toolbar = new WrapPanel(); DockPanel.SetDock(toolbar, Dock.Bottom); root.Children.Add(toolbar);
        var content = new StackPanel(); root.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var notes = new StackPanel { Margin = new Thickness(12) };
        var body = new StackPanel { Margin = new Thickness(12) };
        var source = new StackPanel { Margin = new Thickness(12) };
        notes.Children.Add(WorkflowDialogs.Text("原批注", 16));
        content.Children.Add(notes); content.Children.Add(body);
        content.Children.Add(new Expander { Header = "原文与上下文", Content = source, Margin = new Thickness(12) });
        source.Children.Add(WorkflowDialogs.Text("所属场景", 16)); source.Children.Add(WorkflowDialogs.Text(item.Row.SceneContext));
        source.Children.Add(WorkflowDialogs.Text("出场信息", 16)); source.Children.Add(WorkflowDialogs.Text(item.Row.CastContext));
        source.Children.Add(WorkflowDialogs.Text("原文", 16)); source.Children.Add(WorkflowDialogs.Text(item.Row.StoryContent));
        body.Children.Add(WorkflowDialogs.Text("制作难度", 16));
        var level = new ComboBox { ItemsSource = new[] { "未标注", "红", "黄", "绿" }, SelectedItem = item.Row.LabelLevel ?? "未标注", Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12), IsEnabled = item.Row.CanEdit }; body.Children.Add(level);
        body.Children.Add(WorkflowDialogs.Text("拆分规划", 16)); var plan = WorkflowDialogs.Input(item.Row.BreakdownPlan); plan.IsReadOnly = !item.Row.CanEdit; body.Children.Add(plan);
        var savedPlan = plan.Text;
        var savedLevel = level.SelectedItem as string ?? "未标注";
        bool HasDraft() => !string.Equals(plan.Text, savedPlan, StringComparison.Ordinal)
            || !string.Equals(level.SelectedItem as string ?? "未标注", savedLevel, StringComparison.Ordinal);
        var serverPlan = WorkflowDialogs.Text("", 12); body.Children.Add(serverPlan);
        foreach (var note in item.Row.LegacyNotes)
        {
            notes.Children.Add(WorkflowDialogs.Text($"{note.Label} · {note.SourceCell}", 13));
            notes.Children.Add(WorkflowDialogs.Text(string.IsNullOrEmpty(note.Text) ? "（空）" : note.Text));
        }
        if (item.Row.LegacyNotes.Count == 0) notes.Children.Add(WorkflowDialogs.Text("未返回原批注列。"));
        bool busy = false;
        bool needsReload = false;
        Button? reload = null;
        async Task Execute(Func<Task> action)
        {
            if (busy) return; busy = true; toolbar.IsEnabled = false;
            try { await action(); }
            catch (WorkbookApiException ex) when (ex.StatusCode == 409) { needsReload = true; if (reload != null) reload.Visibility = Visibility.Visible; status.Text = "内容已变更，草稿已保留。请重新加载后核对，再保存。"; }
            catch (System.IO.InvalidDataException ex) { status.Text = ex.Message; }
            catch (Exception ex) { needsReload = true; if (reload != null) reload.Visibility = Visibility.Visible; status.Text = ex is WorkbookApiException ? ex.Message : "操作未完成，草稿已保留，请重新加载确认结果。"; }
            finally { toolbar.IsEnabled = true; busy = false; }
        }
        toolbar.Children.Add(WorkflowDialogs.Button("保存", async (_, _) => await Execute(async () =>
        {
            if (needsReload) { status.Text = "请先重新加载并核对，草稿仍保留。"; return; }
            if (!item.Row.CanEdit) { status.Text = "当前账号没有正文编辑权限。"; return; }
            var label = level.SelectedItem as string;
            item.Row = await model.Service.UpdateSceneAsync(fileId, item.Row.Id, new UpdateSceneRequest(item.Row.Version, label == "未标注" ? null : label, plan.Text));
            // Only a successful save advances the local baseline. A version refresh never does.
            // Input typed while this request was pending remains unsaved and will trigger close protection.
            savedPlan = item.Row.BreakdownPlan ?? "";
            savedLevel = item.Row.LabelLevel ?? "未标注";
            model.SceneChanged(); status.Text = "已保存。";
        })));
        reload = WorkflowDialogs.Button("重新加载", async (_, _) => await Execute(async () =>
        {
            var document = await model.Service.OpenAsync(fileId);
            var latest = document.Sheets.SelectMany(sheet => sheet.SceneRows).SingleOrDefault(row => row.Id == item.Row.Id)
                ?? throw new System.IO.InvalidDataException("原节拍已不存在，请关闭后重新打开工作簿。");
            item.Row = latest; plan.IsReadOnly = !latest.CanEdit; level.IsEnabled = latest.CanEdit; model.SceneChanged();
            serverPlan.Text = $"服务器当前等级：{latest.LabelLevel ?? "未标注"}\n服务器当前规划：{latest.BreakdownPlan}";
            needsReload = false; reload!.Visibility = Visibility.Collapsed;
            body.BringIntoView();
            status.Text = "草稿已保留，请核对服务器当前内容后再保存。";
        }));
        reload.Visibility = Visibility.Collapsed; toolbar.Children.Add(reload);
        toolbar.Children.Add(WorkflowDialogs.Button("讨论", (_, _) =>
        {
            var review = new ReviewWindow(new ReviewViewModel(model.Service, fileId, item.Row.Id, null, "scene-rows")) { Owner = this };
            WorkflowDialogs.Theme(review, resources); review.ShowDialog();
        }));
        toolbar.Children.Add(WorkflowDialogs.Button("来源", (_, _) =>
        {
            new SceneOriginsWindow(model, fileId, item, resources) { Owner = this }.ShowDialog(); model.SceneChanged();
        }));
        Closing += (_, e) =>
        {
            if (busy) { e.Cancel = true; return; }
            if (HasDraft() && MessageBox.Show(this,
                "规划或等级尚未保存，是否放弃当前修改？", "保留草稿", MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }
}

internal sealed record OriginChoice(SceneOrigin Origin, string Label);
public sealed class SceneOriginsWindow : Window
{
    public SceneOriginsWindow(ProjectWorkbooksViewModel model, string fileId, SceneRowItem current, ResourceDictionary resources)
    {
        Title = "来源"; Width = 880; Height = 680; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkflowDialogs.Theme(this, resources);
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var status = WorkflowDialogs.Text(""); DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var revisions = new ComboBox { DisplayMemberPath = nameof(ProjectFile.Name), Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(revisions, Dock.Top); root.Children.Add(revisions);
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); root.Children.Add(grid);
        var available = new ListBox { SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(OriginChoice.Label), Margin = new Thickness(0, 0, 8, 0) }; grid.Children.Add(available);
        var selectedItems = new ObservableCollection<OriginChoice>(current.Row.Origins.Select(origin => new OriginChoice(origin, origin.FileId + " / " + origin.RowId)));
        var savedOrigins = current.Row.Origins.ToHashSet();
        var selected = new ListBox { ItemsSource = selectedItems, SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(OriginChoice.Label) }; Grid.SetColumn(selected, 1); grid.Children.Add(selected);
        foreach (var list in new[] { available, selected }) { list.SetResourceReference(Control.BackgroundProperty, "Brush.Surface.Panel"); list.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary"); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled); }
        bool busy = false;
        bool needsReload = false;
        Button? reload = null;
        async Task Execute(Func<Task> action)
        {
            if (busy) return; busy = true; actions.IsEnabled = false; revisions.IsEnabled = false;
            try { await action(); }
            catch (System.IO.InvalidDataException ex) { status.Text = ex.Message; }
            catch (Exception ex) { needsReload = true; if (reload != null) reload.Visibility = Visibility.Visible; status.Text = ex is WorkbookApiException ? ex.Message : "来源读取或保存失败，请重新加载；选择已保留。"; }
            finally { busy = false; actions.IsEnabled = true; revisions.IsEnabled = true; }
        }
        Loaded += async (_, _) => await Execute(async () =>
        { revisions.ItemsSource = (await model.Service.ListRevisionsAsync(fileId)).Where(file => file.Id != fileId && file.ProjectId == model.Document?.File.ProjectId).ToList(); });
        revisions.SelectionChanged += async (_, _) =>
        {
            if (revisions.SelectedItem is not ProjectFile file) return;
            await Execute(async () =>
            {
                // Never label rows from the previous revision as belonging to a failed new selection.
                // Existing selected origins on the right remain intact throughout loading/failure.
                available.ItemsSource = null;
                var document = await model.Service.OpenAsync(file.Id);
                available.ItemsSource = document.Sheets.SelectMany(sheet => sheet.SceneRows.Select(row => new OriginChoice(new SceneOrigin(file.Id, row.Id),
                    $"{sheet.Name} · 场景 {row.SceneNumber} · 节拍 {row.BeatNumber}\n{row.StoryContent}"))).ToList();
            });
        };
        actions.Children.Add(WorkflowDialogs.Button("添加 →", (_, _) =>
        { foreach (var choice in available.SelectedItems.Cast<OriginChoice>()) if (!selectedItems.Any(c => c.Origin == choice.Origin)) selectedItems.Add(choice); }));
        actions.Children.Add(WorkflowDialogs.Button("移除", (_, _) => { foreach (var choice in selected.SelectedItems.Cast<OriginChoice>().ToList()) selectedItems.Remove(choice); }));
        actions.Children.Add(WorkflowDialogs.Button("旧讨论", (_, _) =>
        {
            var choice = selected.SelectedItem as OriginChoice ?? available.SelectedItem as OriginChoice; if (choice == null) return;
            var review = new ReviewWindow(new ReviewViewModel(model.Service, choice.Origin.FileId, choice.Origin.RowId, null, "scene-rows")) { Owner = this };
            WorkflowDialogs.Theme(review, resources); review.ShowDialog();
        }));
        actions.Children.Add(WorkflowDialogs.Button("保存", async (_, _) => await Execute(async () =>
        {
            if (needsReload) { status.Text = "请先重新加载并核对，选择已保留。"; return; }
            if (!current.Row.CanEdit) throw new System.IO.InvalidDataException("当前账号没有修改来源的权限。");
            if (selectedItems.Count > 100) throw new System.IO.InvalidDataException("最多关联 100 个来源。");
            if (selectedItems.Any(choice => choice.Origin.FileId == fileId)) throw new System.IO.InvalidDataException("来源必须来自其他修订，不能关联当前文件中的行。");
            current.Row = await model.Service.UpdateSceneOriginsAsync(fileId, current.Row.Id, new UpdateSceneOriginsRequest(current.Row.Version, selectedItems.Select(c => c.Origin).ToList()));
            savedOrigins = current.Row.Origins.ToHashSet();
            status.Text = "已保存。";
        })));
        reload = WorkflowDialogs.Button("重新加载", async (_, _) => await Execute(async () =>
        {
            available.ItemsSource = null;
            revisions.ItemsSource = (await model.Service.ListRevisionsAsync(fileId)).Where(file => file.Id != fileId && file.ProjectId == model.Document?.File.ProjectId).ToList();
            var document = await model.Service.OpenAsync(fileId);
            current.Row = document.Sheets.SelectMany(sheet => sheet.SceneRows).SingleOrDefault(row => row.Id == current.Row.Id)
                ?? throw new System.IO.InvalidDataException("原节拍已不存在，请关闭后重新打开工作簿。");
            needsReload = false; reload!.Visibility = Visibility.Collapsed;
            status.Text = $"服务器当前关联 {current.Row.Origins.Count} 个来源。已保留右侧选择，请核对后再保存。";
        }));
        reload.Visibility = Visibility.Collapsed; actions.Children.Add(reload);
        Closing += (_, e) =>
        {
            if (busy) { e.Cancel = true; return; }
            if (!savedOrigins.SetEquals(selectedItems.Select(choice => choice.Origin)) && MessageBox.Show(this,
                "来源关联尚未保存，是否放弃当前修改？", "保留来源选择", MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }
}

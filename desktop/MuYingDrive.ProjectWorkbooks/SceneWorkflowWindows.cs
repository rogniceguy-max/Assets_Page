using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace MuYingDrive.ProjectWorkbooks;

// HOST ADJUSTMENT. SceneDetailsWindow was here. It is now SceneDetailsPanel.cs — a modal
// window per beat became a panel that stays beside the list. The state machine moved with it
// unchanged; see the header comment there.

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
            current.Row = await model.Service.UpdateSceneOriginsAsync(fileId, current.Row.Id,
                new UpdateSceneOriginsRequest(current.Row.FieldVersions.GetValueOrDefault("origins", current.Row.Version),
                    selectedItems.Select(c => c.Origin).ToList()));
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

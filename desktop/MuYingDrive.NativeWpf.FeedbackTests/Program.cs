using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MuYingDrive.NativeWpf.Models;
using MuYingDrive.NativeWpf.Services;
using MuYingDrive.NativeWpf.ViewModels;
using System.Net.Http;
using MuYingDrive.ProjectWorkbooks;
using MuYingDrive.NativeWpf.Views;
using Imazen.WebP;

internal static class Program
{
    private static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (args.Contains("--webp-smoke", StringComparer.Ordinal))
            {
                VerifyWebPDecode();
                Console.WriteLine("PASS: bundled WebP decoder returns a frozen, WPF-ready BGRA bitmap.");
                application.Shutdown();
                return 0;
            }
            if (args.Contains("--thumbnail-cache-smoke", StringComparer.Ordinal))
            {
                var cacheSettings = new NasClientSettings(new Uri("https://thumbnail-test.invalid"), new string('0', 64));
                var cacheApi = new V2ApiClient(new V2ApiClientOptions(cacheSettings.ApiBaseUri, cacheSettings.CertificateSha256));
                var cacheSession = new DriveSession(
                    new V2UserSession { Id = 0, UserName = "访客", Department = "缩略图测试", GroupType = "visitor" },
                    "thumbnail-cache-test-token");
                var cacheAgent = new RustTransferAgentClient(
                    FileClientIdentity.GetVisitorAgentNamespace(cacheSettings.ApiBaseUri, "thumbnail-cache-test-token"),
                    userSid: "S-1-5-21-thumbnail-smoke");
                var cacheTransfers = new TransferViewModel(cacheApi, cacheSession, cacheSettings, cacheAgent,
                    Guid.NewGuid().ToString("D"));
                try
                {
                    VerifyBrokenThumbnailCacheRecovery(new FileThumbnailProvider(cacheApi, cacheSession, cacheTransfers,
                        Guid.NewGuid().ToString("D"))).GetAwaiter().GetResult();
                }
                finally
                {
                    cacheTransfers.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    cacheApi.Dispose();
                }
                application.Shutdown();
                return 0;
            }
            var bindingErrors = new BindingErrors();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var output = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "muying-feedback-render");
            Directory.CreateDirectory(output);
            var settings = new NasClientSettings(new Uri("https://feedback-test.invalid"), new string('0', 64));
            VerifyWebPDecode();
            Assert(TransferTaskStateProjection.Label("committing") == "提交中", "Rust commit state must have a distinct inline label.");
            Assert(TransferTaskStateProjection.Label("linking") == "等待关联", "Transfer completion must not be confused with business linking.");
            Assert(TransferTaskStateProjection.Progress(150, 100, "transferring") == 100,
                "Reported transfer progress must be clamped to the declared byte total.");
            Assert(TransferTaskStateProjection.Progress(0, 0, "completed") == 100,
                "A completed task must render complete even when the agent omitted a byte total.");
            Assert(!TransferTaskStateProjection.CanCancel("committing", true),
                "A task whose commit began must not expose cancellation as if rollback were possible.");
            Assert(!TransferTaskStateProjection.CanCancel("failed", false),
                "A terminal failed task must expose retry, not an invalid cancel action.");
            Assert(!LocalUploadWalker.IsSafeSegmentName("../outside") && !LocalUploadWalker.IsSafeSegmentName("a\\b") &&
                   LocalUploadWalker.IsSafeSegmentName("素材 1"),
                "Local upload path segments must reject traversal and separators while allowing ordinary names.");
            var restoredDownload = JsonSerializer.Deserialize<TransferAgentTaskSnapshot>("""
                {"task_id":"fixture-operation","operation_id":"fixture-operation","kind":"download","source":"project_workbook","state":"transferring","file_id":"file-fixture","revision_id":"revision-fixture","destination_path":"/cache/file-fixture.webp","scope_id":"project:fixture:scene-video","project_id":"project-fixture","bytes_total":100,"bytes_done":40,"updated_at":100}
                """)!;
            Assert(restoredDownload.DestinationPath == "/cache/file-fixture.webp" && restoredDownload.ScopeId == "project:fixture:scene-video",
                "A Rust download task must retain the destination and project scope needed to resume its inline video card.");
            var videoCard = new SceneVideoItem(new VideoItem("video-fixture", "scene.mp4", "scene/scene.mp4")
            { FileId = "file-fixture" });
            videoCard.ApplyTransfer(new ProjectTransferTaskSnapshot("fixture-operation", "transferring", 40, 100,
                null, "file-fixture", "revision-fixture", "download", "project_workbook", "project:fixture:scene-video",
                "project-fixture", false, true, DateTimeOffset.UtcNow));
            Assert(videoCard.DownloadStatus == "下载中" && videoCard.Progress == 40 && videoCard.CanCancel,
                "A scene video card must display the real transfer progress and cancellation state.");
            videoCard.ApplyTransfer(new ProjectTransferTaskSnapshot("fixture-operation", "failed", 40, 100,
                "network failed", "file-fixture", "revision-fixture", "download", "project_workbook", "project:fixture:scene-video",
                "project-fixture", true, false, DateTimeOffset.UtcNow));
            Assert(videoCard.CanRetry && videoCard.HasDownloadError,
                "A retryable video download failure must stay on that video row with an actionable retry state.");

            var recoveryScope = new string('A', 64);
            var recoveryId = Guid.NewGuid().ToString("D");
            var recoveryPath = Path.Combine(Path.GetTempPath(), "muying-recovery-fixture.png");
            var recovery = new PendingWorkbookTransferOperation(recoveryScope, recoveryId, "asset_image",
                "project-fixture", "workbook-fixture", "row-fixture", recoveryPath, "fixture.png", false,
                12, 34, "关联失败，可重试", "fixture error", null, null);
            try
            {
                recovery.Save();
                Assert(PendingWorkbookTransferOperation.LoadAll(recoveryScope).Single(item => item.OperationId == recoveryId) == recovery,
                    "A pending business-link operation must restore its exact operation, scope, row, and local source metadata.");
            }
            finally { recovery.Remove(); }

            var transferOnlyCard = new WorkbookUploadCard(recoveryPath, true)
            { OperationId = recoveryId };
            transferOnlyCard.ApplyTransfer(new ProjectTransferTaskSnapshot(recoveryId, "completed", 12, 12,
                null, "file-fixture", "revision-fixture", "upload", "wpf", "project:fixture",
                "project-fixture", false, false, DateTimeOffset.UtcNow));
            Assert(transferOnlyCard.State != "已完成",
                "Rust transfer completion alone must never mark an asset row link as complete.");

            var session = new DriveSession(new V2UserSession { Id = 1, UserName = "fixture-only", Department = "fixture-only", GroupType = "film", Spaces = new() { ["film"] = "/film", ["writing"] = "/writing", ["public"] = "/public", ["personal"] = "/personal/fixture" } }, "fixture-token");
            var employeeNamespace = FileClientIdentity.GetEmployeeAgentNamespace(settings.ApiBaseUri, 42);
            Assert(employeeNamespace == FileClientIdentity.GetEmployeeAgentNamespace(settings.ApiBaseUri, 42), "Employee agent namespace must be stable across sign-ins.");
            Assert(employeeNamespace != FileClientIdentity.GetEmployeeAgentNamespace(settings.ApiBaseUri, 43), "Different employees must not share a local agent queue.");
            Assert(employeeNamespace != FileClientIdentity.GetEmployeeAgentNamespace(new Uri("https://other-test.invalid"), 42),
                "The same employee on a different service must not share a local agent queue.");
            var syncConfigPath = FileClientIdentity.GetEmployeeSyncConfigPath(settings.ApiBaseUri, 42);
            Assert(syncConfigPath == FileClientIdentity.GetEmployeeSyncConfigPath(settings.ApiBaseUri, 42), "Employee sync configuration must be stable across sign-ins.");
            Assert(syncConfigPath != FileClientIdentity.GetEmployeeSyncConfigPath(settings.ApiBaseUri, 43) &&
                   Path.GetFileName(Path.GetDirectoryName(syncConfigPath)) is not ("42" or "43"),
                "Different employees must use isolated sync configuration without exposing account IDs in the path.");
            Assert(syncConfigPath != FileClientIdentity.GetEmployeeSyncConfigPath(new Uri("https://other-test.invalid"), 42),
                "The same employee on a different service must not reuse sync configuration.");
            const string visitorToken = "visitor-secret-fixture";
            var visitorNamespace = FileClientIdentity.GetVisitorAgentNamespace(settings.ApiBaseUri, visitorToken);
            Assert(visitorNamespace == FileClientIdentity.GetVisitorAgentNamespace(settings.ApiBaseUri, visitorToken), "Visitor namespace must be stable for one visitor session.");
            Assert(visitorNamespace != FileClientIdentity.GetVisitorAgentNamespace(settings.ApiBaseUri, "another-visitor-session") &&
                   visitorNamespace != FileClientIdentity.GetVisitorAgentNamespace(new Uri("https://other-test.invalid"), visitorToken) &&
                   !visitorNamespace.Contains(visitorToken, StringComparison.Ordinal),
                "Visitor sessions must use distinct namespaces without exposing their bearer tokens.");
            using var model = new FeedbackViewModel(session, settings);
            var item = new FeedbackItem(new string('a', 32), "problem", "upload", "上传暂停后无法继续", "点击继续按钮后，没有恢复上传。\n复现：上传文件、暂停，再点击继续。", "in_progress", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
            model.Items.Add(item);
            model.Items.Add(item with { FeedbackId = new string('b', 32), Kind = "suggestion", Title = "希望增加文件收藏功能", Status = "evaluating" });
            model.Draft.Title = "希望支持快速定位文件";
            model.Draft.Description = "在较多文件的目录中，希望更快找到最近使用的资料。";
            Assert(!model.Draft.SubmitCommand.CanExecute(null), "Submission must require privacy confirmation.");
            model.Draft.PrivacyConfirmed = true;
            Assert(model.Draft.SubmitCommand.CanExecute(null), "Valid anonymous draft must be submittable.");
            var bitmap = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)160, 64 * 32 * 4).ToArray(), 64 * 4);
            var imageItem = new ImageItem(new ImageAsset("asset-fixture", "cover", "image/webp", 64, 32)
            {
                CurrentRevisionId = "revision-1"
            }, bitmap);
            imageItem.UpdateAsset(imageItem.Asset with { CurrentRevisionId = "revision-2" });
            Assert(imageItem.Preview is null, "A changed image revision must invalidate its displayed thumbnail.");
            var preview = FeedbackImageService.FromClipboard(bitmap);
            model.Draft.AddImage(preview);
            Assert(!model.Draft.PrivacyConfirmed, "Changing images requires another privacy confirmation.");
            for (int n = 0; n < 4; n++) model.Draft.AddImage(preview);
            Assert(model.Draft.Images.Count == 4, "Image count limit must hold.");
            model.Draft.Images.Clear(); model.Draft.AddImage(preview); model.Draft.Error = "";
            var json = JsonSerializer.Serialize(new FeedbackSubmission("suggestion", "general", "title", "description", [], true), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert(!json.Contains("fixture") && !json.Contains("author") && !json.Contains("user"), "Submission must have no identity field.");
            var failed = new TransferTask(new ResumableTransferSnapshot("secret-task-id", "secret-user-file.png", ResumableTransferDirection.Upload, ResumableTransferStatus.Failed, 0, 100, @"C:\Users\secret-user", 0, "Bearer secret-token at 10.0.0.1", DateTimeOffset.UtcNow));
            model.Draft.IsAdding = true;
            Assert(!model.Draft.SubmitCommand.CanExecute(null), "Submission must wait for screenshot preparation.");
            model.Draft.IsAdding = false;
            var main = new MainViewModel();
            var workspace = WorkspaceViewModel.ForEmployee(main, session, settings);
            // Employee-only pages are loaded in the same workspace shell as read-only sessions.
            typeof(FeedbackViewModel).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace.Feedback, true);
            workspace.Transfers!.Tasks.Add(failed);
            workspace.Files!.SearchText = "fixture-browse-filter";
            var files = workspace.Files;
            var transfers = workspace.Transfers;
            typeof(FilesViewModel).GetField("_scope", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(files, new LogicalFileScope("project:project-fixture", "project-fixture"));
            typeof(FilesViewModel).GetField("_isRecycleBin", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(files, true);
            // The workspace constructor starts an asynchronous directory load. This
            // assertion tests role/scope permission, so model a settled recycle-bin view.
            typeof(FilesViewModel).GetField("_isLoading", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(files, false);
            Assert(files.IsRecycleBin && files.CanRestore,
                "An employee must be able to restore a soft-deleted file from a project scope.");
            workspace.NavigateCommand.Execute("反馈");
            Assert(ReferenceEquals(workspace.CurrentContent, workspace.Feedback), "Feedback must be a peer workspace.");
            workspace.NavigateCommand.Execute("传输");
            Assert(ReferenceEquals(workspace.CurrentContent, transfers) && transfers.Tasks.Contains(failed), "Navigation must retain the transfer queue instance.");
            workspace.NavigateCommand.Execute("视频");
            Assert(ReferenceEquals(workspace.CurrentContent, files) && files.SearchText == "fixture-browse-filter", "Navigation must retain file browsing state.");
            workspace.NavigateCommand.Execute("图片");
            Assert(ReferenceEquals(workspace.CurrentContent, files), "Navigation must retain file browsing state for 图片.");

            // --- the shape of the sidebar ---------------------------------------------------
            var navKeys = workspace.NavigationItems.Select(item => item.Key).ToArray();
            Assert(navKeys.SequenceEqual(["图片", "视频", "个人空间", "同步", "传输", "反馈"]),
                "The global navigation is 图片/视频/个人空间/同步/传输/反馈: " + string.Join("/", navKeys));
            Assert(workspace.SpaceItems.Count == 0, "The department spaces are gone from the sidebar.");
            foreach (var gone in new[] { "文件", "本部门", "其他部门", "公共素材" })
            {
                Assert(!navKeys.Contains(gone), $"'{gone}' must not be a navigation entry any more.");
                Assert(!workspace.SpaceItems.Any(item => item.Key == gone), $"'{gone}' must not be a space entry.");
            }

            var tree = ProjectNavigation.ForProject("211953005", "孟婆",
                [new ProjectNavigation.WorkbookEntry("file-id", "孟婆2_场景节拍表")]);
            Assert(tree.Kind == ProjectNodeKind.Project && tree.Name == "孟婆", "A project row is the project.");
            Assert(tree.Id == "211953005", "A project row is identified by the backend's id, not its name.");
            Assert(tree.IsProject, "Project row IsProject is true.");
            Assert(!tree.IsWorkbook, "Project row IsWorkbook is false.");
            Assert(tree.Children.Select(child => child.Name).SequenceEqual(["工作簿", "图片", "视频"]),
                "A project carries its workbooks and the two media views.");
            var workbookNode = tree.Children[0].Children.Single();
            Assert(workbookNode.Id == "file-id",
                "A workbook row carries the file id the document route takes.");
            Assert(workbookNode.IsWorkbook, "Workbook row IsWorkbook is true.");
            Assert(workbookNode.IsOpenable, "Workbook row IsOpenable is true.");
            Assert(!workbookNode.IsProject, "Workbook row IsProject is false.");
            Assert(tree.Children.All(child => child.ProjectId == "211953005"),
                "Every row knows which project it belongs to.");
            Assert(new ProjectInfo("211953005", "孟婆").ToString() == "孟婆",
                "ProjectInfo.ToString() must return only Name.");

            Assert(workspace.OpenProjectNodeCommand != null, "OpenProjectNodeCommand must be present.");
            Assert(workspace.RenameProjectNodeCommand != null, "RenameProjectNodeCommand must be present.");
            Assert(workspace.DeleteWorkbookProjectNodeCommand != null, "DeleteWorkbookProjectNodeCommand must be present.");
            Assert(workspace.DeleteProjectNodeCommand != null, "DeleteProjectNodeCommand must be present.");

            // --- the names a duplicate gets ---------------------------------------------
            Assert(UniqueName.WithSuffix("照片.jpg", 2) == "照片 (2).jpg", "A duplicate is numbered before its extension.");
            Assert(UniqueName.WithSuffix("照片 (2).jpg", 3) == "照片 (3).jpg", "Numbering a numbered name must not stack suffixes.");
            Assert(UniqueName.WithSuffix("archive.tar.gz", 2) == "archive.tar (2).gz", "Only the last extension separates the suffix.");
            Assert(UniqueName.WithSuffix(".gitignore", 2) == ".gitignore (2)", "A leading dot belongs to the name, not the extension.");
            Assert(UniqueName.WithoutSuffix("照片 (2).jpg") == "照片.jpg", "The existing suffix is stripped for the next candidate.");
            Assert(UniqueName.WithoutSuffix("照片.jpg") == "照片.jpg", "A plain name is already a base name.");

            // --- what a folder selection expands to ---------------------------------------
            var walkRoot = Path.Combine(Path.GetTempPath(), "muying-walk-" + Guid.NewGuid().ToString("N"));
            var chosen = Path.Combine(walkRoot, "素材");
            Directory.CreateDirectory(Path.Combine(chosen, "子目录"));
            Directory.CreateDirectory(Path.Combine(chosen, "空目录"));
            File.WriteAllText(Path.Combine(chosen, "a.txt"), "a");
            File.WriteAllText(Path.Combine(chosen, "子目录", "b.txt"), "b");
            var plan = LocalUploadWalker.Enumerate(chosen);
            Assert(plan.Files.Count == 2, "Both files are found.");
            Assert(plan.Files.All(entry => entry.RelativePath.StartsWith("素材" + Path.DirectorySeparatorChar)),
                "Relative paths keep the chosen folder as their first segment.");
            Assert(plan.Directories.Contains(Path.Combine("素材", "空目录")),
                "An empty folder is still in the plan; otherwise the uploaded shape is not the chosen shape.");
            Directory.Delete(walkRoot, recursive: true);

            // A visitor uses the standard file browser with a read-only DriveSession.
            // Construct it offline so rendering verifies its hidden mutation affordances.
            var visitorApi = new V2ApiClient(new V2ApiClientOptions(new Uri("https://feedback-test.invalid"), new string('0', 64)));
            var visitorSession = new DriveSession(
                new V2UserSession { Id = 0, UserName = "访客", Department = "只读访问", GroupType = "visitor" },
                visitorToken);
            var visitorAgent = new RustTransferAgentClient(visitorNamespace, userSid: "S-1-5-21-render-harness");
            var visitorTransfers = new TransferViewModel(visitorApi, visitorSession, settings, visitorAgent,
                Guid.NewGuid().ToString("D"));
            VerifyBrokenThumbnailCacheRecovery(new FileThumbnailProvider(visitorApi, visitorSession,
                visitorTransfers, Guid.NewGuid().ToString("D"))).GetAwaiter().GetResult();
            var visitor = new FilesViewModel(visitorApi, visitorSession, visitorTransfers, visitorAgent,
                Guid.NewGuid().ToString("D"));
            var readOnlyWorkspace = WorkspaceViewModel.ForSession(main, visitorSession, settings);
            var readOnlyNavKeys = readOnlyWorkspace.NavigationItems.Select(item => item.Key).ToArray();
            Assert(readOnlyNavKeys.SequenceEqual(navKeys),
                "Read-only visitors use the same workspace navigation as employees.");
            Assert(readOnlyWorkspace.NavigationItems.Where(item => item.Key is "个人空间" or "同步" or "反馈")
                    .All(item => !item.IsEnabled),
                "A read-only session sees the common navigation but cannot open personal, sync, or feedback features.");
            Assert(readOnlyWorkspace.NavigationItems.Where(item => item.Key is "图片" or "视频" or "传输")
                    .All(item => item.IsEnabled),
                "A read-only session can browse shared media and monitor its own transfers.");
            var selectionBeforeDisabledNavigation = readOnlyWorkspace.SelectedNavigation;
            readOnlyWorkspace.NavigateCommand.Execute("同步");
            Assert(readOnlyWorkspace.SelectedNavigation == selectionBeforeDisabledNavigation,
                "Invoking a disabled read-only navigation item has no effect.");
            typeof(FilesViewModel).GetField("_scope", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(visitor, new LogicalFileScope("project:project-fixture", "project-fixture"));
            typeof(FilesViewModel).GetField("_isRecycleBin", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(visitor, true);
            Assert(visitor.IsReadOnly && !visitor.CanShowRecycleBin && !visitor.CanRestore,
                "A visitor must remain read-only in project scopes.");

            foreach (var theme in new[] { "Light", "Dark" })
            {
                application.Resources.MergedDictionaries.Clear();
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MuYingDrive.NativeWpf;component/Themes/{theme}.xaml") });
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MuYingDrive.NativeWpf;component/Themes/SharedStyles.xaml") });
                Render(new FeedbackView { DataContext = model }, 960, 680, 96, Path.Combine(output, $"list-{theme}.png"));
                workspace.Feedback!.Items.Clear(); workspace.Feedback.Items.Add(item);
                workspace.NavigateCommand.Execute("反馈");
                Render(new WorkspaceView { DataContext = workspace }, 1200, 720, 96, Path.Combine(output, $"workspace-{theme}.png"));
                Render(new WorkspaceView { DataContext = readOnlyWorkspace }, 1200, 720, 96,
                    Path.Combine(output, $"workspace-readonly-{theme}.png"));
                workspace.ToggleSidebarCommand.Execute(null);
                Render(new WorkspaceView { DataContext = workspace }, 900, 600, 96, Path.Combine(output, $"workspace-collapsed-{theme}.png"));
                workspace.ToggleSidebarCommand.Execute(null);
                Render(new TransferView { DataContext = workspace.Transfers }, 960, 480, 96, Path.Combine(output, $"transfer-failure-{theme}.png"));
                Render(new FilesView { DataContext = visitor }, 1100, 680, 96, Path.Combine(output, $"visitor-file-readonly-{theme}.png"));

                // The two dialogs are new windows, and the render harness is what proves
                // their XAML resolves against the shared theme dictionaries.
                var confirm = new ConfirmDialog(
                    "确认删除", "删除后无法撤销。", ["素材（文件夹，内含 42 项）", "旧版封面.png"], "删除");
                Assert(((Button)confirm.FindName("CancelButton")!).IsDefault,
                    "Enter must cancel the delete dialog rather than confirm it.");
                var confirmContent = (FrameworkElement)confirm.Content;
                confirm.Content = null;
                Render(confirmContent, 460, 340, 96, Path.Combine(output, $"confirm-{theme}.png"));
                confirm.Close();

                // The workbook page is a separate library with its own XAML and its own local
                // resource fallbacks. Rendering it is what shows they resolve — a
                // StaticResource key that does not exist compiles fine and throws on load.
                var workbookClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var workbook = new ProjectWorkbooksViewModel(new HttpProjectWorkbookService(
                    workbookClient, new Uri("https://workbooks.invalid/api/v2/project-workbooks/"),
                    () => "fixture-token",
                    new WorkbookTransportOptions { MaxUploadBytes = 64L * 1024 * 1024 },
                    () => "1"));
                // Loaded never fires for a tree with no window, so the adoption that makes the
                // library look like the host has never been exercised here — the buttons and
                // scrollbars in these screenshots were the library's own fallbacks. Invoking
                // it directly is what puts the host's Button and ScrollBar styles in the shot.
                var workbookView = new ProjectWorkbooksView { DataContext = workbook };
                typeof(ProjectWorkbooksView).GetMethod("AdoptHostTheme", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(workbookView, null);
                // Both are keyed lookups into the host's dictionaries, so renaming either key
                // there would quietly drop the library back to drawing its own controls rather
                // than failing. This is what notices.
                Assert(workbookView.Resources.Contains(typeof(Button))
                       && workbookView.Resources.Contains(typeof(System.Windows.Controls.Primitives.ScrollBar)),
                    "The library has to adopt the host's button and scrollbar styles.");
                Render(workbookView, 1100, 700, 96, Path.Combine(output, $"workbooks-{theme}.png"));

                // The scene detail is built in code rather than XAML, and it spent its life
                // inside a Window of its own — which is exactly where a DynamicResource lookup
                // it depends on could have been resolving without anything saying so.
                // Rendering it bare is what shows it stands up outside a window, because that
                // is where it now lives.
                var sceneItem = new SceneRowItem(new SceneRow("row-fixture", 1, "12", "3", "她把碗放下。", "红", "拆成两拍：递碗、转身。")
                {
                    SceneContext = "第二场 · 夜",
                    CastContext = "孟婆",
                    CanEdit = true,
                });
                var panel = new SceneDetailsPanel(workbook, "file-fixture", sceneItem, new ResourceDictionary());
                Assert(!panel.HasDraft, "A panel nobody has typed into holds no draft.");
                Assert(panel.Heading == "场景 12 · 节拍 3", "The panel names the beat it is showing: " + panel.Heading);
                Render(panel, 360, 620, 96, Path.Combine(output, $"scene-panel-{theme}.png"));

                // The panel cannot outlive the workbook it describes, and the host's project
                // tree can swap that workbook without this view being asked anything. The
                // guard therefore lives on the view model, and this is what holds it there:
                // a refusal must leave the open workbook alone rather than being ignored.
                var asked = 0;
                workbook.ConfirmLeaving = () => { asked++; return false; };
                workbook.OpenAsync(new ProjectFile("file-fixture", "孟婆2_场景节拍表", "scene_beats", 1)).GetAwaiter().GetResult();
                Assert(asked == 1, "Opening a workbook has to ask before it takes the open one away.");
                Assert(!workbook.IsOpen, "A refused open must not replace what is open.");
                workbook.ConfirmLeaving = null;
                workbookClient.Dispose();

                var rename = new TextInputWindow("重命名", "为“照片.jpg”输入新名称。", "照片.jpg", "重命名", 2);
                Assert(rename.Value == "照片.jpg", "The rename box starts from the current name.");
                var renameContent = (FrameworkElement)rename.Content;
                rename.Content = null;
                Render(renameContent, 440, 260, 96, Path.Combine(output, $"rename-{theme}.png"));
                rename.Close();
                typeof(FeedbackViewModel).GetProperty(nameof(FeedbackViewModel.CanManage))!.SetValue(model, true);
                typeof(FeedbackViewModel).GetProperty(nameof(FeedbackViewModel.Detail))!.SetValue(model, item);
                model.SelectedStatus = item.Status;
                model.DetailImages.Add(preview);
                Render(new FeedbackView { DataContext = model }, 660, 680, 144, Path.Combine(output, $"detail-{theme}-150dpi.png"));
                typeof(FeedbackViewModel).GetProperty(nameof(FeedbackViewModel.Detail))!.SetValue(model, null);
                model.DetailImages.Clear();
                typeof(FeedbackViewModel).GetProperty(nameof(FeedbackViewModel.CanManage))!.SetValue(model, false);
                var window = new FeedbackCreateWindow(model.Draft);
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                content.DataContext = model.Draft;
                content.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MuYingDrive.NativeWpf;component/Views/FeedbackStyles.xaml") });
                Render(content, 600, 620, 96, Path.Combine(output, $"create-{theme}.png"));
                window.Close();
                model.Message = "反馈服务暂时不可用，请稍后重试。";
                Render(new FeedbackView { DataContext = model }, 540, 480, 192, Path.Combine(output, $"error-{theme}-200dpi.png"));
                model.Message = "";
                var saved = model.Items.ToArray();
                model.Items.Clear();
                typeof(FeedbackViewModel).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, true);
                Render(new FeedbackView { DataContext = model }, 540, 480, 96, Path.Combine(output, $"empty-{theme}.png"));
                typeof(FeedbackViewModel).GetProperty(nameof(FeedbackViewModel.IsBusy))!.SetValue(model, true);
                Render(new FeedbackView { DataContext = model }, 540, 480, 96, Path.Combine(output, $"loading-{theme}.png"));
                typeof(FeedbackViewModel).GetProperty(nameof(FeedbackViewModel.IsBusy))!.SetValue(model, false);
                foreach (var savedItem in saved) model.Items.Add(savedItem);
            }
            visitor.Dispose();
            visitorTransfers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            visitorApi.Dispose();
            readOnlyWorkspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert(bindingErrors.Messages.Count == 0, string.Join("\n", bindingErrors.Messages));
            workspace.DisposeAsync().AsTask().GetAwaiter().GetResult(); main.Dispose();
            Console.WriteLine("PASS: draft privacy gate, image limits, anonymous serialization, common employee/read-only workspace navigation, retained workspace/queue instances, Light/Dark list/detail/form/empty/loading/error rendering, 150%/200% rendering; no binding errors.");
            application.Shutdown();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Render(FrameworkElement content, double width, double height, double dpi, string path)
    {
        var border = new Border { Background = (Brush)Application.Current.FindResource("Brush.Surface.Canvas"), Child = content, Width = width, Height = height };
        border.Measure(new Size(width, height)); border.Arrange(new Rect(0, 0, width, height)); border.UpdateLayout();
        border.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)(width * dpi / 96), (int)(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(border);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
        border.Child = null;
    }

    private static void VerifyWebPDecode()
    {
        var rgba = new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        };
        var webp = WebPEncoder.Encode(rgba, 2, 2, 8, WebPPixelFormat.Rgba, quality: -1);
        var decodedWebp = WebPBitmapDecoder.Decode(webp);
        var decodedPixels = new byte[rgba.Length];
        decodedWebp.CopyPixels(decodedPixels, 8, 0);
        Assert(decodedWebp.PixelWidth == 2 && decodedWebp.PixelHeight == 2 &&
               decodedWebp.IsFrozen && decodedPixels.SequenceEqual(new byte[]
               {
                   0, 0, 255, 255, 0, 255, 0, 255,
                   255, 0, 0, 255, 255, 255, 255, 255
               }),
            "Bundled WebP decoding must produce a frozen, WPF-ready BGRA bitmap without relying on a Windows codec.");
    }

    private static async Task VerifyBrokenThumbnailCacheRecovery(FileThumbnailProvider provider)
    {
        var malformedWebp = new byte[]
        {
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P'
        };
        var fileId = "thumbnail-cache-fixture-" + Guid.NewGuid().ToString("N");
        var revisionId = "revision-" + Guid.NewGuid().ToString("N");
        var cachePath = provider.GetCachePath(fileId, revisionId);
        var stalePath = string.Empty;
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        try
        {
            File.WriteAllBytes(cachePath, malformedWebp);
            Assert(string.Equals(await provider.GetThumbnailPathAsync(fileId, revisionId), cachePath,
                    StringComparison.OrdinalIgnoreCase),
                "A legacy RIFF/WEBP cache entry is found before actual decoding validates it.");
            try
            {
                WebPBitmapDecoder.Decode(malformedWebp, decodePixelWidth: 640);
                throw new Exception("A header-only WebP cache fixture must fail real decoding.");
            }
            catch (InvalidDataException) { }

            Assert(provider.TryCaptureCacheFileStamp(fileId, revisionId, cachePath, out var firstStamp),
                "The failing cache generation can be captured before decode.");
            Assert(provider.InvalidateAfterDecodeFailure(fileId, revisionId, cachePath, firstStamp) &&
                   !File.Exists(cachePath),
                "The first actual decode failure deletes that cached file and permits one refresh.");

            File.WriteAllBytes(cachePath, malformedWebp);
            Assert(provider.TryCaptureCacheFileStamp(fileId, revisionId, cachePath, out var refreshedStamp),
                "The refreshed cache has a distinct generation stamp.");
            Assert(!provider.InvalidateAfterDecodeFailure(fileId, revisionId, cachePath, refreshedStamp) &&
                   !File.Exists(cachePath),
                "A second decode failure evicts the replacement and enters a bounded retry cooldown.");
            Assert(await provider.GetThumbnailPathAsync(fileId, revisionId) is null,
                "The cooldown suppresses an immediate repeated thumbnail download.");

            var staleFileId = "thumbnail-cache-stale-fixture-" + Guid.NewGuid().ToString("N");
            var staleRevisionId = "revision-" + Guid.NewGuid().ToString("N");
            stalePath = provider.GetCachePath(staleFileId, staleRevisionId);
            File.WriteAllBytes(stalePath, malformedWebp);
            Assert(provider.TryCaptureCacheFileStamp(staleFileId, staleRevisionId, stalePath, out var staleStamp),
                "A cache generation can be stamped before a concurrent replacement.");
            File.WriteAllBytes(stalePath, [.. malformedWebp, 0]);
            Assert(!provider.InvalidateAfterDecodeFailure(staleFileId, staleRevisionId, stalePath, staleStamp) &&
                   File.Exists(stalePath),
                "A stale decode failure cannot delete a newer cache file at the same path.");
            File.Delete(stalePath);
        }
        finally
        {
            try { File.Delete(cachePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!string.IsNullOrWhiteSpace(stalePath))
            {
                try { File.Delete(stalePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        Console.WriteLine("PASS: invalid WebP thumbnail cache eviction, one coalesced refresh window, cooldown, and stale-generation protection.");
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}

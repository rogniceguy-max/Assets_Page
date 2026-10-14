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
using MuYingDrive.NativeWpf.Views;

internal static class Program
{
    private static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var bindingErrors = new BindingErrors();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var output = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "muying-feedback-render");
            Directory.CreateDirectory(output);
            var settings = new NasClientSettings(new Uri("https://feedback-test.invalid"), new string('0', 64), @"\\feedback-test.invalid\share");
            var session = new DriveSession(new V2UserSession { Id = 1, UserName = "fixture-only", Department = "fixture-only", GroupType = "film", Spaces = new() { ["film"] = "/film", ["writing"] = "/writing", ["public"] = "/public", ["personal"] = "/personal/fixture" } }, "fixture-token", new SmbConnectionResult(0));
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
            // The employee workspace always carries all three pages; the nullable
            // properties exist for the tour, which has neither transfers nor files.
            typeof(FeedbackViewModel).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace.Feedback, true);
            workspace.Transfers!.Tasks.Add(failed);
            workspace.Files!.SearchText = "fixture-browse-filter";
            var files = workspace.Files;
            var transfers = workspace.Transfers;
            workspace.NavigateCommand.Execute("反馈");
            Assert(ReferenceEquals(workspace.CurrentContent, workspace.Feedback), "Feedback must be a peer workspace.");
            workspace.NavigateCommand.Execute("传输");
            Assert(ReferenceEquals(workspace.CurrentContent, transfers) && transfers.Tasks.Contains(failed), "Navigation must retain the transfer queue instance.");
            workspace.NavigateCommand.Execute("文件");
            Assert(ReferenceEquals(workspace.CurrentContent, files) && files.SearchText == "fixture-browse-filter", "Navigation must retain file browsing state.");

            // --- the names a duplicate gets ---------------------------------------------
            Assert(UniqueName.WithSuffix("照片.jpg", 2) == "照片 (2).jpg", "A duplicate is numbered before its extension.");
            Assert(UniqueName.WithSuffix("照片 (2).jpg", 3) == "照片 (3).jpg", "Numbering a numbered name must not stack suffixes.");
            Assert(UniqueName.WithSuffix("archive.tar.gz", 2) == "archive.tar (2).gz", "Only the last extension separates the suffix.");
            Assert(UniqueName.WithSuffix(".gitignore", 2) == ".gitignore (2)", "A leading dot belongs to the name, not the extension.");
            Assert(UniqueName.WithoutSuffix("照片 (2).jpg") == "照片.jpg", "The existing suffix is stripped for the next candidate.");
            Assert(UniqueName.WithoutSuffix("照片.jpg") == "照片.jpg", "A plain name is already a base name.");

            // --- what a write may be called ----------------------------------------------
            foreach (var bad in new[]
                     {
                         "", "   ", ".", "..", "a\\b", "a/b", "a:b", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b",
                         "CON", "con.txt", "nul", "LPT1.log", "trailing.", "trailing ", "line\nbreak"
                     })
            {
                var rejected = false;
                try { SmbSpaceResolver.ValidateNameSegment(bad); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, $"The name '{bad.Replace("\n", "\\n")}' must be refused.");
            }
            Assert(SmbSpaceResolver.ValidateNameSegment("正常的名字 (2).jpg") == "正常的名字 (2).jpg",
                "An ordinary name passes through unchanged.");

            // --- where a write may land ---------------------------------------------------
            var resolver = new SmbSpaceResolver(settings, session.User);
            var department = resolver.GetSpacePath(DriveSpaceKind.Department);
            Assert(resolver.IsWithinSpace(DriveSpaceKind.Department, department), "The space root is inside itself.");
            Assert(resolver.RequireWritablePath(DriveSpaceKind.Department, Path.Combine(department, "素材", "a.png")).Length > 0,
                "A path inside the space is a valid write target.");
            var walkedOut = false;
            try { resolver.RequireWritablePath(DriveSpaceKind.Department, Path.Combine(department, "..", "elsewhere")); }
            catch (InvalidOperationException) { walkedOut = true; }
            Assert(walkedOut, "A path that walks out of the space must be refused.");
            // The link walk must not reject an ordinary path, or browsing would break the
            // moment it was added to the read side.
            SmbSpaceResolver.EnsureNoReparsePoints(department);

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

            // The tour renders the same FilesView from a different view model, and WPF binds
            // against the runtime type rather than IFileBrowser — so rendering it here is the
            // only thing that catches a member the employee side has and the tour does not.
            // Its constructor is offline; only InitializeAsync talks to the API.
            var visitorApi = new V2ApiClient(new V2ApiClientOptions(new Uri("https://feedback-test.invalid"), new string('0', 64)));
            var visitor = new VisitorViewModel(main, visitorApi);

            foreach (var theme in new[] { "Light", "Dark" })
            {
                application.Resources.MergedDictionaries.Clear();
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MuYingDrive.NativeWpf;component/Themes/{theme}.xaml") });
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MuYingDrive.NativeWpf;component/Themes/SharedStyles.xaml") });
                Render(new FeedbackView { DataContext = model }, 960, 680, 96, Path.Combine(output, $"list-{theme}.png"));
                workspace.Feedback!.Items.Clear(); workspace.Feedback.Items.Add(item);
                workspace.NavigateCommand.Execute("反馈");
                Render(new WorkspaceView { DataContext = workspace }, 1200, 720, 96, Path.Combine(output, $"workspace-{theme}.png"));
                workspace.ToggleSidebarCommand.Execute(null);
                Render(new WorkspaceView { DataContext = workspace }, 900, 600, 96, Path.Combine(output, $"workspace-collapsed-{theme}.png"));
                workspace.ToggleSidebarCommand.Execute(null);
                Render(new TransferView { DataContext = workspace.Transfers }, 960, 480, 96, Path.Combine(output, $"transfer-failure-{theme}.png"));
                Render(new FilesView { DataContext = visitor }, 1100, 680, 96, Path.Combine(output, $"tour-{theme}.png"));

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
            visitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            visitorApi.Dispose();
            Assert(bindingErrors.Messages.Count == 0, string.Join("\n", bindingErrors.Messages));
            workspace.DisposeAsync().AsTask().GetAwaiter().GetResult(); main.Dispose();
            Console.WriteLine("PASS: draft privacy gate, image limits, anonymous serialization, retained workspace/queue instances, Light/Dark list/detail/form/empty/loading/error rendering, 150%/200% rendering; no binding errors.");
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

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}

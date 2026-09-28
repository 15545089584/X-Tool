using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotApp.Mail;
using ScreenshotApp.Translation;

class Program
{
    [STAThread] static void Main()
    {
        var app = new Application();
        var window = new MailWindow();
        var view = (FrameworkElement)window.Content;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object? value) => typeof(MailWindow).GetField(name, flags)!.SetValue(window, value);
        var row = new MailRow("synthetic", 1, 1, "GitHub <noreply@github.com>", "A new sign-in to your account", DateTimeOffset.Now, false, 100);
        Set("_selected", row);
        ((FrameworkElement)window.FindName("Reader")).Visibility = Visibility.Visible;
        ((FrameworkElement)window.FindName("ReaderEmpty")).Visibility = Visibility.Collapsed;
        ((TextBlock)window.FindName("Subject")).Text = row.Subject;
        ((TextBlock)window.FindName("SenderName")).Text = "GitHub";
        ((TextBlock)window.FindName("Sender")).Text = "noreply@github.com";
        ((TextBlock)window.FindName("Position")).Text = "第 200 封 / 共 200 封";
        var body = (RichTextBox)window.FindName("Body");
        var original = MailDocument.Create("<p>We noticed a new sign-in to your account.</p><p>If this was you, you can safely ignore this email.</p><p><a href='https://example.invalid'>Review activity</a></p>", true);
        body.Document = original;
        Set("_originalDocument", original); Set("_translatedText", "我们注意到您的账户有一次新的登录。");
        Set("_translatedSubject", "您的账户有一次新的登录");
        var button = (Button)window.FindName("TranslateButton"); button.IsEnabled = true;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text.Contains("新的登录")) throw new Exception("译文未显示");
        if (((TextBlock)window.FindName("Subject")).Text != "您的账户有一次新的登录" || ((TextBlock)window.FindName("SenderName")).Text != "GitHub") throw new Exception("标题或发件人翻译边界错误");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!ReferenceEquals(body.Document, original)) throw new Exception("原文格式未恢复");
        if (((TextBlock)window.FindName("Subject")).Text != row.Subject) throw new Exception("标题原文未恢复");
        Console.WriteLine("PASS 翻译切换及原文文档恢复");
        var focusButton = new Button { Style = button.Style, Content = "A字", Width = 36, Height = 34 };
        var focusHost = new Window { Content = focusButton, Width = 100, Height = 100, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false };
        focusHost.Show(); focusHost.UpdateLayout();
        var focusSurface = (Border)focusButton.Template.FindName("Surface", focusButton);
        var beforeFocus = focusSurface.BorderThickness;
        focusButton.Focus(); focusHost.UpdateLayout();
        if (focusButton.FocusVisualStyle is not null || beforeFocus != focusSurface.BorderThickness || !focusButton.Focusable) throw new Exception("焦点描边未稳定或键盘焦点被禁用");
        focusHost.Close();
        Console.WriteLine("PASS 单层焦点提示、稳定边框尺寸与键盘焦点保留");
        foreach (var size in new[] { new Size(1000,620), new Size(1440,900) })
        {
            view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
            var pos = (TextBlock)window.FindName("Position");
            var left = button.TransformToAncestor(view).Transform(new Point(button.ActualWidth,0)).X;
            var right = pos.TransformToAncestor(view).Transform(new Point()).X;
            if (left > right || button.ActualWidth < 32 || button.ActualHeight < 30) throw new Exception("工具栏发生重叠");
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create($"artifacts/mail-encoding-translation-check/preview-{size.Width}.png"); encoder.Save(output);
        }
        typeof(MailWindow).GetMethod("ClearReader", flags)!.Invoke(window, null);
        if (button.IsEnabled || typeof(MailWindow).GetField("_originalDocument", flags)!.GetValue(window) is not null) throw new Exception("切信未清理译文");
        Console.WriteLine("PASS 最小窗口、正常窗口布局及切信清理");
        // 使用发布包内模型与合成正文试译，不读取用户账户或邮件。
        var assembly = typeof(MailWindow).Assembly;
        var models = Path.GetFullPath("ScreenshotApp/bin/mail-translation-responsive/Release/models/translation/default");
        var pathsType = assembly.GetType("ScreenshotApp.Translation.TranslationModelPaths")!;
        var paths = Activator.CreateInstance(pathsType, new[] { "encoder_model.onnx", "decoder_model.onnx", "source.spm", "target.spm", "vocab.json", "manifest.json" }.Select(f => (object)Path.Combine(models,f)).ToArray())!;
        var engineType = assembly.GetType("ScreenshotApp.Translation.OnnxTranslationEngine")!;
        using var engineDisposable = (IDisposable)Activator.CreateInstance(engineType, flags, null, new[] { paths, "英译中", "synthetic-mail-check" }, null)!;
        var engine = (ITranslationEngine)engineDisposable;
        if (!engine.IsReady) throw new Exception(engine.UnavailableReason);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var translated = engine.TranslateAsync(new TranslationRequest("We noticed a new sign-in to your account. If this was you, you can safely ignore this email."), cancel.Token).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(translated.TranslatedText)) throw new Exception("真实模型无输出");
        Console.WriteLine($"MODEL {translated.Elapsed.TotalSeconds:F1}s: {translated.TranslatedText}");
        Console.WriteLine("PASS 真实本地模型合成邮件试译");
        var padding = string.Concat(Enumerable.Repeat("\u034f\u200c\u00a0", 80));
        var baseline = engine.TranslateAsync(new TranslationRequest(padding), cancel.Token).GetAwaiter().GetResult();
        Console.WriteLine($"BASELINE padding: unk={baseline.TranslatedText.Contains("<unk>")}, length={baseline.TranslatedText.Length}");
        var newsletter = "Three teams, three different workflows, one tool.\n" + padding + "\n" + string.Join("\n\n", new[] {
            "Explore how designers use motion to bring their ideas to life. Our latest update makes collaboration easier for your team.",
            "The first team creates product demonstrations for customers. They start with a simple storyboard and add transitions later.",
            "The second team builds interactive presentations. Their designers share feedback before publishing a new version.",
            "The third team makes short videos for social media. Reusable templates help them keep a consistent visual style.",
            "You can adjust the speed of each animation. Preview the result before exporting your project.",
            "Keep your original files in a separate folder. Review the final video with your team before sharing it.",
            "Our support team is ready to answer your questions. Contact support@example.invalid for more information.",
            "We are working on new features for next month. Your feedback helps us decide what to build next."
        }) + "\nFinal paragraph: Thank you for reading our newsletter.\nhttps://example.invalid/end";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var gaps = new List<double>();
        var lastTick = timer.Elapsed.TotalMilliseconds;
        var frame = new System.Windows.Threading.DispatcherFrame();
        var work = Task.Run(() => MailTranslation.TranslateAsync("✨ Inside the motion workflows of Gamma and makepulse", newsletter, engine, cancel.Token));
        var heartbeat = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        heartbeat.Tick += (_, _) => { var now = timer.Elapsed.TotalMilliseconds; gaps.Add(now-lastTick); lastTick=now; if(work.IsCompleted) frame.Continue=false; };
        heartbeat.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); heartbeat.Stop();
        var result = work.GetAwaiter().GetResult();
        Console.WriteLine($"RESPONSIVENESS cpu={(process.TotalProcessorTime-cpuBefore).TotalSeconds:F2}s wall={timer.Elapsed.TotalSeconds:F2}s max-ui-gap={gaps.Max():F1}ms ticks={gaps.Count}");
        if (result.Body.Contains("<unk>") || !result.Body.Contains("https://example.invalid/end") || result.Subject == "Inside the motion workflows of Gamma and makepulse") throw new Exception("长邮件回归失败");
        Console.WriteLine($"LONG MAIL {timer.Elapsed.TotalSeconds:F1}s retained={result.RetainedSegments}\nTITLE: {result.Subject}\nBODY: {result.Body}");
        Console.WriteLine("PASS 带不可见预览填充的长邮件、标题翻译和末尾内容");
        var branded = MailTranslation.TranslateAsync("[Docker] You + Docker = Ready for Action", "Hi, welcome to Docker q47595101800!\nDownload Docker Desktop and sign in to Docker Hub.", engine, cancel.Token).GetAwaiter().GetResult();
        Console.WriteLine($"BRAND {branded.Subject}\n{branded.Body}");
        if (!branded.Subject.StartsWith("[Docker]") || !branded.Body.Contains("Docker Desktop") || !branded.Body.Contains("q47595101800")) throw new Exception("品牌或账号被改写");
        var running = engine.TranslateAsync(new TranslationRequest(string.Join(" ", Enumerable.Repeat("Review the final video with your team before sharing it.", 8))), cancel.Token);
        using (var queuedCancel = new CancellationTokenSource(20))
        {
            try { engine.TranslateAsync(new TranslationRequest("Queued translation request."), queuedCancel.Token).GetAwaiter().GetResult(); throw new Exception("排队任务未取消"); }
            catch (OperationCanceledException) { Console.WriteLine("PASS 排队翻译可及时取消"); }
        }
        running.GetAwaiter().GetResult();
        engine.TranslateAsync(new TranslationRequest("Translation continues after cancellation."), cancel.Token).GetAwaiter().GetResult();
        Console.WriteLine("PASS 取消不泄露推理限流锁");
        var zhModels = Path.GetFullPath("ScreenshotApp/bin/mail-translation-responsive/Release/models/translation/zh-en");
        var zhPaths = Activator.CreateInstance(pathsType, new[] { "encoder_model.onnx", "decoder_model.onnx", "source.spm", "target.spm", "vocab.json", "manifest.json" }.Select(f => (object)Path.Combine(zhModels,f)).ToArray())!;
        using var zhDisposable = (IDisposable)Activator.CreateInstance(engineType, flags, null, new[] { zhPaths, "中译英", "synthetic-zh-check" }, null)!;
        var zhResult = ((ITranslationEngine)zhDisposable).TranslateAsync(new TranslationRequest("你好，欢迎使用截图翻译。") { SourceLanguage = "zh-Hans", TargetLanguage = "en" }, cancel.Token).GetAwaiter().GetResult();
        if (!zhResult.TranslatedText.Any(char.IsAsciiLetter)) throw new Exception("共享线程配置导致中译英失败");
        Console.WriteLine($"PASS 中译英共享配置: {zhResult.TranslatedText}");
        // 不关闭未显示窗口，避免触发用户布局保存事件。
    }
}

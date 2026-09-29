using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenshotApp.Mail;

internal static class Program
{
    [STAThread] private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // 只构造设置页验证控件，不改动真实用户偏好文件。
        var settingsType = typeof(ScreenshotApp.Settings.SettingsWindow);
        var constructor = settingsType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        var preferences = new ScreenshotApp.Settings.AppPreferences { PetInjectionDelaySeconds = 12 };
        var arguments = constructor.GetParameters().Select(p =>
        {
            if (p.ParameterType == typeof(ScreenshotApp.Settings.AppPreferences)) return (object)preferences;
            if (typeof(Delegate).IsAssignableFrom(p.ParameterType))
            {
                var invoke = p.ParameterType.GetMethod("Invoke")!;
                var parameters = invoke.GetParameters().Select(x => System.Linq.Expressions.Expression.Parameter(x.ParameterType)).ToArray();
                return System.Linq.Expressions.Expression.Lambda(p.ParameterType, System.Linq.Expressions.Expression.Default(invoke.ReturnType), parameters).Compile();
            }
            return p.ParameterType == typeof(string) ? "合成测试" : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }).ToArray();
        var settings = (Window)constructor.Invoke(arguments);
        var delay = (TextBox)settings.FindName("InjectionDelayInput");
        var slider = (Slider)settings.FindName("InjectionDelaySlider");
        if (delay.Text != "12" || slider.Value != 12 || slider.Minimum != 1 || slider.Maximum != 30) throw new Exception("倒计时设置未恢复保存值");
        if (new ScreenshotApp.Settings.AppPreferences().PetInjectionDelaySeconds != 5) throw new Exception("默认倒计时改变");
        settingsType.GetMethod("SelectCategoryForValidation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(settings, ["DesktopPet"]);
        var settingsView = (FrameworkElement)settings.Content;
        settingsView.Measure(new Size(1040,760)); settingsView.Arrange(new Rect(0,0,1040,760)); settingsView.UpdateLayout();
        if (delay.Template.FindName("Surface",delay) is not Border { CornerRadius.TopLeft: 8 } || delay.ActualHeight != 34) throw new Exception("秒数输入框未使用圆角模板");
        var settingsImage = new RenderTargetBitmap(1040,760,96,96,PixelFormats.Pbgra32); settingsImage.Render(settingsView);
        var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsImage));
        using (var screenshot = File.Create("artifacts/mail-cache-check/countdown-settings.png")) settingsEncoder.Save(screenshot);
        settings.Close();
        Console.WriteLine("PASS 倒计时默认值、数字框/滑块初始化、范围与设置页布局；未写入用户设置");
        var account = new MailAccountView("synthetic", "QQ", "synthetic@example.invalid", "离线", true, true, false, 23, 8, false, "测试账户");
        var window = new MailAccountDialog(account) { Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var texts = Children(window).OfType<TextBlock>().Select(t => t.Text).ToArray();
                if (!texts.Any(t => t.Contains("所有邮箱占用"))) throw new Exception("存储统计未异步加载");
                var location = Children(window).OfType<TextBox>().Single(t => t.IsReadOnly);
                if (!Path.IsPathFullyQualified(location.Text)) throw new Exception("存储路径未显示");
                var openFolder = Children(window).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "打开邮箱缓存文件夹");
                if (!openFolder.IsEnabled) throw new Exception("打开文件夹按钮未启用");
                var scroll = Children(window).OfType<ScrollViewer>().First();
                scroll.ScrollToBottom(); window.UpdateLayout();
                var view = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create("artifacts/mail-cache-check/settings.png"); encoder.Save(output);
                if (location.ActualWidth <= 0 || location.ActualWidth > 500) throw new Exception("路径宽度异常");
                Console.WriteLine("PASS 账户设置异步统计、只读存储路径、滚动布局与关闭清理");
                window.Close(); app.Shutdown();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        timer.Start(); app.Run();
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var item in Children(child)) yield return item;
        }
    }
}

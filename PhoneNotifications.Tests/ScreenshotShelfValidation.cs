using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class ScreenshotShelfValidation
{
    private const BindingFlags InternalInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        var root = Directory.GetCurrentDirectory();
        var assembly = Assembly.LoadFrom(Path.Combine(root, "ScreenshotApp/bin/notification-screenshot-shelf/Release/XTool.dll"));
        var app = new Application();
        // 使用真实全局资源复现隐式 TextBlock 样式；不启动正式 App 或真实通知服务。
        var document = XDocument.Parse(File.ReadAllText(Path.Combine(root, "ScreenshotApp/App.xaml"), Encoding.UTF8));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            document.Root!.Element(ns + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var output = Path.Combine(root, "artifacts/notification-screenshot-shelf-validation");
        Directory.CreateDirectory(output);

        var windowType = assembly.GetType("ScreenshotApp.Collaboration.PhoneNotificationsWindow")!;
        var window = (Window)Activator.CreateInstance(windowType, InternalInstance, null, [true], null)!;
        var button = (Button)window.FindName("PairButton");
        ((CheckBox)window.FindName("AlertsCheckBox")).IsChecked = true;
        ((CheckBox)window.FindName("PreviewCheckBox")).IsChecked = true;
        var surface = Detach(window);
        Render(surface, 1000, 780, 1.5, Path.Combine(output, "notifications-150.png"));
        var text = Descendants(button).OfType<TextBlock>().Single(t => t.Text == "连接设置");
        Require(((SolidColorBrush)text.Foreground).Color == Colors.White, "全局资源下蓝色按钮实际文字为白色");
        button.IsEnabled = false;
        Render(surface, 820, 660, 1.5, Path.Combine(output, "notifications-minimum-150.png"));
        Require(((SolidColorBrush)text.Foreground).Color == Colors.White, "禁用状态保留白字");
        button.IsEnabled = true;
        ((FrameworkElement)window.FindName("PairPanel")).Visibility = Visibility.Visible;
        Render(surface, 820, 660, 1, Path.Combine(output, "pair-panel.png"));
        var done = Descendants(surface).OfType<TextBlock>().Single(t => t.Text == "完成");
        Require(((SolidColorBrush)done.Foreground).Color == Colors.White, "配对完成按钮实际文字为白色");

        var preferencesType = assembly.GetType("ScreenshotApp.Settings.AppPreferences")!;
        var preferences = Activator.CreateInstance(preferencesType)!;
        var preference = preferencesType.GetProperty("ScreenshotAutoAddToPetShelf")!;
        Require(!(bool)preference.GetValue(preferences)!, "新设置默认关闭");
        preference.SetValue(preferences, true);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(preferences, preferencesType), preferencesType)!;
        Require((bool)preference.GetValue(restored)!, "开启选项可序列化并恢复，不写用户配置");
        preference.SetValue(preferences, false);

        var settingsType = assembly.GetType("ScreenshotApp.Settings.SettingsWindow")!;
        var constructor = settingsType.GetConstructors(InternalInstance).Single();
        var arguments = constructor.GetParameters().Select(parameter =>
        {
            if (parameter.ParameterType == preferencesType) return preferences;
            if (parameter.ParameterType == typeof(string)) return (object)"界面验证";
            if (typeof(Delegate).IsAssignableFrom(parameter.ParameterType))
            {
                var invoke = parameter.ParameterType.GetMethod("Invoke")!;
                var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
                return Expression.Lambda(parameter.ParameterType, Expression.Default(invoke.ReturnType), parameters).Compile();
            }
            return Activator.CreateInstance(parameter.ParameterType);
        }).ToArray();
        var settings = (Window)constructor.Invoke(arguments);
        settingsType.GetMethod("SelectCategoryForValidation", InternalInstance)!.Invoke(settings, ["Screenshot"]);
        var toggle = (CheckBox)settings.FindName("ScreenshotAutoAddToPetShelfCheckBox");
        Require(toggle.IsChecked == false, "设置页面正确加载关闭状态");
        var settingsSurface = Detach(settings);
        Render(settingsSurface, 1120, 700, 1.5, Path.Combine(output, "screenshot-settings-150.png"));
        toggle.IsChecked = true;
        Render(settingsSurface, 900, 590, 1.5, Path.Combine(output, "screenshot-settings-minimum-150.png"));
        Require(toggle.IsChecked == true && toggle.IsEnabled, "开关可交互且初始化不保存用户配置");

        // 暂存行为使用独立临时根，不能扫描或清理用户真实会话。
        var temporary = Path.Combine(Path.GetTempPath(), "XTool-ScreenshotShelf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var source = Path.Combine(temporary, "合成截图.png");
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(source)) encoder.Save(stream);
        var shelfType = assembly.GetType("ScreenshotApp.DesktopPet.PetSessionShelfService")!;
        var shelf = Activator.CreateInstance(shelfType, InternalInstance, null, [false, Path.Combine(temporary, "shelf")], null)!;
        try
        {
            object Add(string path)
            {
                var task = (Task)shelfType.GetMethod("AddFilesAsync", InternalInstance)!.Invoke(shelf, [new[] { path }, CancellationToken.None])!;
                task.GetAwaiter().GetResult();
                return task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
            int Count(object result, string field) => (int)result.GetType().GetProperty(field)!.GetValue(result)!;
            Require(Count(Add(source), "AddedCount") == 1, "已保存截图可加入暂存区");
            Require(Count(Add(source), "DuplicateCount") == 1, "同一截图不重复暂存");
            Require(Count(Add(Path.Combine(temporary, "不存在.png")), "FailedCount") == 1, "源文件缺失返回失败而非成功");
            var items = (System.Collections.IEnumerable)shelfType.GetProperty("Items", InternalInstance)!.GetValue(shelf)!;
            var item = items.Cast<object>().Single();
            var stored = (string)item.GetType().GetProperty("StoredPath")!.GetValue(item)!;
            Require(File.ReadAllBytes(stored).SequenceEqual(File.ReadAllBytes(source)), "暂存副本与原截图一致");
            Require(item.GetType().GetProperty("Thumbnail")!.GetValue(item) is BitmapSource { IsFrozen: true }, "暂存缩略图冻结，可安全跨线程展示");
        }
        finally
        {
            ((IDisposable)shelf).Dispose();
            Require(File.Exists(source), "释放暂存会话保留截图原文件");
            // 验证产物保留，便于人工复核；不删除未知目录或旧构建。
        }
        Console.WriteLine("截图暂存与界面专项验证完成；未启动正式实例、未修改真实偏好或配对。");
    }

    private static Grid Detach(Window window)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var surface = new Grid { Resources = window.Resources, Background = window.Background };
        TextElementDefaults(surface, window);
        surface.Children.Add(content);
        return surface;
    }

    private static void TextElementDefaults(FrameworkElement surface, Window window)
    {
        System.Windows.Documents.TextElement.SetFontFamily(surface, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(surface, window.FontSize);
        System.Windows.Documents.TextElement.SetForeground(surface, window.Foreground);
    }

    private static void Render(FrameworkElement surface, int width, int height, double scale, string path)
    {
        surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("通过：" + message);
    }
}

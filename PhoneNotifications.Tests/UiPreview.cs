using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class UiPreview
{
    internal static void Run()
    {
        var assembly = Assembly.LoadFrom(Path.Combine(Directory.GetCurrentDirectory(), "ScreenshotApp/bin/phone-notifications-reconnect/Release/XTool.dll"));
        var app = new Application();
        var type = assembly.GetType("ScreenshotApp.Collaboration.PhoneNotificationsWindow")!;
        // 专用预览构造器不启动网络、保存配对或接触真实通知。
        var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [true], null)!;
        ((TextBlock)window.FindName("StatusText")).Text = "通知同步已启用 · 以下为界面核验样本";
        ((ListBox)window.FindName("NotificationList")).ItemsSource = new[] {
            new { App = "测试应用", Title = "跨设备通知测试", Text = "这是一条合成通知，用于验证标题、正文和列表布局。" },
            new { App = "日历（仅标题）", Title = "会议将在十分钟后开始", Text = "" },
            new { App = "测试应用", Title = "较长标题在窄窗口中的显示核验", Text = new string('测', 250) }
        };
        ((FrameworkElement)window.FindName("EmptyPanel")).Visibility = Visibility.Collapsed;
        ((TextBlock)window.FindName("CountText")).Text = "3 条";
        ((CheckBox)window.FindName("AlertsCheckBox")).IsChecked = true;
        ((CheckBox)window.FindName("PreviewCheckBox")).IsChecked = true;
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var surface = new Grid { Background = new SolidColorBrush(Color.FromRgb(240,245,251)) };
        surface.Resources = window.Resources;
        System.Windows.Documents.TextElement.SetFontFamily(surface, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(surface, window.FontSize);
        System.Windows.Documents.TextElement.SetForeground(surface, window.Foreground);
        surface.Children.Add(content);
        surface.Measure(new Size(964, 700)); surface.Arrange(new Rect(0, 0, 964, 700)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(964, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/phone-notifications-validation"); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, "desktop-reconnect-preview.png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output);
        var petType = assembly.GetType("ScreenshotApp.DesktopPet.PetTransferBubbleWindow")!;
        var pet = (Window)Activator.CreateInstance(petType, nonPublic: true)!;
        // 合成圆形头像同时覆盖图片分支，避免只检查无头像占位状态。
        var avatarVisual = new DrawingVisual();
        using (var drawing = avatarVisual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 64, 64));
            drawing.DrawEllipse(Brushes.White, null, new Point(32, 26), 12, 12);
            drawing.DrawRoundedRectangle(Brushes.White, null, new Rect(12, 42, 40, 22), 12, 12);
        }
        var avatarBitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); avatarBitmap.Render(avatarVisual);
        using var avatarStream = new MemoryStream();
        var avatarEncoder = new PngBitmapEncoder(); avatarEncoder.Frames.Add(BitmapFrame.Create(avatarBitmap)); avatarEncoder.Save(avatarStream);
        var avatarData = Convert.ToBase64String(avatarStream.ToArray());
        petType.GetMethod("ShowPhoneMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pet,
            ["QQ · 手机通知（测试）", "这是一条合成消息，用来核验放大后的字体、多行消息和点击提示。", (Action)(() => { }), avatarData, true]);
        var petContent = (FrameworkElement)pet.Content;
        pet.Content = null;
        var petSurface = new Grid { Resources = pet.Resources, Background = new SolidColorBrush(Color.FromRgb(240,245,251)) };
        petSurface.Children.Add(petContent);
        petSurface.Measure(new Size(440, 230)); petSurface.Arrange(new Rect(0,0,440,230)); petSurface.UpdateLayout();
        var avatarImage = (Image)pet.FindName("AvatarImage");
        var titleText = (TextBlock)pet.FindName("TitleText");
        var avatarBounds = avatarImage.TransformToAncestor(petSurface).TransformBounds(new Rect(avatarImage.RenderSize));
        var titleBounds = titleText.TransformToAncestor(petSurface).TransformBounds(new Rect(titleText.RenderSize));
        if (avatarImage.Source is null || avatarBounds.Width < 44 || titleBounds.Left < avatarBounds.Right + 8)
            throw new InvalidOperationException("头像未完整显示或与标题间距不足");
        var petBitmap = new RenderTargetBitmap(440,230,96,96,PixelFormats.Pbgra32); petBitmap.Render(petSurface);
        using var petOutput = File.Create(Path.Combine(directory, "pet-notification-avatar-layout.png"));
        var petEncoder = new PngBitmapEncoder(); petEncoder.Frames.Add(BitmapFrame.Create(petBitmap)); petEncoder.Save(petOutput);
        Console.WriteLine("通知窗口和宠物气泡离屏核验完成，未启动接收服务。");
    }
}

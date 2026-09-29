using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotApp.DesktopPet;
using ScreenshotApp.Collaboration;

internal static class Program
{
    [STAThread] private static void Main()
    {
        var app = new Application();
        var globalText = new Style(typeof(TextBlock));
        globalText.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Black));
        app.Resources[typeof(TextBlock)] = globalText;
        var assembly = typeof(DesktopPetWindow).Assembly;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = assembly.GetType("ScreenshotApp.DesktopPet.PetInjectionWindow")!;
        var editor = (Window)Activator.CreateInstance(type, flags, null, [true], null)!;
        var input = (TextBox)type.GetProperty("Editor", flags)!.GetValue(editor)!;
        input.Text = "中文内容与 English text\n第二行 +^%(){}~\n" + new string('长', 1200);
        Render((FrameworkElement)editor.Content, new Size(430, 340), "editor");
        var scrollbars = Children(input).OfType<System.Windows.Controls.Primitives.ScrollBar>().ToArray();
        if (!scrollbars.Any(s => s.Orientation == Orientation.Vertical && s.Width == 7)) throw new Exception("未复用邮箱细滚动条");
        if (((Border)editor.Content).Background is not LinearGradientBrush gradient || gradient.GradientStops.Count != 4) throw new Exception("邮箱渐变未应用");
        var buttons = Children((FrameworkElement)editor.Content).OfType<Button>().ToArray();
        var injectButton = buttons.Single(b => b.Content is TextBlock { Text: "注入" });
        if (buttons.Length != 2 || buttons.Any(b => b.ActualHeight != 34) || !injectButton.IsEnabled) throw new Exception("按钮状态或尺寸错误");
        if (((SolidColorBrush)((TextBlock)injectButton.Content).Foreground).Color != Colors.White) throw new Exception("主按钮文字不是白色");
        injectButton.IsEnabled = false;
        if (((SolidColorBrush)((TextBlock)injectButton.Content).Foreground).Color != Colors.White) throw new Exception("禁用状态改变了白色文字");
        injectButton.IsEnabled = true;
        input.Clear();
        if (injectButton.IsEnabled) throw new Exception("空文本应禁用注入");
        var progress = (Window)Activator.CreateInstance(type, flags, null, [false], null)!;
        ((TextBlock)type.GetProperty("Status",flags)!.GetValue(progress)!).Text = "5 秒后注入 · 请点击目标输入框\nEsc 取消";
        Render((FrameworkElement)progress.Content, new Size(280,94), "countdown");
        if (progress.ShowActivated) throw new Exception("进度气泡不应抢焦点");
        var wheelType = assembly.GetType("ScreenshotApp.DesktopPet.PetCommandWheelWindow")!;
        var wheel = (Window)Activator.CreateInstance(wheelType, flags, null, [new Rect(1000,650,288,288),new Rect(0,0,1440,1000),0,0],null)!;
        var wheelText = Children((FrameworkElement)wheel.Content).OfType<TextBlock>().Select(t=>t.Text).ToArray();
        if (!wheelText.Contains("注入") || wheelText.Contains("手机日程")) throw new Exception("轮盘入口未替换");
        Render((FrameworkElement)wheel.Content,new Size(wheel.Width,wheel.Height),"wheel");
        var collaboration = new CollaborationView();
        foreach (var width in new[] { 800, 1100 })
        {
            Render(collaboration,new Size(width,800),"collaboration-"+width);
            var calendar = Children(collaboration).OfType<Button>().Single(b=>Equals(b.Content,"手机日程"));
            if (calendar.ActualWidth < 70 || calendar.TransformToAncestor(collaboration).Transform(new Point(calendar.ActualWidth,0)).X > width) throw new Exception("手机日程入口溢出");
        }
        var transfer = (Window)Activator.CreateInstance(typeof(PetTransferBubbleWindow),flags,null,[],null)!;
        var transferCard = (Border)transfer.FindName("BubbleCard");
        if (transferCard.Background is not LinearGradientBrush) throw new Exception("通知气泡主题未应用");
        Render((FrameworkElement)transfer.Content,new Size(320,160),"transfer");
        var shelfType = assembly.GetType("ScreenshotApp.DesktopPet.PetSessionShelfService")!;
        using var shelfService = (IDisposable)Activator.CreateInstance(shelfType,flags,null,[false,Path.Combine(Path.GetTempPath(),"pet-theme-"+Guid.NewGuid().ToString("N"))],null)!;
        var shelf = (Window)Activator.CreateInstance(typeof(PetShelfPanelWindow),flags,null,[shelfService,(Func<bool>)(()=>false)],null)!;
        Render((FrameworkElement)shelf.Content,new Size(420,490),"shelf");
        if (((Border)shelf.FindName("PanelRoot")).Background is not LinearGradientBrush shelfGradient || shelfGradient.GradientStops.Count != 4) throw new Exception("暂存区主题未应用");
        var alarmType = assembly.GetType("ScreenshotApp.DesktopPet.PetAlarmService")!;
        using var alarmService = (IDisposable)Activator.CreateInstance(alarmType,flags,null,[],null)!;
        var alarm = (Window)Activator.CreateInstance(typeof(PetAlarmPanelWindow),flags,null,[alarmService,(Func<bool>)(()=>false)],null)!;
        Render((FrameworkElement)alarm.Content,new Size(alarm.Width,alarm.Height),"alarm");
        if (((Border)alarm.FindName("PanelRoot")).Background is not LinearGradientBrush alarmGradient || alarmGradient.GradientStops.Count != 4) throw new Exception("闹钟主题未应用");
        editor.Close(); progress.Close(); wheel.Close(); transfer.Close(); shelf.Close(); alarm.Close();
        Console.WriteLine("PASS 编辑气泡、长文本、按钮状态、非激活进度、轮盘入口及协作中心双尺寸布局");
        app.Shutdown();
    }
    private static void Render(FrameworkElement view, Size size, string name)
    {
        view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32); image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create($"artifacts/pet-injection-check/{name}.png"); encoder.Save(output);
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i); yield return child;
            foreach(var item in Children(child)) yield return item;
        }
    }
}

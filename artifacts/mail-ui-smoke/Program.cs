using System.Windows;
using System.Windows.Controls;
using ScreenshotApp.Mail;
class Smoke
{
 [STAThread] static void Main()
 {
  var app = new Application();
  var main = new MailWindow();
  var view = (FrameworkElement)main.Content; view.Measure(new Size(1000,620)); view.Arrange(new Rect(0,0,1000,620)); view.UpdateLayout();
  if (((FrameworkElement)main.FindName("ReaderEmpty")).ActualWidth < 220) throw new Exception("阅读区过窄");
  if (MailIdentity.Logo("a@evilgithub.com") is not null || MailIdentity.Logo("a@github.com.evil.test") is not null) throw new Exception("域名匹配不安全");
  foreach (var address in new[] {"a@github.com","a@notify.docker.com","a@jetbrains.com","a@qq.com","a@deepseek.com","a@accounts.google.com"})
  {
   var bitmap = new System.Windows.Media.Imaging.BitmapImage(new Uri(MailIdentity.Logo(address)!));
   if (bitmap.PixelWidth < 1) throw new Exception("图标无法解码");
  }
  var doc = MailDocument.Create("<h1>标题</h1><script>隐藏脚本</script><p>正文 <b>加粗</b></p><pre>code</pre><img src='https://invalid.test/track'>", true);
  var content = new System.Windows.Documents.TextRange(doc.ContentStart,doc.ContentEnd).Text;
  if (!content.Contains("正文") || content.Contains("隐藏脚本") || content.Contains("invalid.test")) throw new Exception("正文白名单失败");
  Console.WriteLine("PASS 发件人域名边界、五类图标解码和正文白名单");
  var add = new MailAccountDialog();
  add.Measure(new Size(900,650)); add.Arrange(new Rect(0,0,900,650)); add.UpdateLayout();
  var addView = (FrameworkElement)add.Content; addView.Measure(new Size(900,650)); addView.Arrange(new Rect(0,0,900,650)); addView.UpdateLayout();
  IEnumerable<DependencyObject> Nodes(DependencyObject node) {
   yield return node;
   for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);i++)
    foreach(var child in Nodes(System.Windows.Media.VisualTreeHelper.GetChild(node,i))) yield return child;
  }
  var enterpriseButton = Nodes(addView).OfType<Button>().Single(b => b.Content is StackPanel sp && sp.Children.OfType<TextBlock>().Any(t => t.Text == "腾讯企业邮箱"));
  enterpriseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  add.UpdateLayout();
  if (!Nodes(addView).OfType<TextBlock>().Any(t => t.Text.Contains("imap.exmail.qq.com"))) throw new Exception("企业邮配置未切换");
  var enterpriseIcon = new System.Windows.Media.Imaging.BitmapImage(new Uri(MailIdentity.ProviderLogo("TencentExmail")!));
  if(enterpriseIcon.PixelWidth < 1) throw new Exception("企业邮官方图标无法解码");
  var addImage = new System.Windows.Media.Imaging.RenderTargetBitmap(900,650,96,96,System.Windows.Media.PixelFormats.Pbgra32);
  addImage.Render(addView);
  var addEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); addEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(addImage));
  using(var output = System.IO.File.Create("artifacts/mail-ui-smoke/enterprise-add.png")) addEncoder.Save(output);
  Console.WriteLine("PASS 腾讯企业邮选择、服务器显示与官方图标加载");
  var prefs = new MailAccountDialog(new MailAccountView("test","QQ","test@example.invalid","已连接",true,false,true,23,8));
  prefs.Measure(new Size(560,650)); prefs.Arrange(new Rect(0,0,560,650)); prefs.UpdateLayout();
  Console.WriteLine("PASS 主窗口最小尺寸、添加邮箱与账户设置的 WPF 资源加载及布局");
  void CheckCode(string input, int expected)
  { if (MailCodeHighlighter.Find(input).Count != expected) throw new Exception("验证码识别不符合预期"); }
  CheckCode("验证码：123456，有效期5分钟", 1);
  CheckCode("Your verification code is:\n\nZ4AJ63", 1);
  CheckCode("订单号 123456，日期 2026-09-27", 0);
  CheckCode("verification code: https://example.test/123456", 0);
  if (MailDocument.SafeLink("javascript:alert(1)") is not null || MailDocument.SafeLink("file:///C:/test") is not null || MailDocument.SafeLink("https://user:pass@example.com") is not null) throw new Exception("不安全链接未拦截");
  var linksDoc = MailDocument.Create("<p><a href='https://example.com/path'>查看活动</a></p><p>https://example.org/test</p><a href='javascript:alert(1)'>无效链接</a>", true);
  var links = linksDoc.Blocks.OfType<System.Windows.Documents.Paragraph>().SelectMany(p => p.Inlines.OfType<System.Windows.Documents.Hyperlink>()).ToArray();
  if (links.Length != 2 || links.Any(l => l.ToolTip is null)) throw new Exception("HTML/文本链接解析失败");
  var codes = MailDocument.Create("<p>Your verification code is:</p><p><b>Z4</b>AJ63</p><p>验证码：123456；订单 654321</p>", true);
  var runs = codes.Blocks.OfType<System.Windows.Documents.Paragraph>().SelectMany(p => p.Inlines.OfType<System.Windows.Documents.Run>()).ToArray();
  var highlighted = string.Concat(runs.Where(r => r.ToolTip is not null).Select(r => r.Text));
  if (highlighted != "Z4AJ63123456") throw new Exception("跨 HTML 文本节点高亮失败：" + highlighted);
  var fullText = new System.Windows.Documents.TextRange(codes.ContentStart, codes.ContentEnd).Text;
  if (!fullText.Contains("订单 654321")) throw new Exception("高亮修改了正文");
  var accounts = (ListBox)main.FindName("Accounts");
  accounts.ItemsSource = new[] {
   new MailAccountView("a","QQ","example@qq.com","已连接 · 实时监听",true,false,false,23,8),
   new MailAccountView("b","Gmail","example@gmail.com","认证被拒绝",true,false,false,23,8),
   new MailAccountView("c","Gmail","another@gmail.com","正在连接",true,false,false,23,8) };
  accounts.SelectedIndex = 0;
  ((System.Windows.Controls.ColumnDefinition)main.FindName("AccountColumn")).Width = new GridLength(280);
  ((System.Windows.Controls.ColumnDefinition)main.FindName("ListColumn")).Width = new GridLength(0.36,GridUnitType.Star);
  ((System.Windows.Controls.ColumnDefinition)main.FindName("ReaderColumn")).Width = new GridLength(0.64,GridUnitType.Star);
  ((TextBlock)main.FindName("AccountCount")).Text = "3";
  ((TextBlock)main.FindName("InboxCount")).Text = "200";
  ((TextBlock)main.FindName("ListCount")).Text = "200";
  ((FrameworkElement)main.FindName("ListEmpty")).Visibility = Visibility.Collapsed;
  typeof(MailWindow).GetField("_updating",System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(main,true);
  var messages = (ListBox)main.FindName("Messages");
  messages.ItemsSource = Enumerable.Range(1,4).Select(i => new MailRow("a",1,(uint)i,"Google <no-reply@accounts.google.com>","example@gmail.com 的安全提醒",DateTimeOffset.Now.AddMinutes(-i),false,100){AccountLabel="example@qq.com"}).ToArray();
  messages.SelectedIndex = 0;
  ((FrameworkElement)main.FindName("ReaderEmpty")).Visibility = Visibility.Collapsed;
  ((FrameworkElement)main.FindName("Reader")).Visibility = Visibility.Visible;
  ((TextBlock)main.FindName("Subject")).Text = "example@gmail.com 的安全提醒";
  ((TextBlock)main.FindName("SenderName")).Text = "Google";
  ((TextBlock)main.FindName("Sender")).Text = "no-reply@accounts.google.com\n收件邮箱：example@qq.com · 2026-09-27 16:36";
  ((Image)main.FindName("SenderLogo")).Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(MailIdentity.Logo("x@google.com")!));
  codes.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("链接示例：")));
  foreach (var block in linksDoc.Blocks.ToArray()) { linksDoc.Blocks.Remove(block); codes.Blocks.Add(block); }
  ((RichTextBox)main.FindName("Body")).Document = codes;
  view.Measure(new Size(1440,972)); view.Arrange(new Rect(0,0,1440,972)); view.UpdateLayout();
  var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(1440,972,96,96,System.Windows.Media.PixelFormats.Pbgra32);
  rendered.Render(view);
  var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));
  using(var file = System.IO.File.Create("artifacts/mail-ui-smoke/reference-ui.png")) encoder.Save(file);
  ((FrameworkElement)main.FindName("ReaderEmpty")).Visibility = Visibility.Visible;
  ((FrameworkElement)main.FindName("Reader")).Visibility = Visibility.Collapsed;
  view.Measure(new Size(1320,850)); view.Arrange(new Rect(0,0,1320,850)); view.UpdateLayout();
  var emptyRender = new System.Windows.Media.Imaging.RenderTargetBitmap(1320,850,96,96,System.Windows.Media.PixelFormats.Pbgra32); emptyRender.Render(view);
  var emptyPng = new System.Windows.Media.Imaging.PngBitmapEncoder(); emptyPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(emptyRender));
  using(var file=System.IO.File.Create("artifacts/mail-ui-smoke/smooth-empty.png")) emptyPng.Save(file);
  Console.WriteLine("PASS 验证码语境、跨 HTML 格式、正文保持与合成账户状态渲染");
  var scrollList = new ListBox { Height=240, Width=320, ItemsSource=Enumerable.Range(1,40).Select(i => new TextBlock { Text="合成邮件 "+i, Height=75 }).ToArray() };
  VirtualizingPanel.SetScrollUnit(scrollList,ScrollUnit.Pixel);
  ScrollViewer.SetCanContentScroll(scrollList,true);
  var testFade = new Border(); var testHint = new TextBlock();
  typeof(MailWindow).Assembly.GetType("ScreenshotApp.Mail.MailSmoothScroll")!.GetMethod("Attach")!.Invoke(null,new object[]{scrollList,testFade,testHint});
  var scrollHost = new Window { Content=scrollList, Width=340, Height=280, Left=-30000, Top=-30000, ShowActivated=false, ShowInTaskbar=false };
  scrollHost.Show(); scrollHost.UpdateLayout();
  ScrollViewer? FindScroll(DependencyObject item) {
   if(item is ScrollViewer sv) return sv;
   for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(item);i++) if(FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(item,i)) is {} found) return found;
   return null;
  }
  void Pump(int ms) {
   var frame = new System.Windows.Threading.DispatcherFrame();
   var timer = new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(ms) };
   timer.Tick += (_,_) => {timer.Stop(); frame.Continue=false;}; timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
  }
  Pump(60);
  var scroll = FindScroll(scrollList)!;
  scrollList.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent});
  Pump(400);
  if(SystemParameters.WheelScrollLines != 0 && scroll.VerticalOffset <= 0) throw new Exception("像素滚轮未移动");
  scroll.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseDownEvent});
  scroll.ScrollToEnd(); Pump(100);
  if(testFade.Visibility != Visibility.Collapsed || !testHint.Text.Contains("底部")) throw new Exception("底部卡片仍被渐隐覆盖");
  scrollHost.Close();
  Console.WriteLine("PASS 像素滚轮、滚动到底渐隐撤除及窗口关闭清理");
  main.Close(); add.Close(); prefs.Close();
 }
}

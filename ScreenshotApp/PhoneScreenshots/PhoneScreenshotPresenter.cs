using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenshotApp.Collaboration;
using ScreenshotApp.DesktopPet;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.PhoneScreenshots;

/// <summary>接收的截图留在既有接收目录；宠物与系统提醒使用同一份原图。</summary>
internal static class PhoneScreenshotPresenter
{
    private static PhoneScreenshotWindow? _bubble;
    private static readonly List<(Forms.NotifyIcon Icon, DispatcherTimer Timer)> Notices = new();
    private static long _generation;
    private static bool _initialized;

    internal static bool IsSupported(string path, string name)
    {
        if (new FileInfo(path).Length is <= 0 or > 32 * 1024 * 1024) return false;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png")) return false;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return decoder is JpegBitmapDecoder or PngBitmapDecoder && frame.PixelWidth > 0 && frame.PixelHeight > 0 &&
                (long)frame.PixelWidth * frame.PixelHeight <= 64_000_000;
        }
        catch { return false; }
    }

    internal static BitmapSource LoadImage(string path, bool thumbnail)
    {
        using var stream = File.OpenRead(path);
        var orientation = 1;
        var portrait = false;
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            portrait = decoder.Frames[0].PixelHeight > decoder.Frames[0].PixelWidth;
            if (decoder.Frames[0].Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
                orientation = Convert.ToInt32(metadata.GetQuery("/app1/ifd/{ushort=274}"));
        }
        catch { /* 无方向元数据时按原像素方向显示。 */ }
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (thumbnail)
        {
            // 气泡只需有界缩略图；长截图不再按固定宽度解码出很高的位图。
            if (portrait) image.DecodePixelHeight = 600;
            else image.DecodePixelWidth = 600;
        }
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        // 相机 JPEG 常用 EXIF 记录旋转方向，预览与复制都应用同一变换。
        var matrix = orientation switch
        {
            2 => new System.Windows.Media.Matrix(-1, 0, 0, 1, 0, 0),
            3 => new System.Windows.Media.Matrix(-1, 0, 0, -1, 0, 0),
            4 => new System.Windows.Media.Matrix(1, 0, 0, -1, 0, 0),
            5 => new System.Windows.Media.Matrix(0, 1, 1, 0, 0, 0),
            6 => new System.Windows.Media.Matrix(0, 1, -1, 0, 0, 0),
            7 => new System.Windows.Media.Matrix(0, -1, -1, 0, 0, 0),
            8 => new System.Windows.Media.Matrix(0, -1, 1, 0, 0, 0),
            _ => System.Windows.Media.Matrix.Identity
        };
        if (matrix.IsIdentity) return image;
        var transformed = new TransformedBitmap(image, new System.Windows.Media.MatrixTransform(matrix));
        transformed.Freeze();
        return transformed;
    }

    internal static void Receive(string path, bool photo = false)
    {
        var application = Application.Current;
        if (application is null || application.Dispatcher.HasShutdownStarted) return;
        var generation = Interlocked.Increment(ref _generation);
        _ = Task.Run(() =>
        {
            try
            {
                var thumbnail = LoadImage(path, true);
                application.Dispatcher.BeginInvoke(() =>
                {
                    if (generation != Interlocked.Read(ref _generation) || PhoneNotificationHub.Instance.Locked) return;
                    if (!_initialized)
                    {
                        _initialized = true;
                        SystemEvents.SessionSwitch += (_, e) =>
                        {
                            if (e.Reason == SessionSwitchReason.SessionLock)
                                application.Dispatcher.BeginInvoke(Clear);
                        };
                        application.Exit += (_, _) => Clear();
                    }
                    var previousBubble = _bubble;
                    _bubble = null;
                    var pet = application.Windows.OfType<DesktopPetWindow>().FirstOrDefault(w => w.IsVisible);
                    if (pet is not null)
                    {
                        var bubble = new PhoneScreenshotWindow(path, thumbnail, pet, photo);
                        _bubble = bubble;
                        bubble.Closed += (_, _) => { if (_bubble == bubble) _bubble = null; };
                        bubble.Show();
                    }
                    else ShowNotice(path, photo);
                    previousBubble?.Close();
                });
            }
            catch { /* 图片已保存在接收目录，预览失败不能阻断传输响应。 */ }
        });
    }

    internal static void Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("截图文件已被移动或删除");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void ShowNotice(string path, bool photo)
    {
        // 每个通知绑定自己的文件，避免点击旧通知却打开最新图片。
        while (Notices.Count >= 8) DisposeNotice(Notices[0].Icon);
        var icon = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Visible = true, Text = photo ? "X-Tool 手机照片" : "X-Tool 手机截图" };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        Notices.Add((icon, timer));
        icon.BalloonTipClicked += (_, _) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try { if (!PhoneNotificationHub.Instance.Locked) Open(path); }
                catch (Exception e) { MessageBox.Show(e.Message, "打开手机截图失败"); }
                finally { DisposeNotice(icon); }
            });
        };
        timer.Tick += (_, _) => DisposeNotice(icon);
        timer.Start();
        icon.ShowBalloonTip(10000, photo ? "收到手机照片" : "收到手机截图", "点击使用默认图片程序打开", Forms.ToolTipIcon.Info);
    }

    private static void DisposeNotice(Forms.NotifyIcon icon)
    {
        var entry = Notices.FirstOrDefault(n => n.Icon == icon);
        entry.Timer?.Stop();
        Notices.RemoveAll(n => n.Icon == icon);
        icon.Visible = false;
        icon.Dispose();
    }

    private static void Clear()
    {
        Interlocked.Increment(ref _generation);
        _bubble?.Close();
        _bubble = null;
        foreach (var entry in Notices.ToArray()) DisposeNotice(entry.Icon);
    }
}

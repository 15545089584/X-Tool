using ScreenshotApp.Mail;
using ScreenshotApp.Collaboration;
using System.Windows.Threading;

namespace ScreenshotApp;

public partial class App
{
    private readonly Dictionary<string, MailRow> _pendingMail = new();
    private DispatcherTimer? _mailTimer;
    private System.Windows.Forms.NotifyIcon? _mailBalloon;
    private DispatcherTimer? _mailBalloonTimer;
    private void DismissMailBalloon()
    {
        _mailBalloonTimer?.Stop(); _mailBalloonTimer = null;
        _mailBalloon?.Dispose(); _mailBalloon = null;
    }
    private void ShowMailBalloon(string title, string detail, Action open)
    {
        DismissMailBalloon();
        var icon = _mailBalloon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayDrawingIcon ?? System.Drawing.SystemIcons.Information,
            Text = "X-Tool · 邮件提醒", Visible = true
        };
        icon.BalloonTipClicked += (_, _) => { DismissMailBalloon(); open(); };
        icon.ShowBalloonTip(10000, title, detail, System.Windows.Forms.ToolTipIcon.Info);
        _mailBalloonTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _mailBalloonTimer.Tick += (_, _) => DismissMailBalloon();
        _mailBalloonTimer.Start();
    }
    private void StartMail()
    {
        MailService.Instance.Arrived += MailArrived;
        MailService.Instance.Start();
    }
    private void StopMail()
    {
        _mailTimer?.Stop(); DismissMailBalloon();
        MailService.Instance.Arrived -= MailArrived;
        MailService.Instance.Dispose();
    }
    private void ClearMailAlerts()
    {
        _pendingMail.Clear(); _mailTimer?.Stop(); DismissMailBalloon();
        MailWindow.HidePrivateContent();
    }
    private void MailArrived(MailArrival arrival)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsExitRequested || PhoneNotificationHub.Instance.Locked ||
                !MailService.Instance.AllowNotification(arrival.AccountId, out _)) return;
            foreach (var row in arrival.Rows) _pendingMail[row.Key] = row;
            while (_pendingMail.Count > 200) _pendingMail.Remove(_pendingMail.Keys.First());
            if (_mailTimer is null)
            {
                _mailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _mailTimer.Tick += (_, _) => DisplayMail();
            }
            if (!_mailTimer.IsEnabled) _mailTimer.Start();
        });
    }
    private void DisplayMail()
    {
        _mailTimer?.Stop();
        var state = MailService.Instance.Read();
        var rows = _pendingMail.Values.Where(r => state.Rows.Any(current => current.Key == r.Key && !current.Seen) &&
            MailService.Instance.AllowNotification(r.AccountId, out _)).OrderByDescending(r => r.Date).ToArray();
        _pendingMail.Clear();
        if (IsExitRequested || PhoneNotificationHub.Instance.Locked || rows.Length == 0) return;
        var first = rows[0];
        MailService.Instance.AllowNotification(first.AccountId, out var preview);
        var detail = preview ? first.Sender + "\n" + first.Subject : "收到新邮件，点击打开邮箱中心";
        if (detail.Length > 180) detail = detail[..180] + "…";
        var account = state.Accounts.FirstOrDefault(a => a.Id == first.AccountId);
        var title = rows.Length == 1 ? (account?.DisplayName ?? "") + " · 新邮件" : $"新邮件 · {rows.Length} 封";
        Action open = () => { if (!PhoneNotificationHub.Instance.Locked) MailWindow.Open(); };
        if (IsDesktopPetVisible)
            _desktopPetWindow!.ShowMailNotification(title, detail, open, rows.Length == 1 ? async () =>
            {
                if (PhoneNotificationHub.Instance.Locked) return;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await MailService.Instance.MarkRead(first, timeout.Token);
                }
                catch (Exception error) { ShowTrayBalloon("标为已读失败", MailService.FriendlyError(error), open); }
            } : null);
        else ShowMailBalloon(title, detail, open);
    }
}

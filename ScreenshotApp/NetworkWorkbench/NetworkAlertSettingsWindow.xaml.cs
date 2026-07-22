using System.Windows;

namespace ScreenshotApp.NetworkWorkbench;

public partial class NetworkAlertSettingsWindow : Window
{
    public NetworkAlertSettings? Result { get; private set; }
    public NetworkAlertSettingsWindow(NetworkAlertSettings settings)
    {
        InitializeComponent();
        UploadTextBox.Text = settings.HighUploadMegabytesPerSecond.ToString("0.##");
        LatencyTextBox.Text = settings.HighLatencyMilliseconds.ToString();
        BudgetTextBox.Text = settings.DailyBudgetGigabytes.ToString("0.##");
        QuietStartTextBox.Text = settings.QuietStartHour.ToString(); QuietEndTextBox.Text = settings.QuietEndHour.ToString();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(UploadTextBox.Text, out var upload) || upload <= 0 || !int.TryParse(LatencyTextBox.Text, out var latency) || latency < 50 ||
            !double.TryParse(BudgetTextBox.Text, out var budget) || budget < 0 || !int.TryParse(QuietStartTextBox.Text, out var start) || start is < 0 or > 23 ||
            !int.TryParse(QuietEndTextBox.Text, out var end) || end is < 0 or > 23)
        { MessageBox.Show(this, "请检查阈值与小时范围。", "设置无效", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Result = new NetworkAlertSettings(upload, latency, budget, start, end); DialogResult = true;
    }
}

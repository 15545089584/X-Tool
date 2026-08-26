using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.InformationVault;

public partial class InformationVaultEntryDialog : Window
{
    private readonly InformationVaultEntry _workingEntry;

    internal InformationVaultEntryDialog(InformationVaultEntry? entry = null)
    {
        InitializeComponent();
        _workingEntry = entry?.Clone() ?? new InformationVaultEntry
        {
            Type = InformationVaultEntryType.GitHubCredential
        };
        DialogTitleText.Text = entry is null ? "添加信息" : "编辑信息";
        TypeComboBox.ItemsSource = InformationVaultEntryTypes.Options;
        TypeComboBox.SelectedItem = InformationVaultEntryTypes.Options.First(option => option.Type == _workingEntry.Type);
        TitleBox.Text = _workingEntry.Title;
        AccountBox.Text = _workingEntry.Account;
        SecretBox.Password = _workingEntry.Secret;
        HostBox.Text = _workingEntry.Host;
        PortBox.Text = _workingEntry.Port;
        DatabaseBox.Text = _workingEntry.Database;
        NotesBox.Text = _workingEntry.Notes;
        RecoveryCodesBox.Text = string.Join(Environment.NewLine, _workingEntry.RecoveryCodes.Select(code => code.Value));
        ConfigureFields(_workingEntry.Type);
    }

    internal InformationVaultEntry ResultEntry => _workingEntry;

    internal void CaptureForValidation(string outputPath)
    {
        UpdateLayout();
        var width = Math.Max(1, (int)Math.Round(ActualWidth));
        var height = Math.Max(1, (int)Math.Round(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    internal void OpenTypeDropDownForValidation()
    {
        TypeComboBox.IsDropDownOpen = true;
    }

    internal void CaptureScreenForValidation(string outputPath)
    {
        UpdateLayout();
        var topLeft = PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), 0, 0, bitmap.Size);
        }

        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeComboBox.SelectedItem is InformationVaultEntryTypeOption option)
        {
            ConfigureFields(option.Type);
        }
    }

    private void ConfigureFields(InformationVaultEntryType type)
    {
        var recoveryCodes = type == InformationVaultEntryType.RecoveryCodes;
        RecoveryCodesPanel.Visibility = recoveryCodes ? Visibility.Visible : Visibility.Collapsed;
        AccountPanel.Visibility = recoveryCodes ? Visibility.Collapsed : Visibility.Visible;
        SecretPanel.Visibility = recoveryCodes ? Visibility.Collapsed : Visibility.Visible;
        ConnectionPanel.Visibility = InformationVaultEntryTypes.UsesConnectionFields(type)
            ? Visibility.Visible
            : Visibility.Collapsed;

        AccountLabel.Text = type switch
        {
            InformationVaultEntryType.DeepSeekApiKey => "用途或项目",
            InformationVaultEntryType.GitHubCredential => "GitHub 账号或用途",
            InformationVaultEntryType.VirtualMachine => "登录用户名",
            _ => "账号或用户名"
        };
        SecretLabel.Text = type switch
        {
            InformationVaultEntryType.DeepSeekApiKey => "API Key",
            InformationVaultEntryType.GitHubCredential => "令牌、密钥口令或秘密内容",
            _ => "密码"
        };
        DatabaseLabel.Text = type == InformationVaultEntryType.VirtualMachine ? "系统或用途" : "数据库";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TypeComboBox.SelectedItem is not InformationVaultEntryTypeOption option)
        {
            ValidationText.Text = "请选择记录类型。";
            return;
        }

        var title = TitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            ValidationText.Text = "请填写便于识别的名称。";
            TitleBox.Focus();
            return;
        }

        var recoveryCodes = ParseRecoveryCodes(_workingEntry.RecoveryCodes);
        if (option.Type == InformationVaultEntryType.RecoveryCodes && recoveryCodes.Count == 0)
        {
            ValidationText.Text = "请至少填写一个恢复码。";
            RecoveryCodesBox.Focus();
            return;
        }

        if (option.Type != InformationVaultEntryType.RecoveryCodes && string.IsNullOrEmpty(SecretBox.Password))
        {
            ValidationText.Text = "请填写需要保护的密码或密钥。";
            SecretBox.Focus();
            return;
        }

        _workingEntry.Type = option.Type;
        _workingEntry.Title = title;
        _workingEntry.Account = AccountBox.Text.Trim();
        _workingEntry.Secret = SecretBox.Password;
        _workingEntry.Host = HostBox.Text.Trim();
        _workingEntry.Port = PortBox.Text.Trim();
        _workingEntry.Database = DatabaseBox.Text.Trim();
        _workingEntry.Notes = NotesBox.Text.Trim();
        _workingEntry.RecoveryCodes = recoveryCodes;
        _workingEntry.UpdatedAtUtc = DateTime.UtcNow;
        DialogResult = true;
    }

    private List<InformationVaultRecoveryCode> ParseRecoveryCodes(IReadOnlyCollection<InformationVaultRecoveryCode> existingCodes)
    {
        var existingState = existingCodes
            .GroupBy(code => code.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().IsUsed, StringComparer.Ordinal);
        return RecoveryCodesBox.Text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Select(value => new InformationVaultRecoveryCode
            {
                Value = value,
                IsUsed = existingState.TryGetValue(value, out var isUsed) && isUsed
            })
            .ToList();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

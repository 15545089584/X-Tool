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
    private readonly bool _isEditing;
    private bool _isConfiguringOperatingSystemOptions;

    internal InformationVaultEntryDialog(InformationVaultEntry? entry = null)
    {
        InitializeComponent();
        _isEditing = entry is not null;
        _workingEntry = entry?.Clone() ?? new InformationVaultEntry
        {
            Type = InformationVaultEntryType.GitHubCredential
        };
        if (_workingEntry.Type == InformationVaultEntryType.RecoveryCodes)
        {
            _workingEntry.Type = InformationVaultEntryType.GitHubCredential;
        }
        DialogTitleText.Text = entry is null ? "添加信息" : "编辑信息";
        ConfigureInitialOperatingSystemOptions();
        TypeComboBox.ItemsSource = InformationVaultEntryTypes.Options;
        TypeComboBox.SelectedItem = InformationVaultEntryTypes.Options.First(option => option.Type == _workingEntry.Type);
        TitleBox.Text = _workingEntry.Title;
        AccountBox.Text = _workingEntry.Account;
        SecretBox.Password = _workingEntry.Secret;
        VisibleSecretBox.Text = _workingEntry.Secret;
        GitHubPushKeyBox.Password = _workingEntry.GitHubPushKey;
        GitHubTwoFactorCheckBox.IsChecked = _workingEntry.GitHubTwoFactorEnabled;
        HostBox.Text = _workingEntry.Host;
        PortBox.Text = _workingEntry.Port;
        DatabaseBox.Text = _workingEntry.Database;
        OperatingSystemVersionBox.Text = _workingEntry.OperatingSystemVersion;
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
        if (type == InformationVaultEntryType.RecoveryCodes)
        {
            type = InformationVaultEntryType.GitHubCredential;
        }

        var usesTitle = InformationVaultEntryTypes.UsesEditableTitle(type);
        var usesAccount = InformationVaultEntryTypes.UsesAccount(type);
        var usesConnectionFields = InformationVaultEntryTypes.UsesConnectionFields(type);

        TitlePanel.Visibility = usesTitle ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(TypePanel, 0);
        Grid.SetColumnSpan(TypePanel, usesTitle ? 1 : 3);

        AccountPanel.Visibility = usesAccount ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(SecretPanel, usesAccount ? 2 : 0);
        Grid.SetColumnSpan(SecretPanel, usesAccount ? 1 : 3);
        var showsVisibleSecret = type == InformationVaultEntryType.DeepSeekApiKey;
        SecretBox.Visibility = showsVisibleSecret ? Visibility.Collapsed : Visibility.Visible;
        VisibleSecretBox.Visibility = showsVisibleSecret ? Visibility.Visible : Visibility.Collapsed;
        GitHubOptionsCard.Visibility = type == InformationVaultEntryType.GitHubCredential
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecoveryCodesCard.Visibility = type == InformationVaultEntryType.GitHubCredential
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConnectionPanel.Visibility = usesConnectionFields
            ? Visibility.Visible
            : Visibility.Collapsed;
        var isVirtualMachine = type == InformationVaultEntryType.VirtualMachine;
        VirtualMachineSystemPanel.Visibility = isVirtualMachine ? Visibility.Visible : Visibility.Collapsed;
        DatabasePanel.Visibility = isVirtualMachine ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(PortPanel, isVirtualMachine ? 3 : 1);

        AccountLabel.Text = type switch
        {
            InformationVaultEntryType.GitHubCredential => "GitHub 账号或邮箱",
            InformationVaultEntryType.VirtualMachine => "登录用户名",
            _ => "账号或用户名"
        };
        SecretLabel.Text = type switch
        {
            InformationVaultEntryType.DeepSeekApiKey => "API Key",
            InformationVaultEntryType.GitHubCredential => "GitHub 密码",
            InformationVaultEntryType.Custom => "密码、密钥或敏感内容",
            _ => "密码"
        };
        DatabaseLabel.Text = "数据库";

        if (!_isEditing && !usesTitle)
        {
            TitleBox.Text = InformationVaultEntryTypes.GetDefaultTitle(type);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TypeComboBox.SelectedItem is not InformationVaultEntryTypeOption option)
        {
            ValidationText.Text = "请选择记录类型。";
            return;
        }

        var usesTitle = InformationVaultEntryTypes.UsesEditableTitle(option.Type);
        var usesAccount = InformationVaultEntryTypes.UsesAccount(option.Type);
        var usesConnectionFields = InformationVaultEntryTypes.UsesConnectionFields(option.Type);
        var title = usesTitle
            ? TitleBox.Text.Trim()
            : InformationVaultEntryTypes.GetDefaultTitle(option.Type);
        if (usesTitle && string.IsNullOrWhiteSpace(title))
        {
            ValidationText.Text = "请填写便于识别的名称。";
            TitleBox.Focus();
            return;
        }

        var secret = option.Type == InformationVaultEntryType.DeepSeekApiKey
            ? VisibleSecretBox.Text.Trim()
            : SecretBox.Password;
        if (string.IsNullOrEmpty(secret))
        {
            ValidationText.Text = "请填写需要保护的密码或密钥。";
            if (option.Type == InformationVaultEntryType.DeepSeekApiKey)
            {
                VisibleSecretBox.Focus();
            }
            else
            {
                SecretBox.Focus();
            }
            return;
        }

        if (InformationVaultEntryTypes.RequiresAccount(option.Type) && string.IsNullOrWhiteSpace(AccountBox.Text))
        {
            ValidationText.Text = "请填写账号或用户名，才能可靠执行一键填入。";
            AccountBox.Focus();
            return;
        }

        if (option.Type == InformationVaultEntryType.VirtualMachine &&
            (OperatingSystemComboBox.SelectedItem is not string || OperatingSystemDistributionComboBox.SelectedItem is not string))
        {
            ValidationText.Text = "请选择虚拟机的操作系统和系统版本。";
            OperatingSystemComboBox.Focus();
            return;
        }

        _workingEntry.Type = option.Type;
        _workingEntry.Title = title;
        _workingEntry.Account = usesAccount ? AccountBox.Text.Trim() : string.Empty;
        _workingEntry.Secret = secret;
        _workingEntry.GitHubPushKey = option.Type == InformationVaultEntryType.GitHubCredential
            ? GitHubPushKeyBox.Password
            : string.Empty;
        _workingEntry.GitHubTwoFactorEnabled = option.Type == InformationVaultEntryType.GitHubCredential &&
                                               GitHubTwoFactorCheckBox.IsChecked == true;
        _workingEntry.Host = usesConnectionFields ? HostBox.Text.Trim() : string.Empty;
        _workingEntry.Port = usesConnectionFields ? PortBox.Text.Trim() : string.Empty;
        _workingEntry.Database = usesConnectionFields && option.Type != InformationVaultEntryType.VirtualMachine
            ? DatabaseBox.Text.Trim()
            : string.Empty;
        _workingEntry.OperatingSystem = option.Type == InformationVaultEntryType.VirtualMachine
            ? OperatingSystemComboBox.SelectedItem?.ToString()?.Trim() ?? string.Empty
            : string.Empty;
        _workingEntry.OperatingSystemDistribution = option.Type == InformationVaultEntryType.VirtualMachine
            ? OperatingSystemDistributionComboBox.SelectedItem?.ToString()?.Trim() ?? string.Empty
            : string.Empty;
        _workingEntry.OperatingSystemVersion = option.Type == InformationVaultEntryType.VirtualMachine
            ? OperatingSystemVersionBox.Text.Trim()
            : string.Empty;
        _workingEntry.Notes = NotesBox.Text.Trim();
        _workingEntry.RecoveryCodes = option.Type == InformationVaultEntryType.GitHubCredential
            ? ParseRecoveryCodes(_workingEntry.RecoveryCodes)
            : [];
        _workingEntry.UpdatedAtUtc = DateTime.UtcNow;
        DialogResult = true;
    }

    private void ConfigureInitialOperatingSystemOptions()
    {
        _isConfiguringOperatingSystemOptions = true;
        try
        {
            OperatingSystemComboBox.ItemsSource = InformationVaultOperatingSystems.Families;
            var operatingSystem = string.IsNullOrWhiteSpace(_workingEntry.OperatingSystem)
                ? "Linux"
                : _workingEntry.OperatingSystem;
            if (!InformationVaultOperatingSystems.Families.Contains(operatingSystem, StringComparer.OrdinalIgnoreCase))
            {
                operatingSystem = "其他";
            }
            OperatingSystemComboBox.SelectedItem = InformationVaultOperatingSystems.Families.First(item =>
                string.Equals(item, operatingSystem, StringComparison.OrdinalIgnoreCase));
            ConfigureOperatingSystemDistributions(operatingSystem, _workingEntry.OperatingSystemDistribution);
        }
        finally
        {
            _isConfiguringOperatingSystemOptions = false;
        }
    }

    private void OperatingSystemComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isConfiguringOperatingSystemOptions || OperatingSystemComboBox.SelectedItem is not string operatingSystem)
        {
            return;
        }

        ConfigureOperatingSystemDistributions(operatingSystem, null);
    }

    private void ConfigureOperatingSystemDistributions(string operatingSystem, string? preferredDistribution)
    {
        var options = InformationVaultOperatingSystems.GetDistributions(operatingSystem).ToList();
        if (!string.IsNullOrWhiteSpace(preferredDistribution) &&
            !options.Contains(preferredDistribution, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(preferredDistribution);
        }

        OperatingSystemDistributionComboBox.ItemsSource = options;
        OperatingSystemDistributionComboBox.SelectedItem = !string.IsNullOrWhiteSpace(preferredDistribution)
            ? options.First(item => string.Equals(item, preferredDistribution, StringComparison.OrdinalIgnoreCase))
            : options.FirstOrDefault();
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

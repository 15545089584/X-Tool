using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenshotApp.ClipboardUi;

namespace ScreenshotApp.InformationVault;

public partial class InformationVaultView : UserControl
{
    private readonly InformationVaultStore _store = new();
    private readonly ObservableCollection<InformationVaultEntry> _visibleEntries = [];
    private InformationVaultData? _data;
    private InformationVaultEntry? _selectedEntry;
    private bool _isFirstSetup;
    private bool _secretRevealed;

    public InformationVaultView()
    {
        InitializeComponent();
        EntriesList.ItemsSource = _visibleEntries;
        TypeFilterComboBox.ItemsSource = new[] { new VaultFilterOption(null, "全部类型") }
            .Concat(InformationVaultEntryTypes.Options.Select(option => new VaultFilterOption(option.Type, option.DisplayName)))
            .ToList();
        TypeFilterComboBox.SelectedIndex = 0;
        ConfigureLockedView();
    }

    internal event Action<string>? NotificationRequested;

    internal void PrepareUnlockedValidationState()
    {
        _data = new InformationVaultData
        {
            Entries =
            [
                new InformationVaultEntry
                {
                    Type = InformationVaultEntryType.Steam,
                    Title = "测试用 Steam 账号",
                    Account = "validation@example.invalid",
                    Secret = "not-a-real-password",
                    UpdatedAtUtc = DateTime.UtcNow
                },
                new InformationVaultEntry
                {
                    Type = InformationVaultEntryType.DeepSeekApiKey,
                    Title = "DeepSeek 开发测试",
                    Account = "本地开发",
                    Secret = "sk-validation-not-real",
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                }
            ]
        };
        LockedView.Visibility = Visibility.Collapsed;
        UnlockedView.Visibility = Visibility.Visible;
        RefreshEntries();
    }

    internal void LockVault()
    {
        _store.Lock();
        _data = null;
        _selectedEntry = null;
        _visibleEntries.Clear();
        MasterPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        PasswordMessageText.Text = string.Empty;
        UnlockedView.Visibility = Visibility.Collapsed;
        LockedView.Visibility = Visibility.Visible;
        ConfigureLockedView();
    }

    private void ConfigureLockedView()
    {
        _isFirstSetup = !_store.Exists;
        LockedTitleText.Text = _isFirstSetup ? "创建本地信息库" : "解锁信息库";
        LockedDescriptionText.Text = _isFirstSetup
            ? "设置一个仅用于本地解密的主密码；忘记后只能清空信息库"
            : "输入主密码，本次 X-Tool 运行期间无需重复输入";
        ConfirmPasswordPanel.Visibility = _isFirstSetup ? Visibility.Visible : Visibility.Collapsed;
        ResetVaultButton.Visibility = _isFirstSetup ? Visibility.Collapsed : Visibility.Visible;
        UnlockButton.Content = _isFirstSetup ? "创建并进入信息库" : "解锁信息库";
        Dispatcher.BeginInvoke(() => MasterPasswordBox.Focus());
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        await UnlockOrCreateAsync();
    }

    private async void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await UnlockOrCreateAsync();
        }
    }

    private async Task UnlockOrCreateAsync()
    {
        var password = MasterPasswordBox.Password;
        if (_isFirstSetup && !string.Equals(password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            PasswordMessageText.Text = "两次输入的主密码不一致。";
            ConfirmPasswordBox.Focus();
            return;
        }

        UnlockButton.IsEnabled = false;
        UnlockButton.Content = _isFirstSetup ? "正在创建…" : "正在解锁…";
        PasswordMessageText.Text = string.Empty;
        try
        {
            _data = await Task.Run(() => _isFirstSetup ? _store.Create(password) : _store.Unlock(password));
            MasterPasswordBox.Clear();
            ConfirmPasswordBox.Clear();
            LockedView.Visibility = Visibility.Collapsed;
            UnlockedView.Visibility = Visibility.Visible;
            RefreshEntries();
            NotificationRequested?.Invoke(_isFirstSetup ? "信息库已创建并加密" : "信息库已解锁");
        }
        catch (InformationVaultPasswordException exception)
        {
            PasswordMessageText.Text = exception.Message;
            MasterPasswordBox.SelectAll();
            MasterPasswordBox.Focus();
        }
        catch (Exception exception)
        {
            PasswordMessageText.Text = exception.GetBaseException().Message;
        }
        finally
        {
            UnlockButton.IsEnabled = true;
            UnlockButton.Content = _isFirstSetup ? "创建并进入信息库" : "解锁信息库";
        }
    }

    private void LockVault_Click(object sender, RoutedEventArgs e)
    {
        LockVault();
        NotificationRequested?.Invoke("信息库已锁定");
    }

    private void ResetVaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmVaultReset())
        {
            return;
        }

        try
        {
            _store.Reset();
            LockVault();
            PasswordMessageText.Text = "原信息库已清空，请设置新的主密码。";
            NotificationRequested?.Invoke("信息库已清空并等待重新创建");
        }
        catch (Exception exception)
        {
            PasswordMessageText.Text = $"清空失败：{exception.GetBaseException().Message}";
        }
    }

    private bool ConfirmVaultReset()
    {
        var owner = Window.GetWindow(this);
        var confirmationBox = new TextBox
        {
            Height = 40,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(10, 7, 10, 7),
            FontSize = 13
        };
        var confirmButton = new Button
        {
            Width = 112,
            Height = 38,
            Margin = new Thickness(8, 0, 0, 0),
            Content = "永久清空",
            IsDefault = true
        };
        var cancelButton = new Button
        {
            Width = 88,
            Height = 38,
            Content = "取消",
            IsCancel = true
        };
        var buttonPanel = new StackPanel
        {
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal
        };
        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(confirmButton);
        var content = new StackPanel { Margin = new Thickness(26) };
        content.Children.Add(new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, Text = "清空并重置信息库" });
        content.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 9, 0, 0),
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Text = "忘记主密码后无法恢复任何记录。请输入“清空信息库”确认永久清空。"
        });
        content.Children.Add(confirmationBox);
        content.Children.Add(buttonPanel);
        var dialog = new Window
        {
            Owner = owner,
            Title = "重置信息库",
            Width = 460,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = content
        };
        confirmButton.Click += (_, _) =>
        {
            if (string.Equals(confirmationBox.Text.Trim(), "清空信息库", StringComparison.Ordinal))
            {
                dialog.DialogResult = true;
            }
            else
            {
                confirmationBox.BorderBrush = System.Windows.Media.Brushes.IndianRed;
                confirmationBox.SelectAll();
                confirmationBox.Focus();
            }
        };
        return dialog.ShowDialog() == true;
    }

    private void AddEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null)
        {
            return;
        }

        var dialog = new InformationVaultEntryDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var entry = dialog.ResultEntry;
        _data.Entries.Add(entry);
        if (!TrySave("记录已保存"))
        {
            _data.Entries.Remove(entry);
            return;
        }

        RefreshEntries(entry.Id);
    }

    private void EditEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _selectedEntry is null)
        {
            return;
        }

        var dialog = new InformationVaultEntryDialog(_selectedEntry) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var index = _data.Entries.FindIndex(entry => entry.Id == _selectedEntry.Id);
        if (index < 0)
        {
            return;
        }

        var original = _data.Entries[index];
        _data.Entries[index] = dialog.ResultEntry;
        if (!TrySave("记录已更新"))
        {
            _data.Entries[index] = original;
            return;
        }

        RefreshEntries(dialog.ResultEntry.Id);
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _selectedEntry is null)
        {
            return;
        }

        var result = MessageBox.Show(
            Window.GetWindow(this),
            $"确认删除“{_selectedEntry.Title}”吗？\n删除后无法从信息库恢复。",
            "删除信息",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var index = _data.Entries.FindIndex(entry => entry.Id == _selectedEntry.Id);
        if (index < 0)
        {
            return;
        }

        var removed = _data.Entries[index];
        _data.Entries.RemoveAt(index);
        if (!TrySave("记录已删除"))
        {
            _data.Entries.Insert(index, removed);
            return;
        }

        RefreshEntries();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (IsLoaded)
        {
            RefreshEntries(_selectedEntry?.Id);
        }
    }

    private void TypeFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshEntries(_selectedEntry?.Id);
        }
    }

    private void RefreshEntries(Guid? preferredId = null)
    {
        _visibleEntries.Clear();
        if (_data is null)
        {
            RefreshDetail(null);
            return;
        }

        var filter = (TypeFilterComboBox.SelectedItem as VaultFilterOption)?.Type;
        var query = SearchBox.Text.Trim();
        var filtered = _data.Entries
            .Where(entry => filter is null || entry.Type == filter)
            .Where(entry => string.IsNullOrWhiteSpace(query) || MatchesQuery(entry, query))
            .OrderByDescending(entry => entry.UpdatedAtUtc)
            .ToList();
        foreach (var entry in filtered)
        {
            _visibleEntries.Add(entry);
        }

        EntriesList.SelectedItem = preferredId is null
            ? _visibleEntries.FirstOrDefault()
            : _visibleEntries.FirstOrDefault(entry => entry.Id == preferredId) ?? _visibleEntries.FirstOrDefault();
        RefreshDetail(EntriesList.SelectedItem as InformationVaultEntry);
    }

    private static bool MatchesQuery(InformationVaultEntry entry, string query)
    {
        return entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Account.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Host.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Database.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Notes.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.TypeDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshDetail(EntriesList.SelectedItem as InformationVaultEntry);
    }

    private void RefreshDetail(InformationVaultEntry? entry)
    {
        _selectedEntry = entry;
        _secretRevealed = false;
        EmptyDetailView.Visibility = entry is null ? Visibility.Visible : Visibility.Collapsed;
        EntryDetailView.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        if (entry is null)
        {
            return;
        }

        DetailTitleText.Text = entry.Title;
        DetailTypeText.Text = entry.TypeDisplayName;
        AccountField.Visibility = entry.HasAccount ? Visibility.Visible : Visibility.Collapsed;
        AccountValueText.Text = entry.Account;
        SecretField.Visibility = entry.HasSecret ? Visibility.Visible : Visibility.Collapsed;
        SecretFieldLabel.Text = entry.Type switch
        {
            InformationVaultEntryType.DeepSeekApiKey => "API Key",
            InformationVaultEntryType.GitHubCredential => "令牌或密钥",
            _ => "密码"
        };
        SecretValueText.Text = "••••••••••••";
        var hasConnection = entry.HasHost || entry.HasPort || entry.HasDatabase;
        ConnectionFields.Visibility = hasConnection ? Visibility.Visible : Visibility.Collapsed;
        HostValueText.Text = string.IsNullOrWhiteSpace(entry.Host) ? "未填写" : entry.Host;
        PortValueText.Text = string.IsNullOrWhiteSpace(entry.Port) ? "未填写" : entry.Port;
        DatabaseValueText.Text = string.IsNullOrWhiteSpace(entry.Database) ? "未填写" : entry.Database;
        DatabaseFieldLabel.Text = entry.Type == InformationVaultEntryType.VirtualMachine ? "系统或用途" : "数据库";
        RecoveryCodesField.Visibility = entry.HasRecoveryCodes ? Visibility.Visible : Visibility.Collapsed;
        RecoveryCodesItems.ItemsSource = entry.RecoveryCodes;
        NotesField.Visibility = entry.HasNotes ? Visibility.Visible : Visibility.Collapsed;
        NotesValueText.Text = entry.Notes;
        AutoFillButton.Visibility = entry.SupportsAutoFill ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleSecret_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null)
        {
            return;
        }

        _secretRevealed = !_secretRevealed;
        SecretValueText.Text = _secretRevealed ? _selectedEntry.Secret : "••••••••••••";
        if (sender is Button button)
        {
            button.Content = _secretRevealed ? "隐藏" : "显示";
        }
    }

    private void CopyField_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null || sender is not FrameworkElement element)
        {
            return;
        }

        var (value, label) = element.Tag?.ToString() switch
        {
            "Account" => (_selectedEntry.Account, "账号"),
            "Secret" => (_selectedEntry.Secret, _selectedEntry.Type == InformationVaultEntryType.DeepSeekApiKey ? "API Key" : "密码或密钥"),
            "Host" => (_selectedEntry.Host, "主机"),
            "Port" => (_selectedEntry.Port, "端口"),
            "Database" => (_selectedEntry.Database, "数据库或用途"),
            _ => (string.Empty, "内容")
        };
        CopySensitiveValue(value, label);
    }

    private void CopyRecoveryCode_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _selectedEntry is null || sender is not FrameworkElement { Tag: InformationVaultRecoveryCode code })
        {
            return;
        }

        CopySensitiveValue(code.Value, "恢复码");
        if (code.IsUsed)
        {
            return;
        }

        var result = MessageBox.Show(
            Window.GetWindow(this),
            "恢复码已复制。是否将它标记为已使用？",
            "恢复码状态",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        code.IsUsed = true;
        _selectedEntry.UpdatedAtUtc = DateTime.UtcNow;
        if (TrySave("恢复码已标记为使用"))
        {
            RefreshEntries(_selectedEntry.Id);
        }
        else
        {
            code.IsUsed = false;
        }
    }

    private void CopySensitiveValue(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            NotificationRequested?.Invoke($"{label}为空，未复制");
            return;
        }

        try
        {
            ClipboardService.SetSensitiveText(value, TimeSpan.FromSeconds(30));
            NotificationRequested?.Invoke($"{label}已安全复制，30 秒后自动清除");
        }
        catch (Exception exception)
        {
            NotificationRequested?.Invoke($"复制失败：{exception.GetBaseException().Message}");
        }
    }

    private async void AutoFill_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null)
        {
            return;
        }

        AutoFillButton.IsEnabled = false;
        AutoFillButton.Content = "正在识别登录窗口…";
        try
        {
            var result = await CredentialAutoFillService.FillAsync(_selectedEntry.Clone());
            NotificationRequested?.Invoke(result.Message);
            if (!result.Success)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    result.Message,
                    "一键填入",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            NotificationRequested?.Invoke($"一键填入失败：{exception.GetBaseException().Message}");
        }
        finally
        {
            AutoFillButton.IsEnabled = true;
            AutoFillButton.Content = "一键填入客户端";
        }
    }

    private bool TrySave(string successMessage)
    {
        if (_data is null)
        {
            return false;
        }

        try
        {
            _store.Save(_data);
            NotificationRequested?.Invoke(successMessage);
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"信息库保存失败：{exception.GetBaseException().Message}",
                "保存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private sealed record VaultFilterOption(InformationVaultEntryType? Type, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}

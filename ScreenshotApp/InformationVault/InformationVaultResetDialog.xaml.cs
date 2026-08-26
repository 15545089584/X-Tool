using System.Windows;
using System.Windows.Controls;

namespace ScreenshotApp.InformationVault;

public partial class InformationVaultResetDialog : Window
{
    internal InformationVaultResetDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => ConfirmationBox.Focus();
    }

    private void ConfirmationBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ConfirmationPlaceholder.Visibility = string.IsNullOrEmpty(ConfirmationBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ValidationText.Text = string.Empty;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(ConfirmationBox.Text.Trim(), "清空信息库", StringComparison.Ordinal))
        {
            ValidationText.Text = "确认文字不正确";
            ConfirmationBox.SelectAll();
            ConfirmationBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace ScreenshotApp.Collaboration;

public partial class RemoteFileAccessWindow : Window
{
    private readonly RemoteFileAccessService _service;

    internal RemoteFileAccessWindow(RemoteFileAccessService service)
    {
        InitializeComponent();
        _service = service;
        DataContext = this;
        EnabledCheckBox.IsChecked = service.Enabled;
        RefreshRoots();
    }

    public ObservableCollection<RemoteFileRootInfo> Roots { get; } = [];

    private void RefreshRoots()
    {
        Roots.Clear();
        foreach (var root in _service.Roots)
        {
            Roots.Add(root);
        }
        EmptyState.Visibility = Roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccessHintText.Text = _service.Enabled
            ? Roots.Count == 0 ? "访问已开启，但尚未授权目录。" : $"已向可信手机开放 {Roots.Count} 个只读目录。"
            : "关闭后手机端立即无法列出或下载授权文件。";
    }

    private void EnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _service.Enabled = EnabledCheckBox.IsChecked == true;
        EnabledCheckBox.Content = _service.Enabled ? "已开启" : "已关闭";
        RefreshRoots();
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择允许已信任手机只读访问的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            _service.AddRoot(dialog.SelectedPath);
            RefreshRoots();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.GetBaseException().Message, "无法授权此文件夹",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            _service.RemoveRoot(id);
            RefreshRoots();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

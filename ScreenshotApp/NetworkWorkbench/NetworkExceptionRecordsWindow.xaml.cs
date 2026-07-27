using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ScreenshotApp.NetworkWorkbench;

public partial class NetworkExceptionRecordsWindow : Window
{
    internal NetworkExceptionRecordsWindow(IEnumerable<NetworkTimelineEvent> records)
    {
        InitializeComponent();
        var items = records.ToArray();
        RecordsListBox.ItemsSource = items;
        EmptyPanel.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

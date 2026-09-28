using System.Collections.ObjectModel;
using System.Windows;

namespace ScreenshotApp.SystemTools;

public partial class SoftwareResidualCleanupWindow : Window
{
    private readonly ObservableCollection<SoftwareResidualCandidate> _candidates;

    internal SoftwareResidualCleanupWindow(InstalledSoftwareEntry entry, IReadOnlyList<SoftwareResidualCandidate> candidates)
    {
        InitializeComponent();
        _candidates = new ObservableCollection<SoftwareResidualCandidate>(candidates);
        CandidatesListBox.ItemsSource = _candidates;
        DescriptionText.Text = $"{entry.Name} · 找到 {_candidates.Count:N0} 项仅凭名称、发布者或原卸载登记推断的候选。X-Tool 不会自动选择或删除。";
        StatusText.Text = "请逐项选择确认；没有把握时请保持未选中。";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _candidates.Where(candidate => candidate.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请先选择已经人工确认的候选项。", "未选择候选", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var registryCount = selected.Count(candidate => candidate.Kind == SoftwareResidualKind.Registry);
        var confirmation = MessageBox.Show(
            this,
            $"将处理 {selected.Length} 项候选，其中 {registryCount} 项为注册表。\n\n文件夹会移入回收站；注册表项无法由 X-Tool 恢复。是否继续？",
            "确认处理残留候选",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        IsEnabled = false;
        StatusText.Text = "正在处理所选候选…";
        try
        {
            var result = await Task.Run(() => SoftwareResidualService.DeleteSelected(selected, CancellationToken.None));
            StatusText.Text = result.Failures.Count == 0
                ? $"已处理 {result.Deleted:N0} 项。"
                : $"已处理 {result.Deleted:N0} 项，{result.Failures.Count:N0} 项失败。";
            if (result.Failures.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", result.Failures.Take(8)), "部分候选未处理", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            foreach (var candidate in selected.Where(candidate => result.Failures.All(failure => !failure.StartsWith(candidate.Location, StringComparison.OrdinalIgnoreCase))).ToArray())
                _candidates.Remove(candidate);
        }
        finally
        {
            IsEnabled = true;
        }
    }
}

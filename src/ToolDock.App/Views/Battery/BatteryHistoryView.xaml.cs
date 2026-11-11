using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>历史子页：按设备汇总历史记录，可单删 / 清理过期（对应 VigilWatt 历史管理页）。</summary>
public partial class BatteryHistoryView : UserControl
{
    private readonly ObservableCollection<HistoryRow> _rows = new();

    public BatteryHistoryView()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _rows;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        BatteryMonitor.Instance.Start();

        var devices = BatteryMonitor.Instance.GetDeviceHistoryStats();
        _rows.Clear();
        foreach (var device in devices.OrderByDescending(d => d.Last))
        {
            _rows.Add(new HistoryRow
            {
                DeviceId = device.DeviceId,
                Name = string.IsNullOrEmpty(device.Name) ? device.DeviceId : device.Name,
                Detail = $"共 {device.Count} 条 · 首次 {device.First:yyyy-MM-dd HH:mm} · 最近 {device.Last:yyyy-MM-dd HH:mm}",
            });
        }

        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var stats = BatteryMonitor.Instance.GetHistoryStats();
        SummaryText.Text = stats is null
            ? "历史库不可用"
            : $"{stats.DeviceCount} 台设备 · 共 {stats.TotalCount} 条记录 · {FormatBytes(stats.FileBytes)}";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void DeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string deviceId })
        {
            return;
        }
        if (MessageBox.Show("删除该设备的全部历史记录？此操作不可撤销。", "工具坞 · 历史记录",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        BatteryMonitor.Instance.DeleteHistory(new[] { deviceId });
        Reload();
    }

    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        var settings = BatteryMonitor.Instance.Settings;
        int days = settings?.HistoryRetentionDays ?? 90;
        if (MessageBox.Show($"清理超过 {days} 天的历史记录？", "工具坞 · 历史记录",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        var cutoff = DateTimeOffset.Now.AddDays(-days);
        int removed = BatteryMonitor.Instance.CleanupHistory(cutoff);
        Reload();
        SummaryText.Text += $" · 已清理 {removed} 条";
        _ = e;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1048576 => $"{bytes / 1048576.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>历史列表行。</summary>
public sealed class HistoryRow
{
    public string DeviceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
}

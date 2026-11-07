using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>
/// 设置子页：VigilWatt 设置页里的电量相关项——低电量阈值、采样间隔、历史保留、
/// 本机电量、剩余时间估算，以及 2.4G 品牌插件开关（部分品牌需写帧才能读电量）。
/// </summary>
public partial class BatterySettingsView : UserControl
{
    private readonly ObservableCollection<PluginRow> _plugins = new();
    private bool _loaded;

    public BatterySettingsView()
    {
        InitializeComponent();
        PluginList.ItemsSource = _plugins;
        Loaded += (_, _) => LoadSettings();
    }

    private void LoadSettings()
    {
        BatteryMonitor.Instance.Start();
        var settings = BatteryMonitor.Instance.Settings;
        if (settings is null)
        {
            StatusText.Text = "电量服务未就绪：" + BatteryMonitor.Instance.LastError;
            SaveButton.IsEnabled = false;
            return;
        }

        ThresholdBox.Text = settings.LowBatteryThreshold.ToString(CultureInfo.InvariantCulture);
        SampleBox.Text = settings.SampleIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        RetentionBox.Text = settings.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture);
        SystemBatteryToggle.IsChecked = settings.MonitorSystemBattery;
        RemainingTimeToggle.IsChecked = settings.RemainingTimeEstimateEnabled;

        _plugins.Clear();
        foreach (var plugin in BatteryMonitor.Instance.Plugins)
        {
            bool enabled = settings.PluginEnabled.TryGetValue(plugin.Name, out var on) && on;
            _plugins.Add(new PluginRow
            {
                Name = plugin.Name,
                Title = string.IsNullOrEmpty(plugin.DisplayName) ? plugin.DisplayNameEn : plugin.DisplayName,
                Subtitle = plugin.DisplayNameEn,
                IsEnabled = enabled,
            });
        }

        _loaded = true;
        StatusText.Text = "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var monitor = BatteryMonitor.Instance;
        var settings = monitor.Settings;
        if (settings is null || !_loaded)
        {
            return;
        }

        settings.LowBatteryThreshold = ParseInt(ThresholdBox.Text, 0, 100, 20);
        settings.SampleIntervalMinutes = ParseInt(SampleBox.Text, 1, 60, 5);
        settings.HistoryRetentionDays = ParseInt(RetentionBox.Text, 1, 3650, 90);
        settings.MonitorSystemBattery = SystemBatteryToggle.IsChecked == true;
        settings.RemainingTimeEstimateEnabled = RemainingTimeToggle.IsChecked == true;

        foreach (var row in _plugins)
        {
            settings.PluginEnabled[row.Name] = row.IsEnabled;
        }

        // 回写规范化后的值，避免界面停留在越界输入
        ThresholdBox.Text = settings.LowBatteryThreshold.ToString(CultureInfo.InvariantCulture);
        SampleBox.Text = settings.SampleIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        RetentionBox.Text = settings.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture);

        bool saved = monitor.SaveSettings();
        monitor.ApplySettings();
        StatusText.Text = saved ? "已保存并生效" : "已在本次运行生效，但写入配置文件失败";
    }

    private static int ParseInt(string text, int min, int max, int fallback)
        => int.TryParse(text, out var value) && value >= min && value <= max ? value : fallback;
}

/// <summary>品牌插件开关行。</summary>
public sealed class PluginRow
{
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool IsEnabled { get; set; }
}

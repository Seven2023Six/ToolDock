using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>
/// 电量设置子页：VigilWatt 设置页里的电量相关项——低电量阈值、采样间隔、历史保留、
/// 本机电量、剩余时间估算、任务栏贴片，以及 2.4G 品牌插件（下拉多选，部分品牌需写帧才能读电量）。
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

        PillToggle.IsChecked = settings.TaskbarBatteryEnabled;
        LogLevelBox.SelectedIndex = ParseLogLevel(settings.LogLevel);
        ToolEnabledToggle.IsChecked = Services.UiSettings.Load().IsToolEnabled("battery");
        PillModeBox.SelectedIndex = TaskbarBatteryMode.Normalize(settings.TaskbarBatteryMode) == TaskbarBatteryMode.Lowest ? 1 : 0;
        PillMaxBox.Items.Clear();
        foreach (var choice in TaskbarBatteryOptions.Choices)
        {
            PillMaxBox.Items.Add(choice);
        }
        PillMaxBox.SelectedItem = TaskbarBatteryOptions.Clamp(settings.TaskbarBatteryMaxDevices);

        _plugins.Clear();
        foreach (var plugin in BatteryMonitor.Instance.Plugins)
        {
            bool enabled = settings.PluginEnabled.TryGetValue(plugin.Name, out var on) && on;
            var row = new PluginRow
            {
                Name = plugin.Name,
                Title = string.IsNullOrEmpty(plugin.DisplayName) ? plugin.DisplayNameEn : plugin.DisplayName,
                Subtitle = plugin.DisplayNameEn,
                IsEnabled = enabled,
            };
            row.PropertyChanged += (_, _) => UpdatePluginSummary();
            _plugins.Add(row);
        }
        UpdatePluginSummary();

        _loaded = true;
        StatusText.Text = "";
    }

    /// <summary>下拉按钮上的汇总：0 个显示「未启用」，少量直接列名字，多了显示数量。</summary>
    private void UpdatePluginSummary()
    {
        var enabled = _plugins.Where(p => p.IsEnabled).Select(p => p.Title).ToList();
        PluginSummary.Text = enabled.Count switch
        {
            0 => "未启用",
            <= 3 => string.Join("、", enabled),
            _ => $"已启用 {enabled.Count} 个品牌",
        };
        // 弹层头部带上总数，方便在长列表里确认
        if (PluginHeader is not null)
        {
            PluginHeader.Text = enabled.Count == 0
                ? "选择要启用的品牌"
                : $"选择要启用的品牌（已启用 {enabled.Count} / {_plugins.Count}）";
        }
    }

    private void ToolEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        Services.UiSettings.Load().SetToolEnabled("battery", ToolEnabledToggle.IsChecked == true);
    }

    private void SelectAllPlugins_Click(object sender, RoutedEventArgs e)
    {
        foreach (var plugin in _plugins)
        {
            plugin.IsEnabled = true;
        }
    }

    private void ClearPlugins_Click(object sender, RoutedEventArgs e)
    {
        foreach (var plugin in _plugins)
        {
            plugin.IsEnabled = false;
        }
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
        settings.TaskbarBatteryEnabled = PillToggle.IsChecked == true;
        settings.LogLevel = LogLevelFromIndex(LogLevelBox.SelectedIndex);
        VigilWatt.Core.Utils.Log.MinLevel = LogLevelValueFromIndex(LogLevelBox.SelectedIndex);
        settings.TaskbarBatteryMode = PillModeBox.SelectedIndex == 1 ? TaskbarBatteryMode.Lowest : TaskbarBatteryMode.All;
        settings.TaskbarBatteryMaxDevices = PillMaxBox.SelectedItem is int max
            ? TaskbarBatteryOptions.Clamp(max)
            : TaskbarBatteryOptions.Default;

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

    private static int ParseLogLevel(string? value) => value?.ToLowerInvariant() switch
    {
        "debug" => 0,
        "warn" => 2,
        "error" => 3,
        _ => 1,
    };

    private static string LogLevelFromIndex(int index) => index switch
    {
        0 => "debug",
        2 => "warn",
        3 => "error",
        _ => "info",
    };

    private static VigilWatt.Core.Utils.LogLevel LogLevelValueFromIndex(int index) => index switch
    {
        0 => VigilWatt.Core.Utils.LogLevel.Debug,
        2 => VigilWatt.Core.Utils.LogLevel.Warn,
        3 => VigilWatt.Core.Utils.LogLevel.Error,
        _ => VigilWatt.Core.Utils.LogLevel.Info,
    };

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        string dir = VigilWatt.Core.Utils.AppPaths.RootDir;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    private static int ParseInt(string text, int min, int max, int fallback)
        => int.TryParse(text, out var value) && value >= min && value <= max ? value : fallback;
}

/// <summary>品牌插件下拉项（勾选即改 IsEnabled，汇总文本自动刷新）。</summary>
public sealed class PluginRow : INotifyPropertyChanged
{
    private bool _isEnabled;

    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

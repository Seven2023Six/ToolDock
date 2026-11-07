using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>
/// 设置页（Gallery 卡片风格，功能补齐）：
/// 主题三态 / 气泡通知 / 开机自启 / 内存清理策略 / 日志管理 / 关于。
/// 主题用 WPF Fluent 的 ThemeMode（Light/Dark/System），经管道持久化到 Agent。
/// </summary>
public partial class SettingsPage : UserControl
{
    private bool _syncing;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadState();
    }

    private void LoadState()
    {
        _syncing = true;

        var link = AgentLink.Instance;
        string theme;
        bool notify, auto_start, auto_clean, allow_freezes, confirm_clean, skip_uac;
        uint threshold, interval;

        if (link.Connected)
        {
            theme = link.WelcomeTheme ?? "Light";
            notify = true; // Agent 欢迎包未携带通知开关；默认开，切换后经管道生效
            auto_start = link.CurrentAutoStart;
            auto_clean = link.CurrentAutoClean;
            threshold = link.CurrentThreshold;
            interval = link.CurrentIntervalM;
            skip_uac = link.CurrentSkipUac;
            allow_freezes = link.CurrentAllowFreezes;
            confirm_clean = link.CurrentConfirmClean;
            RunModeText.Text = "托盘 Agent 已连接";
        }
        else
        {
            var settings = Core.AppSettings.Load();
            theme = settings.Theme;
            notify = settings.NotifyEnabled;
            auto_start = settings.AutoStart;
            auto_clean = settings.AutoCleanEnabled;
            threshold = settings.AutoCleanThreshold;
            interval = settings.AutoCleanIntervalMinutes;
            skip_uac = settings.AutoCleanSkipUac;
            allow_freezes = settings.AutoCleanAllowFreezes;
            confirm_clean = settings.AutoCleanConfirm;
            RunModeText.Text = "独立模式（Agent 未运行，改动仅存本地）";
        }

        ThemeModeBox.SelectedIndex = theme switch
        {
            "Dark" => 1,
            "System" => 2,
            _ => 0,
        };
        NotifyToggle.IsChecked = notify;
        AutoStartToggle.IsChecked = auto_start;
        ThresholdToggle.IsChecked = auto_clean;
        IntervalToggle.IsChecked = auto_clean;
        ThresholdBox.Text = threshold.ToString();
        IntervalBox.Text = interval.ToString();
        SkipUacToggle.IsChecked = skip_uac;
        AllowFreezesToggle.IsChecked = allow_freezes;
        ConfirmCleanToggle.IsChecked = confirm_clean;

        VersionRun.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        _syncing = false;
    }

    // ---- 外观 ----

    private void ThemeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        var theme = ThemeModeBox.SelectedIndex switch
        {
            1 => "Dark",
            2 => "System",
            _ => "Light",
        };
        App.ApplyThemeMode(theme);

        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendTheme(theme);
        }
        else
        {
            var settings = Core.AppSettings.Load();
            settings.Theme = theme;
            settings.Save();
        }
    }

    // ---- 通知 / 启动 ----

    private void Notify_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        bool enabled = NotifyToggle.IsChecked == true;
        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendNotify(enabled);
        }
        else
        {
            var settings = Core.AppSettings.Load();
            settings.NotifyEnabled = enabled;
            settings.Save();
        }
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        bool enabled = AutoStartToggle.IsChecked == true;
        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendAutoStart(enabled);
        }
        else
        {
            var settings = Core.AppSettings.Load();
            settings.AutoStart = enabled;
            settings.Save();
        }
    }

    // ---- 内存清理策略 ----

    private void SendCleanCfg()
    {
        if (_syncing)
        {
            return;
        }
        bool auto_clean = ThresholdToggle.IsChecked == true || IntervalToggle.IsChecked == true;
        uint threshold = uint.TryParse(ThresholdBox.Text, out var t) && t is >= 50 and <= 99 ? t : 90u;
        uint interval = uint.TryParse(IntervalBox.Text, out var i) && i is >= 5 and <= 720 ? i : 30u;
        bool allow_freezes = AllowFreezesToggle.IsChecked == true;
        bool confirm_clean = ConfirmCleanToggle.IsChecked == true;

        if (AgentLink.Instance.Connected)
        {
            var link = AgentLink.Instance;
            link.SendCleanCfg(link.CurrentMask, auto_clean, threshold, interval, allow_freezes, confirm_clean);
        }
        else
        {
            var settings = Core.AppSettings.Load();
            settings.AutoCleanEnabled = auto_clean;
            settings.AutoCleanThreshold = threshold;
            settings.AutoCleanIntervalMinutes = interval;
            settings.AutoCleanAllowFreezes = allow_freezes;
            settings.AutoCleanConfirm = confirm_clean;
            settings.Save();
        }
    }

    private void CleanCfg_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        // 两个开关联动：开阈值自动带开定时（与内存清理页行为一致）
        if (_syncing)
        {
            return;
        }
        if (sender == ThresholdToggle && ThresholdToggle.IsChecked == true && IntervalToggle.IsChecked != true)
        {
            _syncing = true;
            IntervalToggle.IsChecked = true;
            _syncing = false;
        }
        SendCleanCfg();
    }

    private void SkipUac_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        bool enabled = SkipUacToggle.IsChecked == true;
        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendSkipUac(enabled);
        }
        else
        {
            var settings = Core.AppSettings.Load();
            settings.AutoCleanSkipUac = enabled;
            settings.Save();
        }
    }

    // ---- 日志 ----

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "logs");
        if (!Directory.Exists(dir))
        {
            return;
        }
        if (MessageBox.Show("确定清空全部日志文件吗？", "工具坞", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        foreach (var file in Directory.GetFiles(dir, "*.log"))
        {
            try
            {
                File.WriteAllText(file, string.Empty);
            }
            catch
            {
                // 正在被写占用的日志跳过，下次再清
            }
        }
        LogView.Text = string.Empty;
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => LoadLogTail();

    private void LoadLogTail()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "logs");
        if (!Directory.Exists(dir))
        {
            LogView.Text = "（暂无日志）";
            return;
        }
        var newest = Directory.GetFiles(dir, "agent-*.log")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
        if (newest is null)
        {
            LogView.Text = "（暂无日志）";
            return;
        }
        try
        {
            var lines = File.ReadAllLines(newest.FullName);
            LogView.Text = string.Join(Environment.NewLine, lines.TakeLast(120));
        }
        catch (Exception exception)
        {
            LogView.Text = "读取失败：" + exception.Message;
        }
    }
}

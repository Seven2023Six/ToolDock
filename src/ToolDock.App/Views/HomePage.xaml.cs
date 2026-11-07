using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToolDock.App.Services;
using ToolDock.Core;

namespace ToolDock.App.Views;

/// <summary>首页：Gallery 卡片风格的总览（内存 / 时间 / 快捷开关 / 运行状态）。</summary>
public partial class HomePage : UserControl
{
    private readonly DispatcherTimer _timer;
    private bool _syncing;

    public HomePage()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => RefreshAll();
        Loaded += (_, _) => { LoadState(); RefreshAll(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void RefreshAll()
    {
        if (SystemMemory.Read() is { } memory)
        {
            UsageText.Text = memory.LoadPercent + "%";
            UsageBar.Value = memory.LoadPercent;
            MemFootnoteText.Text = $"已用 {memory.UsedGiB:0.0} / {memory.TotalGiB:0.0} GB";
        }

        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm:ss");
        DateText.Text = now.ToString("yyyy年M月d日 dddd");

        var link = AgentLink.Instance;
        if (link.Connected)
        {
            AgentStatusText.Text = $"托盘常驻运行中 · 清理区域 0x{link.CurrentMask:X}\n" +
                $"自动清理 {(link.CurrentAutoClean ? $"开（阈值 {link.CurrentThreshold}% / 间隔 {link.CurrentIntervalM} 分）" : "关")} · " +
                $"免 UAC {(link.CurrentSkipUac ? "开" : "关")}";
        }
        else
        {
            AgentStatusText.Text = "独立模式：托盘 Agent 未运行，部分开关仅保存在本地设置";
        }
    }

    private void LoadState()
    {
        _syncing = true;
        var link = AgentLink.Instance;
        bool auto_clean, skip_uac, auto_start;
        if (link.Connected)
        {
            auto_clean = link.CurrentAutoClean;
            skip_uac = link.CurrentSkipUac;
            auto_start = link.CurrentAutoStart;
        }
        else
        {
            var settings = AppSettings.Load();
            auto_clean = settings.AutoCleanEnabled;
            skip_uac = settings.AutoCleanSkipUac;
            auto_start = settings.AutoStart;
        }

        AutoCleanToggle.IsChecked = auto_clean;
        SkipUacToggle.IsChecked = skip_uac;
        AutoStartToggle.IsChecked = auto_start;
        _syncing = false;
    }

    private void SendToggles()
    {
        if (_syncing)
        {
            return;
        }
        bool auto_clean = AutoCleanToggle.IsChecked == true;
        bool skip_uac = SkipUacToggle.IsChecked == true;
        var link = AgentLink.Instance;
        if (link.Connected)
        {
            link.SendCleanCfg(link.CurrentMask, auto_clean, link.CurrentThreshold, link.CurrentIntervalM,
                link.CurrentAllowFreezes, link.CurrentConfirmClean);
            link.SendSkipUac(skip_uac);
            link.SendAutoStart(AutoStartToggle.IsChecked == true);
        }
        else
        {
            var settings = AppSettings.Load();
            settings.AutoCleanEnabled = auto_clean;
            settings.AutoCleanSkipUac = skip_uac;
            settings.AutoStart = AutoStartToggle.IsChecked == true;
            settings.Save();
        }
    }

    private void AutoClean_Changed(object sender, RoutedEventArgs e) => SendToggles();

    private void SkipUac_Changed(object sender, RoutedEventArgs e)
    {
        SendToggles();
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e) => SendToggles();

    private void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        var link = AgentLink.Instance;
        if (link.Connected)
        {
            link.SendCleanNow(confirmed: false);
        }
        else
        {
            if (CleanupCore.IsElevated())
            {
                var result = CleanupCore.Clean(CleanupCore.MaskDefault);
                MemFootnoteText.Text = $"已释放 {result.FreedBytes / 1073741824.0:0.00} GB";
            }
            else
            {
                MemFootnoteText.Text = "独立模式无管理员权限，无法清理";
            }
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToolDock.App.Services;
using ToolDock.Core;

namespace ToolDock.App.Views;

/// <summary>
/// 内存清理页：KPI 实时刷新；区域勾选 / 自动清理策略 / 立即清理均为真功能，
/// 配置经管道下发 Agent（关窗也生效），清理经提权助手执行。
/// </summary>
public partial class MemCleanPage : UserControl
{
    private readonly DispatcherTimer _timer;
    private bool _syncing;

    public MemCleanPage()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => RefreshMemory();
        Loaded += (_, _) => { LoadState(); RefreshMemory(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void LoadState()
    {
        _syncing = true;

        ulong mask;
        bool auto_clean;
        uint threshold;
        uint interval;
        bool skip_uac;

        if (AgentLink.Instance.Connected)
        {
            mask = AgentLink.Instance.CurrentMask;
            auto_clean = AgentLink.Instance.CurrentAutoClean;
            threshold = AgentLink.Instance.CurrentThreshold;
            interval = AgentLink.Instance.CurrentIntervalM;
            skip_uac = AgentLink.Instance.CurrentSkipUac;
        }
        else
        {
            var settings = Core.AppSettings.Load();
            mask = settings.AutoCleanMask;
            auto_clean = settings.AutoCleanEnabled;
            threshold = settings.AutoCleanThreshold;
            interval = settings.AutoCleanIntervalMinutes;
            skip_uac = settings.AutoCleanSkipUac;
        }

        CbWorkingSet.IsChecked = (mask & CleanupCore.RegionWorkingSet) != 0;
        CbSystemFileCache.IsChecked = (mask & CleanupCore.RegionSystemFileCache) != 0;
        CbModifiedList.IsChecked = (mask & CleanupCore.RegionModifiedList) != 0;
        CbStandbyList.IsChecked = (mask & CleanupCore.RegionStandbyList) != 0;
        CbStandbyPriority0.IsChecked = (mask & CleanupCore.RegionStandbyPriority0) != 0;
        CbModifiedFileCache.IsChecked = (mask & CleanupCore.RegionModifiedFileCache) != 0;
        CbRegistryCache.IsChecked = (mask & CleanupCore.RegionRegistryCache) != 0;
        CbCombineLists.IsChecked = (mask & CleanupCore.RegionCombineMemoryLists) != 0;

        ThresholdToggle.IsChecked = auto_clean;
        IntervalToggle.IsChecked = auto_clean;
        ThresholdBox.Text = threshold.ToString();
        IntervalBox.Text = interval.ToString();
        SkipUacToggle.IsChecked = skip_uac;

        CleanHintText.Text = AgentLink.Instance.Connected
            ? "清理经提权助手执行；开启「免 UAC」后自动清理与清理按钮将静默完成。"
            : "当前为独立模式（Agent 未运行），配置仅保存在本地。";

        _syncing = false;
    }

    private void RefreshMemory()
    {
        if (SystemMemory.Read() is not { } memory)
        {
            return;
        }

        SummaryText.Text = $"物理内存 {memory.TotalGiB:0.0} GB · 实时刷新";
        UsageText.Text = memory.LoadPercent.ToString();
        UsedColumn.Width = new System.Windows.GridLength(memory.LoadPercent, System.Windows.GridUnitType.Star);
        UsedLegendText.Text = $"已用 {memory.UsedGiB:0.0} GB";
        FreeLegendText.Text = $"可用 {memory.FreeGiB:0.0} GB";
    }

    private ulong ReadMask()
    {
        ulong mask = 0;
        mask |= CbWorkingSet.IsChecked == true ? CleanupCore.RegionWorkingSet : 0;
        mask |= CbSystemFileCache.IsChecked == true ? CleanupCore.RegionSystemFileCache : 0;
        mask |= CbModifiedList.IsChecked == true ? CleanupCore.RegionModifiedList : 0;
        mask |= CbStandbyList.IsChecked == true ? CleanupCore.RegionStandbyList : 0;
        mask |= CbStandbyPriority0.IsChecked == true ? CleanupCore.RegionStandbyPriority0 : 0;
        mask |= CbModifiedFileCache.IsChecked == true ? CleanupCore.RegionModifiedFileCache : 0;
        mask |= CbRegistryCache.IsChecked == true ? CleanupCore.RegionRegistryCache : 0;
        mask |= CbCombineLists.IsChecked == true ? CleanupCore.RegionCombineMemoryLists : 0;
        return mask;
    }

    private bool ReadAutoCleanEnabled()
        => ThresholdToggle.IsChecked == true || IntervalToggle.IsChecked == true;

    private uint ReadThreshold()
        => uint.TryParse(ThresholdBox.Text, out var v) && v is >= 50 and <= 99 ? v : 90u;

    private uint ReadInterval()
        => uint.TryParse(IntervalBox.Text, out var v) && v is >= 5 and <= 720 ? v : 30u;

    private void SendConfig()
    {
        if (_syncing)
        {
            return;
        }

        ulong mask = ReadMask();
        bool auto_clean = ReadAutoCleanEnabled();
        uint threshold = ReadThreshold();
        uint interval = ReadInterval();

        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendCleanCfg(mask, auto_clean, threshold, interval);
        }
        else
        {
            var settings = Core.AppSettings.Load();
            settings.AutoCleanMask = mask;
            settings.AutoCleanEnabled = auto_clean;
            settings.AutoCleanThreshold = threshold;
            settings.AutoCleanIntervalMinutes = interval;
            settings.Save();
        }
    }

    private void Region_Changed(object sender, RoutedEventArgs e) => SendConfig();

    private void AutoClean_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        // 两个开关联动：开一个 = 自动清理开启
        if ( sender == ThresholdToggle && ThresholdToggle.IsChecked == true && IntervalToggle.IsChecked != true)
        {
            _syncing = true;
            IntervalToggle.IsChecked = true;
            _syncing = false;
        }
        SendConfig();
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

    private void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        SendConfig(); // 先同步当前勾选，避免 Agent 用旧掩码
        CleanButton.IsEnabled = false;
        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendCleanNow();
            CleanHintText.Text = "清理指令已发送——若弹出 UAC 请允许；完成后气泡会显示释放量。";
        }
        else
        {
            // 独立模式（开发场景）：直接在本进程尝试（多半无权限，会提示）
            if (CleanupCore.IsElevated())
            {
                var result = CleanupCore.Clean(ReadMask());
                CleanHintText.Text = $"已释放内存：{result.FreedBytes / 1073741824.0:0.00} GB";
            }
            else
            {
                CleanHintText.Text = "独立模式下无管理员权限，无法清理；请经托盘的 Agent 执行。";
            }
        }
        CleanButton.IsEnabled = true;
    }
}

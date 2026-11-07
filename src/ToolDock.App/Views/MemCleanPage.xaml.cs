using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToolDock.App.Services;
using ToolDock.Core;

namespace ToolDock.App.Views;

/// <summary>
/// 内存清理页：Gallery 卡片风格。单条整体内存实时刷新，区域勾选 / 自动清理策略为真功能，
/// 与设置页共享同一份管道配置（setCleanCfg）。
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

    private void RefreshMemory()
    {
        if (SystemMemory.Read() is not { } memory)
        {
            return;
        }

        SummaryText.Text = $"物理内存 {memory.TotalGiB:0.0} GB · 实时刷新";
        UsageBar.Value = memory.LoadPercent;
        MemFootnoteText.Text = $"已用 {memory.UsedGiB:0.0} / {memory.TotalGiB:0.0} GB（{memory.LoadPercent}%）";
    }

    private void LoadState()
    {
        _syncing = true;

        ulong mask;
        bool auto_clean, allow_freezes, confirm_clean, skip_uac;
        uint threshold, interval;

        if (AgentLink.Instance.Connected)
        {
            var link = AgentLink.Instance;
            mask = link.CurrentMask;
            auto_clean = link.CurrentAutoClean;
            threshold = link.CurrentThreshold;
            interval = link.CurrentIntervalM;
            skip_uac = link.CurrentSkipUac;
            allow_freezes = link.CurrentAllowFreezes;
            confirm_clean = link.CurrentConfirmClean;
        }
        else
        {
            var settings = Core.AppSettings.Load();
            mask = settings.AutoCleanMask;
            auto_clean = settings.AutoCleanEnabled;
            threshold = settings.AutoCleanThreshold;
            interval = settings.AutoCleanIntervalMinutes;
            skip_uac = settings.AutoCleanSkipUac;
            allow_freezes = settings.AutoCleanAllowFreezes;
            confirm_clean = settings.AutoCleanConfirm;
        }

        SetRegionCheckboxes(mask);
        ThresholdToggle.IsChecked = auto_clean;
        IntervalToggle.IsChecked = auto_clean;
        ThresholdBox.Text = threshold.ToString();
        IntervalBox.Text = interval.ToString();
        SkipUacToggle.IsChecked = skip_uac;
        AllowFreezesToggle.IsChecked = allow_freezes;
        ConfirmCleanToggle.IsChecked = confirm_clean;

        _syncing = false;
    }

    private void SetRegionCheckboxes(ulong mask)
    {
        CbWorkingSet.IsChecked = (mask & CleanupCore.RegionWorkingSet) != 0;
        CbSystemFileCache.IsChecked = (mask & CleanupCore.RegionSystemFileCache) != 0;
        CbModifiedList.IsChecked = (mask & CleanupCore.RegionModifiedList) != 0;
        CbStandbyList.IsChecked = (mask & CleanupCore.RegionStandbyList) != 0;
        CbStandbyPriority0.IsChecked = (mask & CleanupCore.RegionStandbyPriority0) != 0;
        CbModifiedFileCache.IsChecked = (mask & CleanupCore.RegionModifiedFileCache) != 0;
        CbRegistryCache.IsChecked = (mask & CleanupCore.RegionRegistryCache) != 0;
        CbCombineLists.IsChecked = (mask & CleanupCore.RegionCombineMemoryLists) != 0;
    }

    private ulong ReadMask()
    {
        ulong mask = 0;
        if (CbWorkingSet.IsChecked == true) mask |= CleanupCore.RegionWorkingSet;
        if (CbSystemFileCache.IsChecked == true) mask |= CleanupCore.RegionSystemFileCache;
        if (CbModifiedList.IsChecked == true) mask |= CleanupCore.RegionModifiedList;
        if (CbStandbyList.IsChecked == true) mask |= CleanupCore.RegionStandbyList;
        if (CbStandbyPriority0.IsChecked == true) mask |= CleanupCore.RegionStandbyPriority0;
        if (CbModifiedFileCache.IsChecked == true) mask |= CleanupCore.RegionModifiedFileCache;
        if (CbRegistryCache.IsChecked == true) mask |= CleanupCore.RegionRegistryCache;
        if (CbCombineLists.IsChecked == true) mask |= CleanupCore.RegionCombineMemoryLists;
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
        bool allow_freezes = AllowFreezesToggle.IsChecked == true;
        bool confirm_clean = ConfirmCleanToggle.IsChecked == true;

        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendCleanCfg(mask, auto_clean, threshold, interval, allow_freezes, confirm_clean);
        }
        else
        {
            var settings = Core.AppSettings.Load();
            settings.AutoCleanMask = mask;
            settings.AutoCleanEnabled = auto_clean;
            settings.AutoCleanThreshold = threshold;
            settings.AutoCleanIntervalMinutes = interval;
            settings.AutoCleanAllowFreezes = allow_freezes;
            settings.AutoCleanConfirm = confirm_clean;
            settings.Save();
        }
    }

    private void Region_Changed(object sender, RoutedEventArgs e) => SendConfig();

    private void Policy_Changed(object sender, RoutedEventArgs e) => SendConfig();

    private void AutoClean_Changed(object sender, RoutedEventArgs e)
    {
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

    /// <summary>清理前确认框正文：列出所选区域。</summary>
    private string BuildConfirmText()
    {
        var lines = new List<string>();
        void Add(CheckBox box)
        {
            if (box.IsChecked == true)
            {
                lines.Add("- " + box.Content);
            }
        }
        Add(CbWorkingSet);
        Add(CbSystemFileCache);
        Add(CbModifiedList);
        Add(CbStandbyList);
        Add(CbStandbyPriority0);
        Add(CbModifiedFileCache);
        Add(CbRegistryCache);
        Add(CbCombineLists);
        return lines.Count == 0 ? "未选择任何区域" : "即将清理以下内存区域：\r\n" + string.Join("\r\n", lines) + "\r\n\r\n确定继续吗？";
    }

    private void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        SendConfig();
        if (ConfirmCleanToggle.IsChecked == true)
        {
            if (MessageBox.Show(BuildConfirmText(), "工具坞 · 内存清理",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }
        }

        CleanButton.IsEnabled = false;
        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendCleanNow(confirmed: true);
        }
        else
        {
            if (CleanupCore.IsElevated())
            {
                var result = CleanupCore.Clean(ReadMask());
                MemFootnoteText.Text = $"已释放 {result.FreedBytes / 1073741824.0:0.00} GB";
            }
            else
            {
                MemFootnoteText.Text = "独立模式无管理员权限，无法清理";
            }
        }
        CleanButton.IsEnabled = true;
    }
}

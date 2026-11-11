using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToolDock.App.Services;
using ToolDock.Core;

namespace ToolDock.App.Views;

/// <summary>
/// 内存清理页：单条整体内存（实时刷新），区域勾选 / 自动清理 / 立即清理均为真功能。
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
        _timer.Tick += (_, _) =>
        {
            // 窗口隐藏到托盘时不必每 2 秒刷新界面
            if (Window.GetWindow(this) is { IsVisible: false })
            {
                return;
            }
            RefreshMemory();
        };
        Loaded += (_, _) =>
        {
            LoadState();
            AgentLink.SkipUacChanged += OnSkipUacChanged;
            RefreshMemory();
            _timer.Start();
            _syncing = true;
            MemoryPillToggle.IsChecked = Services.MemoryPillService.Instance.IsEnabled;
            ToolEnabledToggle.IsChecked = Services.UiSettings.Load().IsToolEnabled("memclean");
            _syncing = false;
            ApplyToolEnabledState(ToolEnabledToggle.IsChecked == true, notify: false);
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            AgentLink.SkipUacChanged -= OnSkipUacChanged;
        };
    }

    /// <summary>Agent 回退免 UAC（安装被取消/失败）时把开关同步回真实状态。</summary>
    private void OnSkipUacChanged(bool enabled)
    {
        if (!IsLoaded)
        {
            return;
        }
        Dispatcher.Invoke(() =>
        {
            _syncing = true;
            SkipUacToggle.IsChecked = enabled;
            _syncing = false;
        });
    }

    private void RefreshMemory()
    {
        if (SystemMemory.Read() is not { } memory)
            return;

        SummaryText.Text = $"物理内存 {memory.TotalGiB:0.0} GB · 实时刷新";
        UsageText.Text = memory.LoadPercent.ToString();
        // 两列都用星号：比例 = percent : (100 - percent)，进度条才正确
        UsedColumn.Width = new System.Windows.GridLength(memory.LoadPercent, System.Windows.GridUnitType.Star);
        FreeColumn.Width = new System.Windows.GridLength(100 - memory.LoadPercent, System.Windows.GridUnitType.Star);
        MemFootnoteText.Text = $"已用 {memory.UsedGiB:0.0} / {memory.TotalGiB:0.0} GB";
        GroupsText.Text = $"提交内存 {memory.CommitUsedGiB:0.0} / {memory.CommitTotalGiB:0.0} GB";
        LastCleanText.Text = FormatLastClean();
    }

    /// <summary>上次清理摘要：时间 + 释放量 + 失败项（来自 Agent 的 welcome/回推）。</summary>
    private static string FormatLastClean()
    {
        var link = AgentLink.Instance;
        if (string.IsNullOrEmpty(link.CurrentLastCleanAt))
        {
            return "上次清理：尚未清理";
        }
        string failed = link.CurrentLastCleanFailed > 0 ? $"（{link.CurrentLastCleanFailed} 项失败）" : "";
        return $"上次清理：{link.CurrentLastCleanAt} 释放 {FormatBytes(link.CurrentLastCleanFreed)}{failed}";
    }

    private static string FormatBytes(ulong bytes)
    {
        double gib = bytes / 1073741824.0;
        return gib >= 0.1 ? $"{gib:0.0} GB" : $"{bytes / 1048576.0:0} MB";
    }

    private void LoadState()
    {
        _syncing = true;

        ulong mask;
        bool auto_clean;
        uint threshold;
        uint interval;
        bool skip_uac;
        bool allow_freezes;
        bool confirm_clean;
        bool bubble_results;
        bool log_results;

        if (AgentLink.Instance.Connected)
        {
            mask = AgentLink.Instance.CurrentMask;
            auto_clean = AgentLink.Instance.CurrentAutoClean;
            threshold = AgentLink.Instance.CurrentThreshold;
            interval = AgentLink.Instance.CurrentIntervalM;
            skip_uac = AgentLink.Instance.CurrentSkipUac;
            allow_freezes = AgentLink.Instance.CurrentAllowFreezes;
            confirm_clean = AgentLink.Instance.CurrentConfirmClean;
            bubble_results = AgentLink.Instance.CurrentBubbleResults;
            log_results = AgentLink.Instance.CurrentLogResults;
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
            bubble_results = settings.AutoCleanBubble;
            log_results = settings.AutoCleanLogResults;
        }

        // 规范化：避免历史/异常数据（阈值 0、间隔 0、掩码 0）在界面上呈现为「全空」
        bool unset = mask == 0 && !auto_clean && threshold == 0 && interval == 0 && !skip_uac;
        if (unset)
        {
            mask = CleanupCore.MaskDefault;
            threshold = 90;
            interval = 30;
        }
        if (threshold is < 50 or > 99)
        {
            threshold = 90;
        }
        if (interval is < 5 or > 720)
        {
            interval = 30;
        }

        SetRegionCheckboxes(mask);
        AutoCleanToggle.IsChecked = auto_clean;
        ThresholdBox.Text = threshold.ToString();
        IntervalBox.Text = interval.ToString();
        SkipUacToggle.IsChecked = skip_uac;
        AllowFreezesToggle.IsChecked = allow_freezes;
        ConfirmCleanToggle.IsChecked = confirm_clean;
        // 热键预设映射（0=关闭）
        uint hk_mods = AgentLink.Instance.Connected ? AgentLink.Instance.CurrentHotkeyMods : Core.AppSettings.Load().HotkeyCleanMods;
        uint hk_vk = AgentLink.Instance.Connected ? AgentLink.Instance.CurrentHotkeyVk : Core.AppSettings.Load().HotkeyCleanVk;
        bool hk_on = AgentLink.Instance.Connected ? AgentLink.Instance.CurrentHotkeyEnabled : Core.AppSettings.Load().HotkeyCleanEnabled;
        HotkeyBox.SelectedIndex = hk_on ? PresetIndexFor(hk_mods, hk_vk) : 0;

        BubbleToggle.IsChecked = bubble_results;
        LogResultsToggle.IsChecked = log_results;

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
        => AutoCleanToggle.IsChecked == true;

    private uint ReadThreshold()
        => uint.TryParse(ThresholdBox.Text, out var v) && v is >= 50 and <= 99 ? v : 90u;

    private uint ReadInterval()
        => uint.TryParse(IntervalBox.Text, out var v) && v is >= 5 and <= 720 ? v : 30u;

    private void SendConfig()
    {
        if (_syncing)
            return;

        ulong mask = ReadMask();
        bool auto_clean = ReadAutoCleanEnabled();
        uint threshold = ReadThreshold();
        uint interval = ReadInterval();
        bool allow_freezes = AllowFreezesToggle.IsChecked == true;
        bool confirm_clean = ConfirmCleanToggle.IsChecked == true;
        bool bubble_results = BubbleToggle.IsChecked == true;
        bool log_results = LogResultsToggle.IsChecked == true;

        // 输入越界时把规范化值写回输入框（避免界面停留在 "0" 这类无效值）
        if (ThresholdBox.Text != threshold.ToString())
        {
            ThresholdBox.Text = threshold.ToString();
        }
        if (IntervalBox.Text != interval.ToString())
        {
            IntervalBox.Text = interval.ToString();
        }

        // 设置归属：Agent 连接时由 Agent 独占写入 plugindata/memclean.json；
        // 本地 settings.json 只在独立模式（Agent 不在）下作为兜底，避免两边各写一份产生分叉。
        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendCleanCfg(mask, auto_clean, threshold, interval, allow_freezes, confirm_clean, bubble_results, log_results);
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
            settings.AutoCleanBubble = bubble_results;
            settings.AutoCleanLogResults = log_results;
            settings.Save();
        }
    }

    private void Region_Changed(object sender, RoutedEventArgs e) => SendConfig();

    private void ToolEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        bool enabled = ToolEnabledToggle.IsChecked == true;
        Services.UiSettings.Load().SetToolEnabled("memclean", enabled);
        ApplyToolEnabledState(enabled, notify: true);
    }

    /// <summary>工具停用：内容区整体禁用变灰、贴片下线、Agent 撤掉托盘入口并停止自动清理。</summary>
    private void ApplyToolEnabledState(bool enabled, bool notify)
    {
        if (ToolContent is not null)
        {
            ToolContent.IsEnabled = enabled;
            ToolContent.Opacity = enabled ? 1.0 : 0.45;
        }
        if (CleanButton is not null)
        {
            CleanButton.IsEnabled = enabled;
        }
        Services.MemoryPillService.Instance.ApplyToolState(enabled);

        if (notify && AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendToolEnabled(enabled);
        }
    }

    private static int PresetIndexFor(uint mods, uint vk)
    {
        if (mods == 0x0002 && vk == 0x70) return 1;
        if (mods == 0x0003 && vk == 0x4D) return 2;
        if (mods == 0x0006 && vk == 0x78) return 3;
        return 1;
    }

    private static (bool Enabled, uint Mods, uint Vk) PresetAt(int index) => index switch
    {
        1 => (true, 0x0002u, 0x70u),
        2 => (true, 0x0003u, 0x4Du),
        3 => (true, 0x0006u, 0x78u),
        _ => (false, 0x0002u, 0x70u),
    };

    private void Hotkey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || HotkeyBox is null)
        {
            return;
        }
        var (enabled, mods, vk) = PresetAt(HotkeyBox.SelectedIndex);
        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendHotkey(enabled, mods, vk);
            return;
        }

        var settings = Core.AppSettings.Load();
        settings.HotkeyCleanEnabled = enabled;
        settings.HotkeyCleanMods = mods;
        settings.HotkeyCleanVk = vk;
        settings.Save();
    }

    private void Bubble_Click(object sender, RoutedEventArgs e) => SendConfig();

    private void LogResults_Click(object sender, RoutedEventArgs e) => SendConfig();

    /// <summary>恢复 Mem Reduct 默认值：区域 0xE7、自动清理关、阈值 90、间隔 30、确认与气泡开。</summary>
    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        const string message = "恢复默认清理设置？区域 0xE7、自动清理关闭、阈值 90、间隔 30 分钟、清理前确认开启";
        if (MessageBox.Show(message, "工具坞 · 内存清理", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _syncing = true;
        SetRegionCheckboxes(CleanupCore.MaskDefault);
        AutoCleanToggle.IsChecked = false;
        ThresholdBox.Text = "90";
        IntervalBox.Text = "30";
        AllowFreezesToggle.IsChecked = false;
        ConfirmCleanToggle.IsChecked = true;
        BubbleToggle.IsChecked = true;
        LogResultsToggle.IsChecked = false;
        _syncing = false;

        SendConfig();
    }

    private void MemoryPill_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        Services.MemoryPillService.Instance.SetEnabled(MemoryPillToggle.IsChecked == true);
    }

    private void Back_Click(object sender, System.Windows.RoutedEventArgs e)
        => (System.Windows.Window.GetWindow(this) as MainWindow)?.ShowPage("builtin");

    private void AutoClean_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;

        SendConfig();
    }

    private void AutoValue_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            SendConfig();
        }
    }

    private void SkipUac_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;

        bool enabled = SkipUacToggle.IsChecked == true;
        if (AgentLink.Instance.Connected)
        {
            // 由 Agent 安装/停止提权助手并落盘，真实状态会经 skipUac 推送回来
            AgentLink.Instance.SendSkipUac(enabled);
            return;
        }

        var settings = Core.AppSettings.Load();
        settings.AutoCleanSkipUac = enabled;
        settings.Save();
    }

    private void AllowFreezes_Click(object sender, RoutedEventArgs e)
    {
        SendConfig();
    }

    private void ConfirmClean_Click(object sender, RoutedEventArgs e)
    {
        SendConfig();
    }

    /// <summary>清理前确认框正文：列出所选区域（对齐 Mem Reduct SOURCE_MANUAL 清单文案）。</summary>
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
        RunClean(skipConfirm: false);
    }

    /// <summary>静默清理：不弹「清理前确认」，直接下发（UAC 仍按免 UAC 设置）。</summary>
    private void SilentClean_Click(object sender, RoutedEventArgs e)
    {
        SendConfig();
        RunClean(skipConfirm: true);
    }

    private void RunClean(bool skipConfirm)
    {
        if (!skipConfirm && ConfirmCleanToggle.IsChecked == true)
        {
            if (MessageBox.Show(BuildConfirmText(), "工具坞 · 内存清理",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }
        }

        CleanButton.IsEnabled = false;
        SilentCleanButton.IsEnabled = false;
        try
        {
            if (AgentLink.Instance.Connected)
            {
                AgentLink.Instance.SendCleanNow(confirmed: true, silent: skipConfirm);
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
        }
        finally
        {
            CleanButton.IsEnabled = true;
            SilentCleanButton.IsEnabled = true;
        }
    }
}

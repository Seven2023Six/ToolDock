using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ToolDock.Abstractions;
using ToolDock.Core;

namespace ToolDock.Agent;

/// <summary>
/// 内存清理插件（移植自 Mem Reduct v3.5.2 清理逻辑，GPL-3.0）。
/// 清理本体在 ToolDock.Core.CleanupCore；本插件负责托盘贡献、自动清理策略与提权调度。
/// 非提权时优先请求常驻提权助手（CleanerDaemon，授权一次后全程静默），
/// 助手不可用时手动清理退回一次性提权，自动清理降级为逐进程工作集回收。
/// 对齐原工具的两个策略开关：自动清理默认剔除冻结区（IsAllowStandbyListCleanup=false）、
/// 手动清理前确认（IsShowReductConfirmation=true）。
/// </summary>
internal sealed class MemoryCleanPlugin : IBackgroundPlugin
{
    /// <summary>冻结区 = 备用列表 + 已修改页面列表（Mem Reduct REDUCT_MASK_FREEZES）。</summary>
    private const ulong MaskFreezes = CleanupCore.RegionStandbyList | CleanupCore.RegionModifiedList;

    /// <summary>阈值清理防抖：清理后内存不会立刻跌到阈值以下，60 秒内不重复拉起。</summary>
    private static readonly TimeSpan AutoDebounce = TimeSpan.FromSeconds(60);

    /// <summary>助手启动失败后的重试间隔：避免每次自动检查都弹一次授权。</summary>
    private static readonly TimeSpan DaemonRetry = TimeSpan.FromMinutes(30);

    public PluginMetadata Metadata { get; } = new("memclean", "内存清理", ToolboxVersion, "系统");

    /// <summary>插件版本跟随工具箱主版本（单一版本源：Directory.Build.props）。</summary>
    private static string ToolboxVersion
        => typeof(MemoryCleanPlugin).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.3.0";

    private IAgentContext? _context;
    private bool _skipUac;
    private ulong _mask = CleanupCore.MaskDefault;
    private bool _autoClean;
    private uint _threshold = 90;
    private uint _intervalMinutes = 30;
    private bool _allowStandbyAuto;
    private bool _confirmClean = true;
    /// <summary>免 UAC 助手操作的串行锁：快速连点开关时启动/停止不能交错。</summary>
    private readonly object _daemonGate = new();

    /// <summary>最新一次免 UAC 请求序号；旧请求发现序号变了就放弃，避免覆盖新状态。</summary>
    private int _skipUacSeq;

    private bool _hotkeyEnabled;
    private uint _hotkeyMods;
    private uint _hotkeyVk;
    private IDisposable? _hotkey;
    private bool _bubbleResults = true;
    private bool _logResults;
    private bool _enabled = true;
    private DateTime _lastClean = DateTime.UtcNow;
    private ulong _lastFreed;
    private DateTime? _lastCleanAt;
    private int _lastFailedCount;
    private DateTime _lastAutoHint = DateTime.MinValue;
    private DateTime _lastNearLog = DateTime.MinValue;
    private DateTime _lastAutoTrigger = DateTime.MinValue;
    private DateTime _lastDaemonAttempt = DateTime.MinValue;
    private DateTime _lastStatusUpdate = DateTime.MinValue;
    private System.Threading.Timer? _autoTimer;
    private readonly object _gate = new();

    public Task InitializeAsync(IAgentContext context, CancellationToken cancellationToken)
    {
        _context = context;

        _mask = (ulong)context.Settings.Get("Mask", (long)CleanupCore.MaskDefault);
        _autoClean = context.Settings.Get("AutoClean", false);
        _threshold = context.Settings.Get("Threshold", 90u);
        _intervalMinutes = context.Settings.Get("IntervalMinutes", 30u);
        _skipUac = context.Settings.Get("SkipUac", false);
        _allowStandbyAuto = context.Settings.Get("AllowFreezes", false);
        _confirmClean = context.Settings.Get("ConfirmClean", true);

        _hotkeyEnabled = context.Settings.Get("HotkeyEnabled", false);
        _hotkeyMods = context.Settings.Get("HotkeyMods", (uint)0x0002);
        _hotkeyVk = context.Settings.Get("HotkeyVk", (uint)0x70);
        _bubbleResults = context.Settings.Get("BubbleResults", true);
        _logResults = context.Settings.Get("LogResults", false);
        _enabled = context.Settings.Get("Enabled", true);
        if (_enabled)
        {
            context.Tray.AddMenu(MenuText, () => CleanNowExternal(confirmed: false));
            context.Tray.SetStatusLine("内存清理就绪");
        }

        // 管道服务端先就绪：提权助手启动后会主动连上来
        CleanerDaemon.Start();

        // 助手跨 Agent 重启存活：启动时只探测，缺失也不主动弹授权（开机阶段由用户操作触发）
        if (_skipUac)
        {
            Task.Run(CheckDaemon);
        }

        ApplyHotkey();

        // 1 秒采样（Mem Reduct 同款）：越过阈值后 1 秒内触发清理；
        // 清理在后台执行，用 60 秒防抖避免内存还没降下来时连续执行
        _autoTimer = new System.Threading.Timer(_ => AutoTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        context.Log.Info($"已启动（mask=0x{_mask:X} 自动清理={_autoClean} 阈值={_threshold}% 间隔={_intervalMinutes}分 免UAC={_skipUac} 自动含冻结区={_allowStandbyAuto} 清理确认={_confirmClean}）");
        return Task.CompletedTask;
    }

    private const string MenuText = "立即清理内存";

    /// <summary>工具总开关：停用后移除托盘入口与状态行，自动清理与手动清理都不再执行。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }
        _enabled = enabled;
        _context?.Settings.Set("Enabled", enabled);
        Persist();

        if (_context is { } context)
        {
            if (enabled)
            {
                context.Tray.AddMenu(MenuText, () => CleanNowExternal(confirmed: false));
                UpdateStatusLine();
            }
            else
            {
                context.Tray.RemoveMenu(MenuText);
                context.Tray.SetStatusLine(null);
            }
        }
        _context?.Log.Info("内存清理工具已" + (enabled ? "启用" : "停用"));
    }

    public bool IsEnabled => _enabled;

    public Task ShutdownAsync()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        _autoTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>按当前设置注册/注销全局热键（热键清理不走确认框，与 Mem Reduct SOURCE_HOTKEY 一致）。</summary>
    private void ApplyHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;

        if (!_hotkeyEnabled || _context is null)
        {
            return;
        }
        _hotkey = _context.Hotkeys.Register((int)_hotkeyMods, (int)_hotkeyVk, () => CleanNowExternal(confirmed: true));
        _context.Log.Info($"热键清理已注册（mods=0x{_hotkeyMods:X} vk=0x{_hotkeyVk:X}）");
    }

    /// <summary>设置页下发热键配置。</summary>
    public void SetHotkey(bool enabled, uint modifiers, uint virtualKey)
    {
        _hotkeyEnabled = enabled;
        _hotkeyMods = modifiers;
        _hotkeyVk = virtualKey;
        Persist();
        ApplyHotkey();
    }

    public (bool Enabled, uint Mods, uint Vk) GetHotkey() => (_hotkeyEnabled, _hotkeyMods, _hotkeyVk);

    // ---- 配置（App 经管道下发）----

    public (ulong Mask, bool AutoClean, uint Threshold, uint IntervalMinutes, bool SkipUac, bool AllowFreezes, bool ConfirmClean, bool BubbleResults, bool LogResults) GetCleanState()
        => (_mask, _autoClean, _threshold, _intervalMinutes, _skipUac, _allowStandbyAuto, _confirmClean, _bubbleResults, _logResults);

    /// <summary>最近一次清理结果（welcome / settings 推送给界面展示）。</summary>
    public (ulong Freed, string? At, int Failed) GetLastClean()
        => (_lastFreed, _lastCleanAt?.ToString("MM-dd HH:mm"), _lastFailedCount);

    /// <summary>
    /// 清理配置更新。**不含免 UAC**：开关由 SetSkipUac 专门管理，
    /// 否则客户端漏发该字段（默认 false）会在每次改区域/阈值时把助手停掉。
    /// </summary>
    public void ApplyConfig(ulong mask, bool autoClean, uint threshold, uint intervalMinutes, bool allowFreezes, bool confirmClean, bool bubbleResults, bool logResults)
    {
        _bubbleResults = bubbleResults;
        _logResults = logResults;
        _mask = mask;
        _autoClean = autoClean;
        // 范围钳制：非法值（0 或越界）一律回落到安全默认，避免界面出现「全 0」状态
        _threshold = threshold is >= 50 and <= 99 ? threshold : 90u;
        _intervalMinutes = intervalMinutes is >= 5 and <= 720 ? intervalMinutes : 30u;
        _allowStandbyAuto = allowFreezes;
        _confirmClean = confirmClean;

        Persist();
        UpdateStatusLine();

        _context?.Log.Info($"配置已更新（mask=0x{_mask:X} 自动={_autoClean} 阈值={_threshold}% 间隔={_intervalMinutes}分 免UAC={_skipUac} 自动含冻结区={_allowStandbyAuto} 清理确认={_confirmClean}）");
    }

    /// <summary>
    /// 免 UAC 开关（界面直接下发）。返回 true 表示最终状态与请求一致。
    /// 开启时启动常驻提权助手并**实际探测**管道可用；失败（取消授权/被拦截）就回退，
    /// 避免出现「开关开着却用不了」的假状态。
    /// </summary>
    public bool SetSkipUac(bool enabled)
    {
        int seq = Interlocked.Increment(ref _skipUacSeq);
        lock (_daemonGate)
        {
            // 等待锁期间又来了更新的请求：这一单已经过时，交给后来的那次处理
            if (seq != Volatile.Read(ref _skipUacSeq))
            {
                return true;
            }

            bool ok = true;
            if (enabled)
            {
                ok = CleanerDaemon.EnsureStarted(_context, allowPrompt: true, out string error);
                if (ok)
                {
                    _context?.Log.Info("免 UAC 提权助手已就绪（常驻，授权一次后静默清理）");
                }
                else
                {
                    _context?.Log.Warn("免 UAC 提权助手启动失败，已回退开关：" + error);
                    _context?.Notify.Show("内存清理", "免UAC助手启动失败：" + error);
                }
            }
            else
            {
                CleanerDaemon.RequestStop();
                _context?.Log.Info("免 UAC 提权助手已停止");
            }

            _skipUac = enabled && ok;
            Persist();
            UpdateStatusLine();
            return _skipUac == enabled;
        }
    }

    /// <summary>当前免 UAC 状态（管道回推界面用）。</summary>
    public bool SkipUacEnabled => _skipUac;

    // ---- 清理入口 ----

    /// <summary>
    /// 托盘菜单 / App 按钮共用的手动清理入口（线程安全）。
    /// confirmed=false 时按"清理前确认"设置弹确认框（App 弹过确认后传 true）。
    /// </summary>
    public void CleanNowExternal(bool confirmed, bool silent = false)
    {
        Task.Run(() => CleanInternal("手动", confirmed, silent));
    }

    /// <summary>自动清理生效掩码：默认剔除冻结区（Mem Reduct：SOURCE_AUTO 排除 REDUCT_MASK_FREEZES）。</summary>
    private ulong AutoEffectiveMask()
        => _allowStandbyAuto ? _mask : (_mask & ~MaskFreezes);

    private void AutoTick()
    {
        try
        {
            var snapshot = CleanupCore.ReadMemorySnapshot();
            if (snapshot is null)
            {
                return;
            }

            // 托盘提示不需要每秒刷：10 秒一次足够（贴片自己每秒读）
            if (DateTime.UtcNow - _lastStatusUpdate >= TimeSpan.FromSeconds(10))
            {
                _lastStatusUpdate = DateTime.UtcNow;
                UpdateStatusLine();
            }

            if (!_enabled || !_autoClean)
            {
                return;
            }

            bool by_threshold = _threshold > 0 && snapshot.LoadPercent >= _threshold;
            bool by_interval = _intervalMinutes > 0 && DateTime.UtcNow - _lastClean >= TimeSpan.FromMinutes(_intervalMinutes);

            // 接近阈值时每 10 分钟留一条记录：下次"没触发"能直接看到系统读到的百分比
            if (!by_threshold && !by_interval &&
                snapshot.LoadPercent + 5 >= _threshold &&
                DateTime.UtcNow - _lastNearLog >= TimeSpan.FromMinutes(10))
            {
                _lastNearLog = DateTime.UtcNow;
                _context?.Log.Info($"自动检查：{snapshot.LoadPercent}% / 阈值 {_threshold}%（未达阈值）");
            }

            if (by_threshold)
            {
                // 秒级触发 + 防抖：清理在后台跑，结果回来前及内存尚未回落时不重复执行
                if (DateTime.UtcNow - _lastAutoTrigger < AutoDebounce)
                {
                    return;
                }
                _lastAutoTrigger = DateTime.UtcNow;
                CleanInternal("阈值", confirmed: true);
            }
            else if (by_interval)
            {
                CleanInternal("定时", confirmed: true);
            }
        }
        catch (Exception exception)
        {
            (_context?.Log ?? new FileLogger(Metadata.Id)).Warn("自动清理检查失败：" + exception.Message);
        }
    }

    private void CleanInternal(string source, bool confirmed, bool silent = false)
    {
        lock (_gate)
        {
            if (_context is not { } context || !_enabled)
            {
                return;
            }

            // 自动清理永不确认；手动清理按设置弹确认框（Mem Reduct：SOURCE_MANUAL + IsShowReductConfirmation）
            bool is_auto = source is "阈值" or "定时";
            ulong mask = is_auto ? AutoEffectiveMask() : _mask;
            context.Log.Info($"开始清理（{source}，confirmed={confirmed}，mask=0x{mask:X}，已提权={context.Elevation.IsElevated}，免UAC={_skipUac}）");

            if (!is_auto && !confirmed && _confirmClean)
            {
                if (!NativeMethods.ShowConfirm(BuildRegionListText(mask)))
                {
                    context.Log.Info("手动清理已取消（用户拒绝确认）");
                    return;
                }
            }

            // 已经提权（Agent 本身以管理员运行）→ 直接清理
            if (context.Elevation.IsElevated)
            {
                var result = CleanupCore.Clean(mask);
                _lastClean = DateTime.UtcNow;
                ReportResult(context, source, result.FreedBytes, result.Regions, flushed_via_helper: false, silent);
                return;
            }

            // 免 UAC：常驻提权助手（授权一次后所有清理静默执行）
            if (_skipUac && TryDaemonClean(context, source, mask, is_auto, silent))
            {
                return;
            }

            // 自动清理是无人值守行为，绝不弹 UAC：
            // 助手不可用时降级为逐进程工作集回收（同用户进程无需管理员权限）
            if (is_auto)
            {
                var fallback = CleanupCore.TrimWorkingSets(mask);
                _lastClean = DateTime.UtcNow;
                ReportResult(context, source, fallback.FreedBytes, fallback.Regions, flushed_via_helper: false, silent);
                NoteAutoDegraded(context, $"提权助手不可用，已按用户权限回收工作集（释放 {FormatBytes(fallback.FreedBytes)}，{fallback.PrivilegeDiag}）");
                return;
            }

            // 手动：拉起一次性提权助手（UAC）；结果经临时文件回传
            CleanViaElevatedHelper(context, source, mask, silent);
        }
    }

    /// <summary>请求常驻助手清理；返回 false 表示需要调用方继续走降级/一次性提权。</summary>
    private bool TryDaemonClean(IAgentContext context, string source, ulong mask, bool is_auto, bool silent)
    {
        context.Log.Info("尝试通过提权助手清理");
        if (!CleanerDaemon.IsRunning(200))
        {
            // 自动清理只在重试窗口外尝试启动，避免每次检查都请求授权
            if (is_auto && DateTime.UtcNow - _lastDaemonAttempt < DaemonRetry)
            {
                context.Log.Info("提权助手未运行（重试窗口内），本次跳过助手");
                return false;
            }

            _lastDaemonAttempt = DateTime.UtcNow;
            if (!CleanerDaemon.EnsureStarted(context, allowPrompt: true, out string error))
            {
                context.Log.Warn("提权助手启动失败：" + error);
                return false;
            }
        }

        if (!CleanerDaemon.TryClean(mask, out var result, out string clean_error))
        {
            context.Log.Warn("提权助手清理失败：" + clean_error);
            return false;
        }

        _lastClean = DateTime.UtcNow;
        context.Log.Info("提权助手返回清理结果");
        try
        {
            ReportResult(context, source, result.FreedBytes, result.Regions, flushed_via_helper: true, silent);
        }
        catch (Exception exception)
        {
            context.Log.Warn("上报清理结果失败：" + exception.GetType().Name + " " + exception.Message);
        }
        context.Log.Info($"清理已通过提权助手执行（{source}，mask=0x{mask:X}）");
        return true;
    }

    /// <summary>一次性提权清理（免 UAC 未开启时的手动路径）：UAC 授权后由本程序的管理员副本执行。</summary>
    private void CleanViaElevatedHelper(IAgentContext context, string source, ulong mask, bool silent)
    {
        string? helper = GetHelperPath();
        if (helper is null)
        {
            context.Notify.Show("内存清理", "找不到提权程序（ToolDock.Agent.exe）");
            return;
        }

        string result_file = Path.Combine(Path.GetTempPath(), $"tooldock_clean_{Guid.NewGuid():N}.txt");
        bool started = context.Elevation.RunElevatedAsync(helper, $"--clean {mask} -out \"{result_file}\"").GetAwaiter().GetResult();

        if (!started)
        {
            context.Notify.Show("内存清理", "提权被取消，未执行清理");
            return;
        }

        // 等待助手完成（最多 15 秒）；文件可能刚创建还在写入，稍后再读
        for (int i = 0; i < 30 && !File.Exists(result_file); i++)
        {
            Thread.Sleep(500);
        }

        ulong freed = 0;
        List<CleanupCore.RegionResult>? regions = null;
        if (File.Exists(result_file))
        {
            Thread.Sleep(150);
            try
            {
                foreach (var line in File.ReadAllLines(result_file))
                {
                    if (line.StartsWith("freed=", StringComparison.Ordinal) &&
                        ulong.TryParse(line[6..], out var v))
                    {
                        freed = v;
                    }
                    else if (line.StartsWith("region=", StringComparison.Ordinal))
                    {
                        // region=名称|status=0x…（ElevatedHelper 回传）
                        var parts = line[7..].Split('|');
                        if (parts.Length == 2 && parts[1].StartsWith("status=0x", StringComparison.Ordinal) &&
                            uint.TryParse(parts[1][8..], NumberStyles.HexNumber, null, out var st))
                        {
                            (regions ??= new List<CleanupCore.RegionResult>()).Add(new CleanupCore.RegionResult(parts[0], st));
                        }
                    }
                }
                File.Delete(result_file);
            }
            catch
            {
            }
        }
        else
        {
            context.Log.Warn("提权助手未回传结果（可能被取消或被安全软件拦截）");
        }

        _lastClean = DateTime.UtcNow;
        ReportResult(context, source, freed, regions, flushed_via_helper: true, silent);
    }

    /// <summary>气泡 + 托盘状态行 + 日志（失败区域按 Mem Reduct 逐项记日志）。</summary>
    private void ReportResult(IAgentContext context, string source, ulong freed, IReadOnlyList<CleanupCore.RegionResult>? regions, bool flushed_via_helper, bool silent = false)
    {
        var failed = regions?.Where(r => (int)r.Status < 0).ToList();
        _lastFreed = freed;
        _lastCleanAt = DateTime.Now;
        _lastFailedCount = failed?.Count ?? 0;
        string summary = $"已释放内存：{FormatBytes(freed)}";
        if (failed is { Count: > 0 })
        {
            summary += $"（{failed.Count} 项失败）";
        }

        if (_bubbleResults && !silent)
        {
            context.Notify.Show("内存清理", summary);
        }
        context.Tray.SetStatusLine($"内存 {CleanupCore.ReadMemorySnapshot()?.LoadPercent ?? 0}% · 已释放 {FormatBytes(freed)}");
        if (_logResults)
        {
            context.Log.Info($"清理完成（{source}{(flushed_via_helper ? "，经提权助手" : "")}）：释放 {FormatBytes(freed)}");
        }

        if (failed is { Count: > 0 })
        {
            foreach (var region in failed)
            {
                context.Log.Warn($"区域「{region.Name}」清理失败：0x{region.Status:X8}");
            }
        }
    }

    /// <summary>确认框正文：列出将清理的区域（对齐 Mem Reduct SOURCE_MANUAL 分支的清单文案）。</summary>
    private static string BuildRegionListText(ulong mask)
    {
        var lines = new List<string>();
        void Add(ulong bit, string title)
        {
            if ((mask & bit) == bit)
            {
                lines.Add("- " + title);
            }
        }
        Add(CleanupCore.RegionWorkingSet, "工作集");
        Add(CleanupCore.RegionSystemFileCache, "系统文件缓存");
        Add(CleanupCore.RegionModifiedList, "已修改页面列表");
        Add(CleanupCore.RegionStandbyList, "备用列表");
        Add(CleanupCore.RegionStandbyPriority0, "备用列表（无优先级）");
        Add(CleanupCore.RegionModifiedFileCache, "已修改文件缓存");
        Add(CleanupCore.RegionRegistryCache, "注册表缓存");
        Add(CleanupCore.RegionCombineMemoryLists, "合并内存列表");
        return lines.Count == 0 ? "未选择任何区域" : "即将清理以下内存区域：\r\n" + string.Join("\r\n", lines) + "\r\n\r\n确定继续吗？";
    }

    private string? GetHelperPath()
    {
        // 优先用主程序自身提权（ToolDock.Agent.exe --clean）：独立未签名 helper
        // 容易被安全软件拦截/锁文件。helper 仅作旧布局兜底。
        string? self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self) && File.Exists(self))
        {
            return self;
        }

        string base_dir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(base_dir, "helper", "ToolDock.ElevatedHelper.exe"),
            Path.Combine(base_dir, "ToolDock.ElevatedHelper.exe"),
            Path.Combine(base_dir, "..", "..", "..", "..", "ToolDock.ElevatedHelper", "bin", "Release", "net8.0-windows", "ToolDock.ElevatedHelper.exe"),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>启动时只探测助手，不在开机阶段主动请求授权。</summary>
    private void CheckDaemon()
    {
        if (_context is not { } context)
        {
            return;
        }
        try
        {
            if (CleanerDaemon.IsRunning(500))
            {
                context.Log.Info("免 UAC 提权助手已在运行");
                return;
            }
            context.Log.Info("免 UAC 提权助手未运行：首次清理时会请求一次管理员授权（之后常驻静默）");
        }
        catch (Exception exception)
        {
            context.Log.Warn("检测提权助手失败：" + exception.Message);
        }
    }

    /// <summary>自动清理降级说明：30 分钟最多记一条，避免每次检查刷日志/气泡。</summary>
    private void NoteAutoDegraded(IAgentContext context, string reason)
    {
        if (DateTime.UtcNow - _lastAutoHint < TimeSpan.FromMinutes(30))
        {
            return;
        }
        _lastAutoHint = DateTime.UtcNow;
        context.Log.Warn("自动清理降级：" + reason);
    }

    private void Persist()
    {
        if (_context is null)
        {
            return;
        }
        try
        {
            _context.Settings.Set("Mask", (long)_mask);
            _context.Settings.Set("AutoClean", _autoClean);
            _context.Settings.Set("Threshold", _threshold);
            _context.Settings.Set("IntervalMinutes", _intervalMinutes);
            _context.Settings.Set("SkipUac", _skipUac);
            _context.Settings.Set("AllowFreezes", _allowStandbyAuto);
            _context.Settings.Set("ConfirmClean", _confirmClean);
            _context.Settings.Set("HotkeyEnabled", _hotkeyEnabled);
            _context.Settings.Set("HotkeyMods", (long)_hotkeyMods);
            _context.Settings.Set("HotkeyVk", (long)_hotkeyVk);
            _context.Settings.Set("BubbleResults", _bubbleResults);
            _context.Settings.Set("LogResults", _logResults);
            _context.Settings.SaveAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _context?.Log.Warn("设置保存失败：" + exception.Message);
        }
    }

    private void UpdateStatusLine()
    {
        var snapshot = CleanupCore.ReadMemorySnapshot();
        string mode = _autoClean ? $"自动（阈值 {_threshold}%）" : "手动";
        _context?.Tray.SetStatusLine($"内存 {snapshot?.LoadPercent ?? 0}% · {mode}");
    }

    private static string FormatBytes(ulong bytes)
    {
        double gib = bytes / 1073741824.0;
        return gib >= 0.1 ? $"{gib:0.0} GB" : $"{bytes / 1048576.0:0} MB";
    }

    /// <summary>Agent 端确认框（托盘菜单路径无 UI，用 MessageBoxW 模态确认，同 Mem Reduct 行为）。</summary>
    private static class NativeMethods
    {
        public const int IDOK = 1;
        private const uint MB_OKCANCEL = 0x01;
        private const uint MB_ICONQUESTION = 0x20;
        private const uint MB_TOPMOST = 0x40000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

        /// <summary>返回 true = 用户点了确定。</summary>
        public static bool ShowConfirm(string text)
            => MessageBoxW(nint.Zero, text, "工具坞 · 内存清理", MB_OKCANCEL | MB_ICONQUESTION | MB_TOPMOST) == IDOK;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ToolDock.Abstractions;
using ToolDock.Core;

namespace ToolDock.Agent;

/// <summary>
/// 内存清理插件（移植自 Mem Reduct v3.5.2 清理逻辑，GPL-3.0）。
/// 清理本体在 ToolDock.Core.CleanupCore；本插件负责托盘贡献、自动清理策略与提权调度。
/// 非提权时经 IElevationService 拉起 ElevatedHelper（UAC）；开启"免 UAC"后走计划任务。
/// 对齐原工具的两个策略开关：自动清理默认剔除冻结区（IsAllowStandbyListCleanup=false）、
/// 手动清理前确认（IsShowReductConfirmation=true）。
/// </summary>
internal sealed class MemoryCleanPlugin : IBackgroundPlugin
{
    private const string HelperRelativePath = "helper/ToolDock.ElevatedHelper.exe";
    private const string TaskName = "ToolDock_Clean";

    /// <summary>自动清理专用任务：掩码按策略剔除冻结区，与手动任务分开安装。</summary>
    private const string TaskNameAuto = "ToolDock_CleanAuto";

    /// <summary>冻结区 = 备用列表 + 已修改页面列表（Mem Reduct REDUCT_MASK_FREEZES）。</summary>
    private const ulong MaskFreezes = CleanupCore.RegionStandbyList | CleanupCore.RegionModifiedList;

    public PluginMetadata Metadata { get; } = new("memclean", "内存清理", "0.3.0", "系统");

    private IAgentContext? _context;
    private bool _skipUac;
    private ulong _mask = CleanupCore.MaskDefault;
    private bool _autoClean;
    private uint _threshold = 90;
    private uint _intervalMinutes = 30;
    private bool _allowStandbyAuto;
    private bool _confirmClean = true;
    /// <summary>免 UAC 任务操作的串行锁：快速连点开关时安装/卸载不能交错，否则会互相删掉对方的结果。</summary>
    private readonly object _taskGate = new();

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
    private DateTime _lastAutoHint = DateTime.MinValue;
    private DateTime _lastNearLog = DateTime.MinValue;
    private DateTime _lastAutoTrigger = DateTime.MinValue;
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

        // 免 UAC 任务跨会话持久：启动时校验存在性/掩码，缺失或过期才重建（重建需提权，能免则免）
        if (_skipUac)
        {
            Task.Run(EnsureTasks);
        }

        ApplyHotkey();

        // 1 秒采样（Mem Reduct 同款）：越过阈值后 1 秒内触发清理；
        // 清理任务异步执行，用 5 秒防抖避免内存还没降下来时连续拉起任务
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

    /// <summary>
    /// 清理配置更新。**不含免 UAC**：开关由 SetSkipUac 专门管理，
    /// 否则客户端漏发该字段（默认 false）会在每次改区域/阈值时把任务删掉。
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

        // 免 UAC 开着时顺手校验任务（缺失且已提权才静默重建，不弹 UAC），但不改变开关状态
        if (_skipUac)
        {
            Task.Run(EnsureTasks);
        }

        _context?.Log.Info($"配置已更新（mask=0x{_mask:X} 自动={_autoClean} 阈值={_threshold}% 间隔={_intervalMinutes}分 免UAC={_skipUac} 自动含冻结区={_allowStandbyAuto} 清理确认={_confirmClean}）");
    }

    /// <summary>
    /// 免 UAC 开关（界面直接下发）。返回 true 表示最终状态与请求一致。
    /// 开启时安装并**实际校验**两个任务都存在；失败（取消 UAC / 创建被拒）就回退，
    /// 避免出现「开关开着却用不了」的假状态。任务操作全程串行，快速连点不会互相覆盖。
    /// </summary>
    public bool SetSkipUac(bool enabled)
    {
        int seq = Interlocked.Increment(ref _skipUacSeq);
        lock (_taskGate)
        {
            // 等待锁期间又来了更新的请求：这一单已经过时，交给后来的那次处理
            if (seq != Volatile.Read(ref _skipUacSeq))
            {
                return true;
            }

            _skipUac = enabled;
            bool ok = true;
            if (enabled)
            {
                ok = InstallTask()
                     && TaskExists(TaskName, ElevatedOperations.ManualArg)
                     && TaskExists(TaskNameAuto, ElevatedOperations.AutoArg);
                if (!ok)
                {
                    _skipUac = false;
                    _context?.Log.Warn("免 UAC 任务安装或校验失败，已回退开关");
                }
            }
            else
            {
                UninstallTask();
            }
            Persist();
            UpdateStatusLine();
            return ok;
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
                // 秒级触发 + 5 秒防抖：清理任务在后台跑，任务结果回来前不重复拉起
                if (DateTime.UtcNow - _lastAutoTrigger < TimeSpan.FromSeconds(5))
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

            // 自动清理是无人值守行为，绝不弹 UAC（Mem Reduct 对自动路径同样静默处理）：
            // 免 UAC 开着走计划任务；没开或任务执行失败则跳过并节流提示，等用户开启免 UAC 后自然恢复
            if (is_auto)
            {
                string task = AutoEffectiveMask() != _mask ? TaskNameAuto : TaskName;
                if (_skipUac && RunScheduledTask(task))
                {
                    _lastClean = DateTime.UtcNow;
                    if (!silent && _bubbleResults)
                    {
                        context.Notify.Show("内存清理", $"自动清理已执行（{source}）");
                    }
                    context.Log.Info($"自动清理已通过计划任务 {task} 执行（{source}，mask=0x{mask:X}）");
                    return;
                }
                HintAutoBlocked(context, reason: _skipUac ? "免UAC任务执行失败" : "未开启免UAC");
                return;
            }

            // 免 UAC：计划任务以最高权限静默执行（手动走全掩码任务）
            if (_skipUac)
            {
                if (RunScheduledTask(TaskName))
                {
                    _lastClean = DateTime.UtcNow;
                    if (!silent && _bubbleResults)
                    {
                        context.Notify.Show("内存清理", $"清理已执行（{source}）");
                    }
                    else if (silent)
                    {
                        context.Log.Info("静默清理：已抑制气泡提示");
                    }
                    context.Log.Info($"清理已通过计划任务 {TaskName} 执行（{source}，mask=0x{mask:X}）");
                    return;
                }
                context.Log.Warn("计划任务执行失败，回退到提权助手");
            }

            // 拉起提权助手（UAC）；结果经临时文件回传
            string helper = GetHelperPath();
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

            // 等待助手完成（最多 30 秒）
            for (int i = 0; i < 30 && !File.Exists(result_file); i++)
            {
                Thread.Sleep(1000);
            }

            ulong freed = 0;
            List<CleanupCore.RegionResult>? regions = null;
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
                            uint.TryParse(parts[1][8..], System.Globalization.NumberStyles.HexNumber, null, out var st))
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

            _lastClean = DateTime.UtcNow;
            ReportResult(context, source, freed, regions, flushed_via_helper: true, silent);
        }
    }

    /// <summary>气泡 + 托盘状态行 + 日志（失败区域按 Mem Reduct 逐项记日志）。</summary>
    private void ReportResult(IAgentContext context, string source, ulong freed, IReadOnlyList<CleanupCore.RegionResult>? regions, bool flushed_via_helper, bool silent = false)
    {
        var failed = regions?.Where(r => (int)r.Status < 0).ToList();
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
        // 优先用主程序自身提权（E:\..\ToolDock.Agent.exe --xxx）：独立未签名 helper
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

    private bool RunScheduledTask(string task)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks", $"/run /tn {task}")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
            });
            process?.WaitForExit(15000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- 免 UAC 任务管理 ----
    // 关键事实：创建 RunLevel=Highest 的任务必须提权（非提权 schtasks /create 一律"拒绝访问"），
    // 所以安装走 ElevatedHelper（开关切换时弹一次 UAC，之后全自动静默）；卸载/查询普通权限即可。

    /// <summary>确保免 UAC 任务存在（任务不带掩码数字，改配置无需重建）；只在缺失时重建（重建要提权）。</summary>
    private void EnsureTasks()
    {
        lock (_gate)
        {
            if (_context is not { } context || !_skipUac)
            {
                return;
            }

            if (TaskExists(TaskName, ElevatedOperations.ManualArg) && TaskExists(TaskNameAuto, ElevatedOperations.AutoArg))
            {
                return; // 任务健在且参数正确，不打扰用户
            }

            // 启动阶段绝不弹 UAC：非提权时只记日志，由自动清理的节流提示引导用户重新开关
            if (!context.Elevation.IsElevated)
            {
                context.Log.Warn("免 UAC 任务缺失，已跳过自动重建（避免开机弹 UAC）；请在设置里重新开关「免 UAC 提权」");
                return;
            }

            InstallTask();
        }
    }

    private bool InstallTask()
    {
        lock (_taskGate)
        {
        if (_context is not { } context)
        {
            return false;
        }
        string? helper = GetHelperPath();
        if (helper is null)
        {
            context.Log.Warn("无法安装免 UAC 任务：找不到提权助手");
            context.Notify.Show("内存清理", "找不到提权助手，免UAC不可用");
            return false;
        }

        bool ok;
        if (context.Elevation.IsElevated)
        {
            ok = CreateTask(helper, TaskName, ElevatedOperations.ManualArg)
                 && CreateTask(helper, TaskNameAuto, ElevatedOperations.AutoArg);
        }
        else
        {
            // 经提权助手创建（RunLevel=Highest 必须提权）：开关切换时弹一次 UAC，属用户主动操作
            string result_file = Path.Combine(Path.GetTempPath(), $"tooldock_task_{Guid.NewGuid():N}.txt");
            bool started = context.Elevation.RunElevatedAsync(helper,
                $"--install-task -out \"{result_file}\"").GetAwaiter().GetResult();
            if (!started)
            {
                context.Notify.Show("内存清理", "提权被取消或被安全软件拦截：请允许 ToolDock 提权，或在安全软件中信任 ToolDock 目录后重试");
                context.Log.Warn("免 UAC 任务安装未启动（取消或安全软件拦截）");
                return false;
            }
            for (int i = 0; i < 35 && !File.Exists(result_file); i++)
            {
                Thread.Sleep(1000);
            }
            ok = File.Exists(result_file) && File.ReadAllText(result_file).StartsWith("ok=1", StringComparison.Ordinal);
            try { File.Delete(result_file); } catch { }
        }

        if (ok)
        {
            context.Log.Info($"免 UAC 计划任务已安装（手动/自动两个任务，掩码运行时读取，当前 mask=0x{_mask:X} 自动=0x{AutoEffectiveMask():X}）");
        }
        else
        {
            context.Log.Warn("免 UAC 计划任务安装失败：提权助手创建任务未成功");
            context.Notify.Show("内存清理", "免UAC任务创建失败，请重试开关");
        }
        return ok;
        }
    }

    private bool CreateTask(string helper, string task, string mode)
    {
        try
        {
            // 触发器设为十年后一次：永不自动触发、永不过期、/run 随时可调；
            // /sd 只认固定补零的 yyyy/MM/dd（系统短日期格式 zh-CN 的 yyyy/M/d 会被 schtasks 拒绝）
            string date = DateTime.Now.AddYears(10).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
            using var process = Process.Start(new ProcessStartInfo("schtasks",
                $"/create /f /tn {task} /tr \"\\\"{helper}\\\" {mode}\" /sc once /sd {date} /st 00:00 /rl highest")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
            });
            process?.WaitForExit(15000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void UninstallTask()
    {
        lock (_taskGate)
        {
        // 两个任务本来就不存在时直接返回：否则会白白拉起提权助手弹一次 UAC
        if (!TaskExists(TaskName, ElevatedOperations.ManualArg) && !TaskExists(TaskNameAuto, ElevatedOperations.AutoArg))
        {
            _context?.Log.Info("免 UAC 任务本就不存在，跳过卸载");
            return;
        }

        bool direct_ok = DeleteTask(TaskName) && DeleteTask(TaskNameAuto);
        if (direct_ok)
        {
            _context?.Log.Info("免 UAC 计划任务已删除");
            return;
        }
        if (_context is not { } context)
        {
            return;
        }

        // highest 任务非提权删不掉（实测"拒绝访问"）→ 经提权助手删除（关闭开关属用户主动操作，弹一次UAC可接受）
        string? helper = GetHelperPath();
        if (helper is null)
        {
            context.Log.Warn("免 UAC 任务删除失败且找不到提权助手（残留任务无害，仅在被手动 /run 时生效）");
            return;
        }
        string result_file = Path.Combine(Path.GetTempPath(), $"tooldock_task_{Guid.NewGuid():N}.txt");
        bool started = context.Elevation.RunElevatedAsync(helper, $"--uninstall-task -out \"{result_file}\"").GetAwaiter().GetResult();
        if (started)
        {
            for (int i = 0; i < 35 && !File.Exists(result_file); i++)
            {
                Thread.Sleep(1000);
            }
            try { File.Delete(result_file); } catch { }
            context.Log.Info("免 UAC 计划任务删除请求已执行（经提权助手）");
        }
        }
    }

    private bool DeleteTask(string task)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks", $"/delete /f /tn {task}")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
            });
            process?.WaitForExit(15000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>查询任务存在且命令行包含预期参数（能识别旧格式任务并触发迁移重建）。</summary>
    private static bool TaskExists(string task, string expected_arg)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks", $"/query /tn {task} /fo list /v")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (process is null)
            {
                return false;
            }
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            // 参数与目标程序都要匹配：旧版指向 ElevatedHelper.exe 的任务视为过期，需要重建
            string exeName = Path.GetFileName(Environment.ProcessPath ?? "ToolDock.Agent.exe");
            return process.ExitCode == 0
                   && output.Contains(expected_arg, StringComparison.Ordinal)
                   && output.Contains(exeName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>自动清理被跳过时的节流提示（30 分钟一次，避免每分钟气泡骚扰）。</summary>
    private void HintAutoBlocked(IAgentContext context, string reason)
    {
        if (DateTime.UtcNow - _lastAutoHint < TimeSpan.FromMinutes(30))
        {
            context.Log.Info($"自动清理跳过（{reason}），提示节流中");
            return;
        }
        _lastAutoHint = DateTime.UtcNow;
        string message = reason == "未开启免UAC"
            ? "自动清理需要开启「免 UAC 提权」开关（设置一次后即可静默执行）"
            : "免UAC任务执行失败，请在设置里重新开关「免 UAC 提权」以重建任务";
        context.Notify.Show("内存清理", message);
        context.Log.Warn($"自动清理已跳过：{reason}");
        context.Tray.SetStatusLine($"内存 {CleanupCore.ReadMemorySnapshot()?.LoadPercent ?? 0}% · 自动清理待启用（{reason}）");
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
            (_context?.Log).Warn("设置保存失败：" + exception.Message);
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

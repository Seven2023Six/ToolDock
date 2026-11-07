using System.Diagnostics;
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
    private DateTime _lastClean = DateTime.MinValue;
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

        context.Tray.AddMenu("立即清理内存", () => CleanNowExternal(confirmed: false));
        context.Tray.SetStatusLine("内存清理就绪");

        _autoTimer = new System.Threading.Timer(_ => AutoTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

        context.Log.Info($"已启动（mask=0x{_mask:X} 自动清理={_autoClean} 阈值={_threshold}% 间隔={_intervalMinutes}分 免UAC={_skipUac} 自动含冻结区={_allowStandbyAuto} 清理确认={_confirmClean}）");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        _autoTimer?.Dispose();
        return Task.CompletedTask;
    }

    // ---- 配置（App 经管道下发）----

    public (ulong Mask, bool AutoClean, uint Threshold, uint IntervalMinutes, bool SkipUac, bool AllowFreezes, bool ConfirmClean) GetCleanState()
        => (_mask, _autoClean, _threshold, _intervalMinutes, _skipUac, _allowStandbyAuto, _confirmClean);

    public void ApplyConfig(ulong mask, bool autoClean, uint threshold, uint intervalMinutes, bool skipUac, bool allowFreezes, bool confirmClean)
    {
        _mask = mask;
        _autoClean = autoClean;
        _threshold = threshold;
        _intervalMinutes = intervalMinutes;
        _skipUac = skipUac;
        _allowStandbyAuto = allowFreezes;
        _confirmClean = confirmClean;

        Persist();
        UpdateStatusLine();

        // 免 UAC 计划任务里内嵌了掩码，配置变化时重建（手动 + 自动两个任务）
        if (_skipUac)
        {
            InstallTask();
        }

        _context?.Log.Info($"配置已更新（mask=0x{_mask:X} 自动={_autoClean} 阈值={_threshold}% 间隔={_intervalMinutes}分 免UAC={_skipUac} 自动含冻结区={_allowStandbyAuto} 清理确认={_confirmClean}）");
    }

    public void SetSkipUac(bool enabled)
    {
        _skipUac = enabled;
        if (enabled)
        {
            InstallTask();
        }
        else
        {
            UninstallTask();
        }
        Persist();
        UpdateStatusLine();
    }

    // ---- 清理入口 ----

    /// <summary>
    /// 托盘菜单 / App 按钮共用的手动清理入口（线程安全）。
    /// confirmed=false 时按"清理前确认"设置弹确认框（App 弹过确认后传 true）。
    /// </summary>
    public void CleanNowExternal(bool confirmed)
    {
        Task.Run(() => CleanInternal("手动", confirmed));
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

            UpdateStatusLine();

            if (!_autoClean)
            {
                return;
            }

            bool by_threshold = _threshold > 0 && snapshot.LoadPercent >= _threshold;
            bool by_interval = _intervalMinutes > 0 && DateTime.UtcNow - _lastClean >= TimeSpan.FromMinutes(_intervalMinutes);

            if (by_threshold || by_interval)
            {
                CleanInternal(by_threshold ? "阈值" : "定时", confirmed: true);
            }
        }
        catch (Exception exception)
        {
            (_context?.Log ?? new FileLogger(Metadata.Id)).Warn("自动清理检查失败：" + exception.Message);
        }
    }

    private void CleanInternal(string source, bool confirmed)
    {
        lock (_gate)
        {
            if (_context is not { } context)
            {
                return;
            }

            // 自动清理永不确认；手动清理按设置弹确认框（Mem Reduct：SOURCE_MANUAL + IsShowReductConfirmation）
            bool is_auto = source is "阈值" or "定时";
            ulong mask = is_auto ? AutoEffectiveMask() : _mask;

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
                ReportResult(context, source, result.FreedBytes, result.Regions, flushed_via_helper: false);
                return;
            }

            // 免 UAC：计划任务以最高权限静默执行（手动走全掩码任务，自动走剔除冻结区任务）
            if (_skipUac)
            {
                string task = is_auto && AutoEffectiveMask() != _mask ? TaskNameAuto : TaskName;
                if (RunScheduledTask(task))
                {
                    _lastClean = DateTime.UtcNow;
                    context.Notify.Show("内存清理", $"清理已执行（{source}）");
                    context.Log.Info($"清理已通过计划任务 {task} 执行（{source}，mask=0x{mask:X}）");
                    return;
                }
                context.Log.Warn("计划任务执行失败，回退到提权助手");
            }

            // 拉起提权助手（UAC）；结果经临时文件回传
            string helper = GetHelperPath();
            if (helper is null)
            {
                context.Notify.Show("内存清理", "找不到提权助手程序");
                return;
            }

            string result_file = Path.Combine(Path.GetTempPath(), $"tooldock_clean_{Guid.NewGuid():N}.txt");
            bool started = context.Elevation.RunElevatedAsync(helper, $"-clean {mask} -out \"{result_file}\"").GetAwaiter().GetResult();

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
            ReportResult(context, source, freed, regions, flushed_via_helper: true);
        }
    }

    /// <summary>气泡 + 托盘状态行 + 日志（失败区域按 Mem Reduct 逐项记日志）。</summary>
    private void ReportResult(IAgentContext context, string source, ulong freed, IReadOnlyList<CleanupCore.RegionResult>? regions, bool flushed_via_helper)
    {
        var failed = regions?.Where(r => (int)r.Status < 0).ToList();
        string summary = $"已释放内存：{FormatBytes(freed)}";
        if (failed is { Count: > 0 })
        {
            summary += $"（{failed.Count} 项失败）";
        }

        context.Notify.Show("内存清理", summary);
        context.Tray.SetStatusLine($"内存 {CleanupCore.ReadMemorySnapshot()?.LoadPercent ?? 0}% · 已释放 {FormatBytes(freed)}");
        context.Log.Info($"清理完成（{source}{(flushed_via_helper ? "，经提权助手" : "")}）：释放 {FormatBytes(freed)}");

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

    private void InstallTask()
    {
        string? helper = GetHelperPath();
        if (helper is null)
        {
            (_context?.Log).Warn("无法安装免 UAC 任务：找不到提权助手");
            return;
        }
        try
        {
            // 手动任务：全掩码；自动任务：策略掩码（默认剔除冻结区）
            CreateTask(TaskName, helper, $"-taskclean {_mask}");
            CreateTask(TaskNameAuto, helper, $"-taskclean {AutoEffectiveMask()}");
            (_context?.Log).Info($"免 UAC 计划任务已安装（手动 mask=0x{_mask:X} 自动 mask=0x{AutoEffectiveMask():X}）");
        }
        catch (Exception exception)
        {
            (_context?.Log).Warn("计划任务安装失败：" + exception.Message);
        }
    }

    private void CreateTask(string task, string helper, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks",
            $"/create /f /tn {task} /tr \"\\\"{helper}\\\" {arguments}\" /sc once /sd 01/01/2000 /st 00:00 /rl highest")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
        });
        process?.WaitForExit(15000);
    }

    private void UninstallTask()
    {
        try
        {
            DeleteTask(TaskName);
            DeleteTask(TaskNameAuto);
        }
        catch
        {
        }
    }

    private void DeleteTask(string task)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks", $"/delete /f /tn {task}")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
            });
            process?.WaitForExit(15000);
        }
        catch
        {
        }
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

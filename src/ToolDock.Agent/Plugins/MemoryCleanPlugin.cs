using System.Diagnostics;
using ToolDock.Abstractions;
using ToolDock.Core;

namespace ToolDock.Agent;

/// <summary>
/// 内存清理插件（移植自 Mem Reduct v3.5.2 清理逻辑，GPL-3.0）。
/// 清理本体在 ToolDock.Core.CleanupCore；本插件负责托盘贡献、自动清理策略与提权调度。
/// 非提权时经 IElevationService 拉起 ElevatedHelper（UAC）；开启"免 UAC"后走计划任务。
/// </summary>
internal sealed class MemoryCleanPlugin : IBackgroundPlugin
{
    private const string HelperRelativePath = "helper/ToolDock.ElevatedHelper.exe";
    private const string TaskName = "ToolDock_Clean";

    public PluginMetadata Metadata { get; } = new("memclean", "内存清理", "0.3.0", "系统");

    private IAgentContext? _context;
    private bool _skipUac;
    private ulong _mask = CleanupCore.MaskDefault;
    private bool _autoClean;
    private uint _threshold = 90;
    private uint _intervalMinutes = 30;
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

        context.Tray.AddMenu("立即清理内存", () => CleanNowExternal());
        context.Tray.SetStatusLine("内存清理就绪");

        _autoTimer = new System.Threading.Timer(_ => AutoTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

        context.Log.Info($"已启动（mask=0x{_mask:X} 自动清理={_autoClean} 阈值={_threshold}% 间隔={_intervalMinutes}分 免UAC={_skipUac}）");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        _autoTimer?.Dispose();
        return Task.CompletedTask;
    }

    // ---- 配置（App 经管道下发）----

    public (ulong Mask, bool AutoClean, uint Threshold, uint IntervalMinutes, bool SkipUac) GetCleanState()
        => (_mask, _autoClean, _threshold, _intervalMinutes, _skipUac);

    public void ApplyConfig(ulong mask, bool autoClean, uint threshold, uint intervalMinutes, bool skipUac)
    {
        _mask = mask;
        _autoClean = autoClean;
        _threshold = threshold;
        _intervalMinutes = intervalMinutes;
        _skipUac = skipUac;

        Persist();
        UpdateStatusLine();

        // 免 UAC 计划任务里内嵌了掩码，配置变化时重建
        if (_skipUac)
        {
            InstallTask();
        }

        _context?.Log.Info($"配置已更新（mask=0x{_mask:X} 自动={_autoClean} 阈值={_threshold}% 间隔={_intervalMinutes}分 免UAC={_skipUac}）");
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

    /// <summary>托盘菜单 / App 按钮 / 自动策略共用的清理入口（线程安全）。</summary>
    public void CleanNowExternal()
    {
        Task.Run(() => CleanInternal("手动"));
    }

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
                CleanInternal(by_threshold ? "阈值" : "定时");
            }
        }
        catch (Exception exception)
        {
            (_context?.Log ?? new FileLogger(Metadata.Id)).Warn("自动清理检查失败：" + exception.Message);
        }
    }

    private void CleanInternal(string source)
    {
        lock (_gate)
        {
            if (_context is not { } context)
            {
                return;
            }

            // 已经提权（Agent 本身以管理员运行）→ 直接清理
            if (context.Elevation.IsElevated)
            {
                var result = CleanupCore.Clean(_mask);
                _lastClean = DateTime.UtcNow;
                context.Notify.Show("内存清理", $"已释放内存：{FormatBytes(result.FreedBytes)}");
                context.Tray.SetStatusLine($"内存 {CleanupCore.ReadMemorySnapshot()?.LoadPercent ?? 0}% · 已释放 {FormatBytes(result.FreedBytes)}");
                context.Log.Info($"清理完成（{source}）：释放 {FormatBytes(result.FreedBytes)}");
                return;
            }

            // 免 UAC：计划任务以最高权限静默执行
            if (_skipUac)
            {
                if (RunScheduledTask())
                {
                    _lastClean = DateTime.UtcNow;
                    context.Notify.Show("内存清理", $"清理已执行（{source}）");
                    context.Log.Info($"清理已通过计划任务执行（{source}）");
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
            bool started = context.Elevation.RunElevatedAsync(helper, $"-clean {_mask} -out \"{result_file}\"").GetAwaiter().GetResult();

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
            try
            {
                foreach (var line in File.ReadAllLines(result_file))
                {
                    if (line.StartsWith("freed=", StringComparison.Ordinal) &&
                        ulong.TryParse(line[6..], out var v))
                    {
                        freed = v;
                    }
                }
                File.Delete(result_file);
            }
            catch
            {
            }

            _lastClean = DateTime.UtcNow;
            context.Notify.Show("内存清理", $"已释放内存：{FormatBytes(freed)}");
            context.Tray.SetStatusLine($"内存 {CleanupCore.ReadMemorySnapshot()?.LoadPercent ?? 0}% · 已释放 {FormatBytes(freed)}");
            context.Log.Info($"清理完成（{source}）：释放 {FormatBytes(freed)}");
        }
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

    private bool RunScheduledTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks", $"/run /tn {TaskName}")
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
            using var process = Process.Start(new ProcessStartInfo("schtasks",
                $"/create /f /tn {TaskName} /tr \"\\\"{helper}\\\" -taskclean {_mask}\" /sc once /sd 01/01/2000 /st 00:00 /rl highest")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
            });
            process?.WaitForExit(15000);
            (_context?.Log).Info("免 UAC 计划任务已安装");
        }
        catch (Exception exception)
        {
            (_context?.Log).Warn("计划任务安装失败：" + exception.Message);
        }
    }

    private void UninstallTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks", $"/delete /f /tn {TaskName}")
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
}

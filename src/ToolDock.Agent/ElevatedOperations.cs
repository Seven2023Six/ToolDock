using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ToolDock.Core;

namespace ToolDock.Agent;

/// <summary>
/// 提权动作：以管理员身份**再跑一遍本进程**时执行（计划任务安装/卸载、静默清理）。
/// 不再依赖独立的 ToolDock.ElevatedHelper.exe —— 未签名的独立小程序申请最高权限，
/// 很容易被安全软件（卡巴斯基等）拦截甚至锁住文件；主程序已由用户信任，触发面更小。
/// 这些模式必须在单实例检查之前处理：常驻 Agent 占着互斥量，提权副本不能因此退出。
/// </summary>
internal static class ElevatedOperations
{
    public const string TaskName = "ToolDock_Clean";
    public const string TaskNameAuto = "ToolDock_CleanAuto";
    public const string ManualArg = "--taskclean-manual";
    public const string AutoArg = "--taskclean-auto";

    /// <summary>创建手动/自动两个最高权限任务（任务命令行指向本程序，掩码运行时读取）。</summary>
    public static int InstallTasks(string[] args)
    {
        string? outFile = GetArg(args, "-out");
        string self = Environment.ProcessPath ?? "";
        string err1 = CreateTask(TaskName, self, ManualArg, out bool ok1);
        string err2 = CreateTask(TaskNameAuto, self, AutoArg, out bool ok2);
        WriteInstallResult(outFile, ok1 && ok2,
            ok1 && ok2 ? "" : $"任务创建失败（手动={ok1} 自动={ok2}）{err1}{err2}");
        LogFile.Write($"[elevated] 计划任务安装：手动={ok1} 自动={ok2}{err1}{err2}");
        return ok1 && ok2 ? 0 : 1;
    }

    public static int UninstallTasks(string[] args)
    {
        string? outFile = GetArg(args, "-out");
        bool ok1 = DeleteTask(TaskName);
        bool ok2 = DeleteTask(TaskNameAuto);
        WriteInstallResult(outFile, ok1 && ok2, "");
        LogFile.Write($"[elevated] 计划任务删除：手动={ok1} 自动={ok2}");
        return ok1 && ok2 ? 0 : 1;
    }

    /// <summary>计划任务触发的静默清理：掩码从插件设置读取，改配置无需重建任务。</summary>
    public static int CleanFromPlugin(bool auto)
    {
        try
        {
            ulong mask = auto ? ReadPluginAutoMask() : ReadPluginMask();
            var result = CleanupCore.Clean(mask);
            LogFile.Write($"[elevated] 计划任务清理（{(auto ? "自动" : "手动")}）mask=0x{mask:X} 释放 {result.FreedBytes / 1048576.0:0} MB");
            return 0;
        }
        catch (Exception exception)
        {
            LogFile.Write($"[elevated] 计划任务清理失败：{exception.Message}");
            return 1;
        }
    }

    /// <summary>提权清理：结果写入临时文件，供常驻 Agent 读回。</summary>
    public static int CleanWithMask(ulong mask, string? outFile)
    {
        var result = CleanupCore.Clean(mask);
        WriteResult(outFile, result);
        return 0;
    }

    public static string? GetArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// schtasks 命令行。触发器用「十年后一次」：永不自动触发、永不过期、/run 随时可调；
    /// /sd 只认固定补零的 yyyy/MM/dd（zh-CN 的 yyyy/M/d 会被拒绝）。
    /// </summary>
    private static string CreateTask(string task, string self, string arguments, out bool ok)
    {
        string date = DateTime.Now.AddYears(10).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        string args = $"/create /f /tn {task} /tr \"\\\"{self}\\\" {arguments}\" /sc once /sd {date} /st 00:00 /rl highest";
        string stderr = Run("schtasks", args, out ok);
        return ok ? "" : stderr;
    }

    private static bool DeleteTask(string task)
    {
        Run("schtasks", $"/delete /f /tn {task}", out bool ok);
        return ok;
    }

    private static string PluginDataPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolDock", "plugindata", "memclean.json");

    private static ulong ReadPluginMask()
        => ReadPluginData()?.Mask ?? CleanupCore.MaskDefault;

    private static ulong ReadPluginAutoMask()
    {
        const ulong freezes = CleanupCore.RegionStandbyList | CleanupCore.RegionModifiedList;
        var data = ReadPluginData();
        if (data is null)
        {
            // 设置不可读时按自动语义保守处理：不碰冻结区
            return CleanupCore.MaskDefault & ~freezes;
        }
        return data.AllowFreezes ? data.Mask : (data.Mask & ~freezes);
    }

    private static MemCleanPluginData? ReadPluginData()
    {
        try
        {
            if (!File.Exists(PluginDataPath))
            {
                return null;
            }
            return JsonSerializer.Deserialize(File.ReadAllText(PluginDataPath), MemCleanDataContext.Default.MemCleanPluginData);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteResult(string? path, CleanupCore.CleanupResult result)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            var lines = new List<string>
            {
                $"freed={result.FreedBytes}",
                $"priv={result.PrivilegeDiag}",
                $"flush={result.FlushDiag}",
            };
            foreach (var region in result.Regions)
            {
                lines.Add($"region={region.Name}|status=0x{region.Status:X8}");
            }
            File.WriteAllLines(path, lines);
        }
        catch
        {
            // 结果文件写不写都不影响清理本身
        }
    }

    private static void WriteInstallResult(string? path, bool ok, string error)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            File.WriteAllText(path, ok ? "ok=1" : "error=" + error);
        }
        catch
        {
        }
    }

    /// <summary>执行外部命令并返回真实退出码；先带超时等待，再收 stderr（避免子进程挂住时永久阻塞）。</summary>
    private static string Run(string file, string arguments, out bool ok)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                ok = false;
                return "（进程启动失败）";
            }
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                ok = false;
                return "schtasks: 执行超时（30 秒）";
            }
            string stderr = stderrTask.GetAwaiter().GetResult().Trim();
            ok = process.ExitCode == 0;
            return ok ? "" : "schtasks: " + (stderr.Length > 0 ? stderr.Split('\n')[0].Trim() : $"exit={process.ExitCode}");
        }
        catch (Exception exception)
        {
            ok = false;
            return exception.Message;
        }
    }
}

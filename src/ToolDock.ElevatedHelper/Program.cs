using System.Globalization;
using System.IO;
using System.Text.Json;
using ToolDock.Core;

namespace ToolDock.ElevatedHelper;

/// <summary>
/// 提权助手：由 Agent 经 IElevationService 以管理员身份拉起，执行完即退出。
/// 命令：
///   -clean &lt;mask&gt; [-out &lt;file&gt;]   执行内存清理，结果写入 out 文件（JSON 行）
///   -install-task [-out &lt;file&gt;]    创建免 UAC 计划任务（手动/自动各一个，不带掩码）
///   -uninstall-task [-out &lt;file&gt;]  删除计划任务
///   -taskclean &lt;mask&gt;                计划任务触发的静默清理（兼容旧任务，带掩码数字）
///   -taskclean-manual                 计划任务触发的静默清理（掩码运行时从插件设置读取）
///   -taskclean-auto                   同上，但按策略剔除冻结区（自动清理语义）
/// 注意：创建 RunLevel=Highest 的任务必须提权；schtasks /sd 只认固定补零的 yyyy/MM/dd。
/// </summary>
internal static class Program
{
    private const string TaskName = "ToolDock_Clean";
    private const string TaskNameAuto = "ToolDock_CleanAuto";

    private static int Main(string[] args)
    {
        try
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string out_file = GetArg(args, "-out");
            string mask_str = GetArg(args, "-clean") ?? GetArg(args, "-taskclean");
            ulong mask = mask_str is not null && ulong.TryParse(mask_str, out var m) ? m : CleanupCore.MaskDefault;

            switch (mode)
            {
                case "-clean":
                {
                    var result = CleanupCore.Clean(mask);
                    WriteResult(out_file, result);
                    return 0;
                }

                case "-taskclean":
                {
                    // 旧版任务自带掩码数字（/taskclean <mask>），保留兼容
                    CleanupCore.Clean(mask);
                    return 0;
                }

                case "-taskclean-manual":
                case "-taskclean-auto":
                {
                    // 免 UAC 任务不带掩码数字：运行时从插件设置文件读取，改掩码/策略无需重建任务
                    ulong effective = mode == "-taskclean-manual"
                        ? ReadPluginMask()
                        : ReadPluginAutoMask();
                    CleanupCore.Clean(effective);
                    return 0;
                }

                case "-install-task":
                {
                    // 任务不带掩码数字（掩码运行时从插件设置读取），改配置无需重建任务
                    string self = Environment.ProcessPath ?? "";
                    string err1 = Run("schtasks", TaskAction(TaskName, self, "-taskclean-manual"), out bool ok1);
                    string err2 = Run("schtasks", TaskAction(TaskNameAuto, self, "-taskclean-auto"), out bool ok2);
                    WriteInstallResult(out_file, ok1 && ok2,
                        ok1 && ok2 ? "" : $"任务创建失败（手动={ok1} 自动={ok2}）{err1}{err2}");
                    return ok1 && ok2 ? 0 : 1;
                }

                case "-uninstall-task":
                {
                    _ = Run("schtasks", $"/delete /f /tn {TaskName}", out bool ok1);
                    _ = Run("schtasks", $"/delete /f /tn {TaskNameAuto}", out bool ok2);
                    WriteInstallResult(out_file, ok1 && ok2, "");
                    return ok1 && ok2 ? 0 : 1;
                }
            }

            return 1;
        }
        catch (Exception exception)
        {
            try
            {
                string out_file = GetArg(args, "-out");
                if (out_file is not null)
                {
                    File.WriteAllText(out_file, "error=" + exception.Message);
                }
            }
            catch
            {
            }
            return 1;
        }
    }

    /// <summary>
    /// 计划任务命令行。触发器用「十年后一次」：永不自动触发、永不过期、/run 随时可调。
    /// 注意：schtasks /sd 不遵循系统短日期格式（zh-CN 的 yyyy/M/d 会被拒），只认固定补零的 yyyy/MM/dd。
    /// </summary>
    private static string TaskAction(string task, string self, string arguments)
    {
        string date = DateTime.Now.AddYears(10).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        return $"/create /f /tn {task} /tr \"\\\"{self}\\\" {arguments}\" /sc once /sd {date} /st 00:00 /rl highest";
    }

    private static string PluginDataPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolDock", "plugindata", "memclean.json");

    /// <summary>读取插件设置的完整掩码（缺文件/损坏时回退默认 0xE7）。</summary>
    private static ulong ReadPluginMask()
        => ReadPluginData()?.Mask ?? CleanupCore.MaskDefault;

    /// <summary>读取自动清理生效掩码：默认剔除冻结区（Mem Reduct：SOURCE_AUTO 排除备用/已修改列表）。</summary>
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

    private static string? GetArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
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

    /// <summary>执行外部命令并返回真实退出码（0 = 成功）；失败时带回 stderr 首行便于诊断。</summary>
    private static string Run(string file, string arguments, out bool ok)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                ok = false;
                return "（进程启动失败）";
            }
            // 先等待带超时，再收 stderr：直接 ReadToEnd 会在子进程挂住时永久阻塞
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

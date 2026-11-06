using System.IO;
using System.Text.Json;
using ToolDock.Core;

namespace ToolDock.ElevatedHelper;

/// <summary>
/// 提权助手：由 Agent 经 IElevationService 以管理员身份拉起，执行完即退出。
/// 命令：
///   -clean &lt;mask&gt; [-out &lt;file&gt;]   执行内存清理，结果写入 out 文件（JSON 行）
///   -install-task &lt;mask&gt;           创建免 UAC 计划任务（内嵌当前掩码）
///   -uninstall-task                删除计划任务
///   -taskclean &lt;mask&gt;              计划任务触发的静默清理（无输出）
/// </summary>
internal static class Program
{
    private const string TaskName = "ToolDock_Clean";

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
                    CleanupCore.Clean(mask);
                    return 0;
                }

                case "-install-task":
                {
                    string self = Environment.ProcessPath ?? "";
                    Run("schtasks", $"/create /f /tn {TaskName} /tr \"\\\"{self}\\\" -taskclean {mask}\" /sc once /sd 01/01/2000 /st 00:00 /rl highest");
                    return 0;
                }

                case "-uninstall-task":
                {
                    Run("schtasks", $"/delete /f /tn {TaskName}");
                    return 0;
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

    private static void Run(string file, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file, arguments)
        {
            UseShellExecute = true,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(30000);
    }
}

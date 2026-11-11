using System.IO;
using System.Text.Json;
using ToolDock.Core;

namespace ToolDock.Agent;

/// <summary>
/// 提权动作：以管理员身份**再跑一遍本进程**时执行（常驻清理助手、静默清理）。
/// 不再依赖独立的 ToolDock.ElevatedHelper.exe —— 未签名的独立小程序申请最高权限，
/// 很容易被安全软件（卡巴斯基等）拦截甚至锁住文件；主程序已由用户信任，触发面更小。
/// 这些模式必须在单实例检查之前处理：常驻 Agent 占着互斥量，提权副本不能因此退出。
/// </summary>
internal static class ElevatedOperations
{
    /// <summary>旧版计划任务的入口：仍保留，用于清理历史任务残留触发的调用。</summary>
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
}

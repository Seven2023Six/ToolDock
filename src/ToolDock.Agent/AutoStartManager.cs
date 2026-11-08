using Microsoft.Win32;

namespace ToolDock.Agent;

/// <summary>
/// PowerToys 式开机自启：写 HKCU 当前用户启动项，指向 Agent 本体（静默、无窗口）。
/// 注册表的唯一同步者是 Agent：启动时按设置对齐一次，之后经管道命令即时增删。
/// </summary>
internal static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>任务管理器「启动应用」的启用/禁用标记；0x03 = 禁用，0x02 = 启用。</summary>
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string ValueName = "ToolDock";

    /// <summary>0x02 + 8 字节时间戳（全 0 表示不在任务管理器里显示禁用记录）。</summary>
    private static readonly byte[] ApprovedEnabled =
    {
        0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };

    /// <param name="clearStartupApproval">
    /// 用户在应用里主动开启时传 true：清掉任务管理器遗留的「已禁用」标记，
    /// 否则 Run 键写对了，系统登录时仍会跳过。
    /// </param>
    public static void Apply(bool enabled, string agentExePath, bool clearStartupApproval = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
            {
                LogFile.Write("[autostart] 无法打开注册表启动项");
                return;
            }
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{agentExePath}\"");
                if (clearStartupApproval)
                {
                    MarkApproved();
                }
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName);
                }
                ClearApproval();
            }
        }
        catch (Exception exception)
        {
            LogFile.Write($"[autostart] 注册表更新失败：{exception.Message}");
        }
    }

    /// <summary>把「启动应用」状态改为启用（否则任务管理器里禁用过一次就永久不启动）。</summary>
    private static void MarkApproved()
    {
        try
        {
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            approved?.SetValue(ValueName, ApprovedEnabled, RegistryValueKind.Binary);
            LogFile.Write("[autostart] 已清除任务管理器的禁用标记");
        }
        catch (Exception exception)
        {
            LogFile.Write($"[autostart] 清除禁用标记失败：{exception.Message}");
        }
    }

    private static void ClearApproval()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception exception)
        {
            LogFile.Write($"[autostart] 清理启动标记失败：{exception.Message}");
        }
    }
}

using Microsoft.Win32;

namespace ToolDock.Agent;

/// <summary>
/// PowerToys 式开机自启：写 HKCU 当前用户启动项，指向 Agent 本体（静默、无窗口）。
/// 注册表的唯一同步者是 Agent：启动时按设置对齐一次，之后经管道命令即时增删。
/// </summary>
internal static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ToolDock";

    public static void Apply(bool enabled, string agentExePath)
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
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName);
            }
        }
        catch (Exception exception)
        {
            LogFile.Write($"[autostart] 注册表更新失败：{exception.Message}");
        }
    }
}

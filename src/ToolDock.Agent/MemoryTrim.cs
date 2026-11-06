using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToolDock.Agent;

/// <summary>
/// 主动回收工作集：让 Windows 把暂不使用的页换出物理内存。
/// 对常驻托盘程序是标准做法——被换出的页在下次访问时自动调回，功能无损，
/// 只是任务管理器里的"内存"占用数字会显著下降。
/// </summary>
internal static class MemoryTrim
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void TrimCurrent()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
        catch
        {
            // 回收失败无关紧要
        }
    }
}

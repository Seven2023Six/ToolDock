using System.Runtime.InteropServices;

namespace ToolDock.App.Services;

/// <summary>物理内存快照：首页与内存清理页共用的读取助手（GlobalMemoryStatusEx）。</summary>
internal static class SystemMemory
{
    public sealed record Snapshot(uint LoadPercent, double UsedGiB, double FreeGiB, double TotalGiB);

    public static Snapshot? Read()
    {
        var status = new NativeMemoryStatus();
        if (!GlobalMemoryStatusEx(status))
        {
            return null;
        }
        return new Snapshot(
            status.DwMemoryLoad,
            (status.UllTotalPhys - status.UllAvailPhys) / 1073741824.0,
            status.UllAvailPhys / 1073741824.0,
            status.UllTotalPhys / 1073741824.0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private class NativeMemoryStatus
    {
        public uint DwLength = 64;
        public uint DwMemoryLoad;
        public ulong UllTotalPhys;
        public ulong UllAvailPhys;
        public ulong UllTotalPageFile;
        public ulong UllAvailPageFile;
        public ulong UllTotalVirtual;
        public ulong UllAvailVirtual;
        public ulong UllAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] NativeMemoryStatus lpBuffer);
}

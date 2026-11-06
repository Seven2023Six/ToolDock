using System.Runtime.InteropServices;

namespace ToolDock.Core;

/// <summary>
/// 内存清理核心（移植自 Mem Reduct v3.5.2，GPL-3.0）。
/// 供提权助手进程调用；调用方须以管理员身份运行。
/// 区域掩码与 Mem Reduct 保持一致，便于互通。
/// </summary>
public static class CleanupCore
{
    // 区域掩码（与 Mem Reduct main.h 一致）
    public const ulong RegionWorkingSet = 0x01;
    public const ulong RegionSystemFileCache = 0x02;
    public const ulong RegionStandbyPriority0 = 0x04;
    public const ulong RegionStandbyList = 0x08;
    public const ulong RegionModifiedList = 0x10;
    public const ulong RegionCombineMemoryLists = 0x20;
    public const ulong RegionRegistryCache = 0x40;
    public const ulong RegionModifiedFileCache = 0x80;
    public const ulong MaskDefault =
        RegionWorkingSet | RegionSystemFileCache | RegionStandbyPriority0 |
        RegionStandbyList | RegionModifiedList | RegionCombineMemoryLists |
        RegionRegistryCache | RegionModifiedFileCache;

    // NtSetSystemInformation 信息类
    private const int SystemMemoryListInformation = 80;
    private const int SystemFileCacheInformationEx = 81;
    private const int SystemCombinePhysicalMemoryInformation = 130;
    private const int SystemRegistryReconciliationInformation = 155;

    // SYSTEM_MEMORY_LIST_COMMAND
    private const int CmdEmptyWorkingSets = 4;
    private const int CmdFlushModifiedList = 5;
    private const int CmdPurgeStandbyList = 6;
    private const int CmdPurgeLowPriorityStandbyList = 7;

    public sealed record RegionResult(string Name, uint Status);
    public sealed record CleanupResult(ulong BeforeBytes, ulong AfterBytes, IReadOnlyList<RegionResult> Regions)
    {
        public ulong FreedBytes => BeforeBytes > AfterBytes ? BeforeBytes - AfterBytes : 0;
    }

    public static bool IsElevated()
    {
        try
        {
            return NativeMethods.IsUserAnAdmin();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用清理所需的令牌特权。</summary>
    public static void EnablePrivileges()
    {
        EnablePrivilege("SeProfileSingleProcessPrivilege");  // 内存列表命令
        EnablePrivilege("SeIncreaseQuotaPrivilege");          // 系统文件缓存
    }

    /// <summary>执行清理。返回前后内存差异与各区域状态。</summary>
    public static CleanupResult Clean(ulong mask)
    {
        EnablePrivileges();

        var before = ReadMemorySnapshot();
        var regions = new List<RegionResult>();

        // 工作集
        if ((mask & RegionWorkingSet) != 0)
        {
            var st = NtSetSystemMemoryListCommand(CmdEmptyWorkingSets);
            regions.Add(new RegionResult("工作集", st));
        }

        // 系统文件缓存
        if ((mask & RegionSystemFileCache) != 0)
        {
            var sfci = new SystemFilecacheInformation
            {
                MinimumWorkingSet = ulong.MaxValue,
                MaximumWorkingSet = ulong.MaxValue,
            };
            var st = NativeMethods.NtSetSystemInformation(SystemFileCacheInformationEx, ref sfci, (uint)Marshal.SizeOf<SystemFilecacheInformation>());
            regions.Add(new RegionResult("系统文件缓存", st));
        }

        // 已修改页面列表
        if ((mask & RegionModifiedList) != 0)
        {
            var st = NtSetSystemMemoryListCommand(CmdFlushModifiedList);
            regions.Add(new RegionResult("已修改页面列表", st));
        }

        // 备用列表
        if ((mask & RegionStandbyList) != 0)
        {
            var st = NtSetSystemMemoryListCommand(CmdPurgeStandbyList);
            regions.Add(new RegionResult("备用列表", st));
        }

        // 备用列表（无优先级）
        if ((mask & RegionStandbyPriority0) != 0)
        {
            var st = NtSetSystemMemoryListCommand(CmdPurgeLowPriorityStandbyList);
            regions.Add(new RegionResult("备用列表（无优先级）", st));
        }

        // 已修改文件缓存：刷新各卷缓存（挂载管理器 → 逐卷 FlushFileBuffers）
        if ((mask & RegionModifiedFileCache) != 0)
        {
            var st = FlushVolumeCaches();
            regions.Add(new RegionResult("已修改文件缓存", st));
        }

        // 注册表缓存（Win8.1+）
        if ((mask & RegionRegistryCache) != 0)
        {
            var st = NativeMethods.NtSetSystemInformation(SystemRegistryReconciliationInformation, IntPtr.Zero, 0);
            regions.Add(new RegionResult("注册表缓存", st));
        }

        // 合并内存列表（Win10+）
        if ((mask & RegionCombineMemoryLists) != 0 && Environment.OSVersion.Version.Build >= 10240)
        {
            var combine = new MemoryCombineInformationEx { Handle = IntPtr.Zero };
            var st = NativeMethods.NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, ref combine, (uint)Marshal.SizeOf<MemoryCombineInformationEx>());
            regions.Add(new RegionResult("合并内存列表", st));
        }

        var after = ReadMemorySnapshot();
        return new CleanupResult(before.UsedBytes, after.UsedBytes, regions);
    }

    // ---- 卷缓存刷新（移植自 Mem Reduct _app_flushvolumecache）----

    private static uint FlushVolumeCaches()
    {
        try
        {
            var mountmgr = NativeMethods.CreateFileW(
                "\\\\.\\MountPointManager",
                0x80000000, // GENERIC_READ
                FileShare.ReadWrite,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);

            if (mountmgr.IsInvalid)
                return 0xC0000005u; // 无法打开挂载管理器

            try
            {
                if (!QueryMountPoints(mountmgr, out var buffer, out var size))
                    return 0xC0000005u;

                uint count = 0;
                if (size >= 12)
                    count = BitConverter.ToUInt32(buffer, 4);

                uint status = 0;
                int entryOffset = 12;
                for (uint i = 0; i < count && entryOffset + 24 <= size; i++, entryOffset += 24)
                {
                    uint nameOffset = BitConverter.ToUInt32(buffer, entryOffset);
                    ushort nameLength = BitConverter.ToUInt16(buffer, entryOffset + 4);

                    if (nameOffset + nameLength > (uint)size || nameLength == 0)
                        continue;

                    string volume = "\\\\.\\" + System.Text.Encoding.Unicode.GetString(buffer, (int)nameOffset, nameLength);

                    status |= FlushVolume(volume);
                }
                return status;
            }
            finally
            {
                // buffer 是托管 byte[]，交给 GC
            }
        }
        catch
        {
            return 0xC0000005u;
        }
    }

    private static bool QueryMountPoints(Microsoft.Win32.SafeHandles.SafeFileHandle device, out byte[] buffer, out int size)
    {
        const uint ioctlQueryPoints = 0x006D0008;
        int alloc = 0x10000;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            buffer = new byte[alloc];
            if (NativeMethods.DeviceIoControl(
                    device, ioctlQueryPoints,
                    IntPtr.Zero, 0,
                    buffer, alloc,
                    out int returned, IntPtr.Zero))
            {
                size = returned;
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            if (err != 122 /*ERROR_INSUFFICIENT_BUFFER*/ && err != 234 /*ERROR_MORE_DATA*/)
            {
                size = 0;
                return false;
            }
            alloc *= 2;
        }

        buffer = Array.Empty<byte>();
        size = 0;
        return false;
    }

    private static uint FlushVolume(string volumePath)
    {
        var hv = NativeMethods.CreateFileW(
            volumePath,
            0x40000000, // GENERIC_WRITE
            FileShare.ReadWrite,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);

        if (hv.IsInvalid)
            return 0xC0000005u;

        try
        {
            return NativeMethods.FlushFileBuffers(hv) ? 0u : 0xC0000005u;
        }
        finally
        {
            hv.Dispose();
        }
    }

    // ---- 内存快照 ----

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

    public sealed record MemorySnapshot(uint LoadPercent, ulong UsedBytes, ulong FreeBytes, ulong TotalBytes, double UsedGiB, double FreeGiB, double TotalGiB);

    public static MemorySnapshot? ReadMemorySnapshot()
    {
        var st = new NativeMemoryStatus();
        if (!GlobalMemoryStatusEx(st))
            return null;
        return new MemorySnapshot(
            st.DwMemoryLoad,
            st.UllTotalPhys - st.UllAvailPhys,
            st.UllAvailPhys,
            st.UllTotalPhys,
            (st.UllTotalPhys - st.UllAvailPhys) / 1073741824.0,
            st.UllAvailPhys / 1073741824.0,
            st.UllTotalPhys / 1073741824.0);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] NativeMemoryStatus lpBuffer);

    // ---- NT 调用 ----

    private static uint NtSetSystemMemoryListCommand(int command)
    {
        return NativeMethods.NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemFilecacheInformation
    {
        public ulong CurrentSize;
        public ulong PeakSize;
        public ulong MinimumWorkingSet;
        public ulong MaximumWorkingSet;
        public ulong CurrentSizeIncludingTransitionInPages;
        public ulong PeakSizeIncludingTransitionInPages;
        public uint TransitionRePurposeCount;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryCombineInformationEx
    {
        public IntPtr Handle;
        public ulong PagesCombined;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LuidAndAttributes
    {
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    private static void EnablePrivilege(string name)
    {
        try
        {
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 0x0028 /*TOKEN_ADJUST_PRIVILEGES|TOKEN_QUERY*/, out var token))
                return;

            try
            {
                var tp = new TokenPrivileges { PrivilegeCount = 1 };
                if (!NativeMethods.LookupPrivilegeValueW(null, name, ref tp.Privileges.Luid))
                    return;

                tp.Privileges.Attributes = 0x00000002; // SE_PRIVILEGE_ENABLED
                NativeMethods.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        catch
        {
            // 特权启用失败会在 NtSetSystemInformation 里以状态码呈现
        }
    }
}

/// <summary>ntdll/advapi32/kernel32 互操作。</summary>
internal static class NativeMethods
{
    [DllImport("ntdll.dll")]
    public static extern uint NtSetSystemInformation(int infoClass, ref CleanupCore.SystemFilecacheInformation info, uint size);

    [DllImport("ntdll.dll")]
    public static extern uint NtSetSystemInformation(int infoClass, ref int command, uint size);

    [DllImport("ntdll.dll")]
    public static extern uint NtSetSystemInformation(int infoClass, ref CleanupCore.MemoryCombineInformationEx info, uint size);

    [DllImport("ntdll.dll")]
    public static extern uint NtSetSystemInformation(int infoClass, IntPtr buffer, uint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LookupPrivilegeValueW(string? systemName, string name, ref long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(
        IntPtr token,
        bool disableAll,
        ref CleanupCore.TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("shell32.dll")]
    public static extern bool IsUserAnAdmin();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device, uint ioctl,
        IntPtr inBuffer, int inSize,
        byte[] outBuffer, int outSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle file);
}

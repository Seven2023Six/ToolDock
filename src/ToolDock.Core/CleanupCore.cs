using System.Runtime.InteropServices;

namespace ToolDock.Core;

/// <summary>
/// 内存清理核心（移植自 Mem Reduct v3.5.2，GPL-3.0）。
/// 供提权助手进程调用；调用方须以管理员身份运行。
/// 全链路诊断输出（PrivilegeDiag / FlushDiag）用于远程排障。
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
    // 原工具默认：工作集 + 系统文件缓存 + 备用(优先级0) + 注册表 + 合并 + 已修改文件缓存
    public const ulong MaskDefault =
        RegionWorkingSet | RegionSystemFileCache | RegionStandbyPriority0 |
        RegionRegistryCache | RegionCombineMemoryLists | RegionModifiedFileCache;

    // NtSetSystemInformation 信息类（值与 routine ntapi.h 一致）
    private const int SystemMemoryListInformation = 80;
    private const int SystemFileCacheInformationEx = 81;
    private const int SystemCombinePhysicalMemoryInformation = 130;
    private const int SystemRegistryReconciliationInformation = 155;

    // SYSTEM_MEMORY_LIST_COMMAND
    private const int CmdEmptyWorkingSets = 2;
    private const int CmdFlushModifiedList = 3;
    private const int CmdPurgeStandbyList = 4;
    private const int CmdPurgeLowPriorityStandbyList = 5;

    public sealed record RegionResult(string Name, uint Status);
    public sealed record CleanupResult(
        ulong BeforeBytes, ulong AfterBytes,
        IReadOnlyList<RegionResult> Regions,
        string PrivilegeDiag, string FlushDiag)
    {
        public ulong FreedBytes => BeforeBytes > AfterBytes ? BeforeBytes - AfterBytes : 0;
    }

    public sealed record MemorySnapshot(uint LoadPercent, ulong UsedBytes, ulong FreeBytes, ulong TotalBytes, double UsedGiB, double FreeGiB, double TotalGiB);

    public static MemorySnapshot ReadMemorySnapshot()
    {
        var st = new NativeMemoryStatus();
        if (!GlobalMemoryStatusEx(st))
            return new MemorySnapshot(0, 0, 0, 0, 0, 0, 0);
        return new MemorySnapshot(
            st.DwMemoryLoad,
            st.UllTotalPhys - st.UllAvailPhys,
            st.UllAvailPhys,
            st.UllTotalPhys,
            (st.UllTotalPhys - st.UllAvailPhys) / 1073741824.0,
            st.UllAvailPhys / 1073741824.0,
            st.UllTotalPhys / 1073741824.0);
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

    /// <summary>执行清理。返回各区域状态与诊断信息。</summary>
    public static CleanupResult Clean(ulong mask)
    {
        string privilege_diag = EnablePrivileges(out uint priv_status);

        var before = ReadMemorySnapshot();
        var regions = new List<RegionResult>
        {
            new("特权启用", priv_status),
        };

        // 工作集（SeProfileSingleProcessPrivilege）
        if ((mask & RegionWorkingSet) != 0)
        {
            int command = CmdEmptyWorkingSets;
            regions.Add(new RegionResult("工作集", NativeMethods.NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int))));
        }

        // 系统文件缓存（SeIncreaseQuotaPrivilege）
        if ((mask & RegionSystemFileCache) != 0)
        {
            var sfci = new SystemFilecacheInformation
            {
                MinimumWorkingSet = ulong.MaxValue,
                MaximumWorkingSet = ulong.MaxValue,
            };
            regions.Add(new RegionResult("系统文件缓存", NativeMethods.NtSetSystemInformation(SystemFileCacheInformationEx, ref sfci, (uint)Marshal.SizeOf<SystemFilecacheInformation>())));
        }

        // 已修改页面列表
        if ((mask & RegionModifiedList) != 0)
        {
            int command = CmdFlushModifiedList;
            regions.Add(new RegionResult("已修改页面列表", NativeMethods.NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int))));
        }

        // 备用列表
        if ((mask & RegionStandbyList) != 0)
        {
            int command = CmdPurgeStandbyList;
            regions.Add(new RegionResult("备用列表", NativeMethods.NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int))));
        }

        // 备用列表（无优先级）
        if ((mask & RegionStandbyPriority0) != 0)
        {
            int command = CmdPurgeLowPriorityStandbyList;
            regions.Add(new RegionResult("备用列表（无优先级）", NativeMethods.NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int))));
        }

        // 已修改文件缓存：刷新各卷缓存
        string flush_diag = "";
        if ((mask & RegionModifiedFileCache) != 0)
        {
            regions.Add(new RegionResult("已修改文件缓存", FlushVolumeCaches(out flush_diag)));
        }

        // 注册表缓存（Win8.1+）
        if ((mask & RegionRegistryCache) != 0)
        {
            regions.Add(new RegionResult("注册表缓存", NativeMethods.NtSetSystemInformation(SystemRegistryReconciliationInformation, IntPtr.Zero, 0)));
        }

        // 合并内存列表（Win10+）
        if ((mask & RegionCombineMemoryLists) != 0 && Environment.OSVersion.Version.Build >= 10240)
        {
            var combine = new MemoryCombineInformationEx();
            regions.Add(new RegionResult("合并内存列表", NativeMethods.NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, ref combine, (uint)Marshal.SizeOf<MemoryCombineInformationEx>())));
        }

        var after = ReadMemorySnapshot();
        return new CleanupResult(before.UsedBytes, after.UsedBytes, regions, privilege_diag, flush_diag);
    }

    // ---- 特权启用（advapi32 标准做法，带全量诊断）----

    public static string EnablePrivileges(out uint status)
    {
        var diag = new List<string>();
        uint final = 0;

        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 0x0028, out var token))
        {
            int err = Marshal.GetLastWin32Error();
            status = 0xC0000005u;
            return "OpenProcessToken 失败 err=" + err;
        }

        try
        {
            foreach (var name in new[] { "SeProfileSingleProcessPrivilege", "SeIncreaseQuotaPrivilege" })
            {
                if (!NativeMethods.LookupPrivilegeValueW(null, name, out var luid))
                {
                    int err = Marshal.GetLastWin32Error();
                    diag.Add($"{name} lookup失败 err={err}");
                    final = 0xC0000061u;
                    continue;
                }

                var tp = new NativeMethods.TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = 0x00000002, // SE_PRIVILEGE_ENABLED
                };

                if (NativeMethods.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    diag.Add($"{name} ok(err={err})");
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    diag.Add($"{name} adjust失败 err={err}");
                    final = 0xC0000061u;
                }
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }

        status = final;
        return string.Join("; ", diag);
    }

    // ---- 卷缓存刷新（FindFirstVolume 枚举，无需 WDK）----

    private static uint FlushVolumeCaches(out string diag)
    {
        var lines = new List<string>();
        uint status = 0;
        int flushed = 0;

        try
        {
            var sb = new System.Text.StringBuilder(1024);
            IntPtr find = NativeMethods.FindFirstVolumeW(sb, 1024);

            if (find == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                diag = "FindFirstVolume err=" + err;
                return 0xC0010001u;
            }

            try
            {
                do
                {
                    string volume = sb.ToString(); // \\?\Volume{guid}\
                    string open_path = volume.EndsWith("\\") ? volume[..^1] : volume;

                    uint st = FlushVolume(open_path);
                    lines.Add($"{open_path} st=0x{st:X}");
                    if (st == 0) flushed++;
                }
                while (NativeMethods.FindNextVolumeW(find, sb, 1024));
            }
            finally
            {
                NativeMethods.FindVolumeClose(find);
            }

            lines.Add($"flushed={flushed}");
        }
        catch (Exception exception)
        {
            lines.Add("exception " + exception.GetType().Name + ": " + exception.Message);
            status = 0xC0010005u;
        }

        diag = string.Join("; ", lines);
        return status;
    }

    private static uint FlushVolume(string volume_path)
    {
        var hv = NativeMethods.CreateFileW(
            volume_path,
            0x40000000, // GENERIC_WRITE
            FileShare.ReadWrite,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);

        if (hv.IsInvalid)
            return 0xC0010003u;

        try
        {
            return NativeMethods.FlushFileBuffers(hv) ? 0u : 0xC0010004u;
        }
        finally
        {
            hv.Dispose();
        }
    }

    // ---- NT / Win32 互操作 ----

    internal static class NativeMethods
    {
        [DllImport("ntdll.dll")]
        public static extern uint NtSetSystemInformation(int info_class, ref CleanupCore.SystemFilecacheInformation info, uint size);

        [DllImport("ntdll.dll")]
        public static extern uint NtSetSystemInformation(int info_class, ref int command, uint size);

        [DllImport("ntdll.dll")]
        public static extern uint NtSetSystemInformation(int info_class, ref CleanupCore.MemoryCombineInformationEx info, uint size);

        [DllImport("ntdll.dll")]
        public static extern uint NtSetSystemInformation(int info_class, IntPtr buffer, uint size);

        [DllImport("ntdll.dll")]
        public static extern int NtOpenProcessToken(IntPtr process, uint desired_access, out IntPtr token);

        [DllImport("ntdll.dll")]
        public static extern int NtAdjustPrivilegesToken(IntPtr token, bool disable_all, IntPtr new_state, uint buffer_length, IntPtr previous_state, IntPtr return_length);

        [DllImport("ntdll.dll")]
        public static extern int NtClose(IntPtr handle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool LookupPrivilegeValueW(string? system_name, string name, out long luid);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public long Luid;
            public uint Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(
            IntPtr token, bool disable_all, ref TokenPrivileges new_state, uint buffer_length, IntPtr previous_state, IntPtr return_length);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr process, uint desired_access, out IntPtr token);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("shell32.dll")]
        public static extern bool IsUserAnAdmin();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string file_name, uint desired_access, FileShare share_mode, IntPtr security_attributes,
            FileMode creation_disposition, uint flags_and_attributes, IntPtr template_file);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            Microsoft.Win32.SafeHandles.SafeFileHandle device, uint ioctl,
            IntPtr in_buffer, int in_size,
            byte[] out_buffer, int out_size,
            out int bytes_returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushFileBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle file);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstVolumeW(System.Text.StringBuilder volume_name, uint name_length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool FindNextVolumeW(IntPtr find, System.Text.StringBuilder volume_name, uint name_length);

        [DllImport("kernel32.dll")]
        public static extern bool FindVolumeClose(IntPtr find);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemFilecacheInformation
    {
        public ulong CurrentSize;
        public ulong PeakSize;
        public uint PageFaultCount;
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

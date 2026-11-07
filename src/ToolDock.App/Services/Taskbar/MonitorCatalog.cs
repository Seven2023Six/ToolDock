using System.Runtime.InteropServices;

namespace ToolDock.App.Services;

/// <summary>
/// 功 能： 枚举本机活动显示器：GDI 名、稳定身份、友好名、工作区与物理矩形。
/// 2026/10/20 15:22:00 xliu
/// 名称与身份取自 QueryDisplayConfig（MONITORINFOEX 的 \\.\DISPLAYn 只是配置序号），见 docs/dev/monitor-identity.md。
/// </summary>
public static class MonitorCatalog
{
    public static IReadOnlyList<MonitorScreen> AllScreens()
    {
        var identities = QueryIdentities();
        var list = new List<MonitorScreen>();
        var callback = new Native.MonitorEnumProc((IntPtr hMonitor, IntPtr _, ref Native.RECT rc, IntPtr __) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfoW(hMonitor, ref info))
            {
                identities.TryGetValue(info.szDevice, out var ident);
                list.Add(new MonitorScreen(
                    info.szDevice,
                    string.IsNullOrEmpty(ident.Id) ? info.szDevice : ident.Id,
                    string.IsNullOrEmpty(ident.Label) ? MonitorSelection.FallbackLabel(info.szDevice) : ident.Label,
                    new PixelRect(rc.Left, rc.Top, rc.Width, rc.Height),
                    new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height),
                    (info.dwFlags & 1) != 0));
            }
            return true;
        });
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// 按设备路径查显示器友好名，**未激活的屏也能查到**（QDC 对它们返回空名字）。
    /// 设置页要显示"那块屏现在没插，但它叫 XX"就靠这个。
    /// </summary>
    public static string? FriendlyNameOf(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return null;

        for (uint i = 0; ; i++)
        {
            var adapter = new Native.DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<Native.DISPLAY_DEVICEW>() };
            if (!Native.EnumDisplayDevicesW(null, i, ref adapter, 0)) break;
            if (string.IsNullOrEmpty(adapter.DeviceName)) continue;

            for (uint j = 0; ; j++)
            {
                var monitor = new Native.DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<Native.DISPLAY_DEVICEW>() };
                if (!Native.EnumDisplayDevicesW(adapter.DeviceName, j, ref monitor, Native.EDD_GET_DEVICE_INTERFACE_NAME)) break;
                if (monitor.DeviceID.Equals(devicePath, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(monitor.DeviceString))
                    return monitor.DeviceString;
            }
        }
        return null;
    }

    /// <summary>GDI 名 → (友好名, 设备路径)。QDC 失败就返回空表，上层退回"显示器 N"文案，不影响贴片本身。</summary>
    private static Dictionary<string, (string Label, string Id)> QueryIdentities()
    {
        var map = new Dictionary<string, (string Label, string Id)>(StringComparer.OrdinalIgnoreCase);
        if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
            return map;

        var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
        if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return map;

        for (int i = 0; i < pathCount; i++)
        {
            var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id,
                },
            };
            if (Native.DisplayConfigGetDeviceInfo(ref source) != 0 || string.IsNullOrEmpty(source.viewGdiDeviceName))
                continue;

            var target = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = paths[i].targetInfo.adapterId,
                    id = paths[i].targetInfo.id,
                },
            };
            if (Native.DisplayConfigGetDeviceInfo(ref target) != 0) continue;

            map[source.viewGdiDeviceName] = (target.monitorFriendlyDeviceName ?? "", target.monitorDevicePath ?? "");
        }
        return map;
    }
}

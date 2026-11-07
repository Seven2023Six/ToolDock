using VigilWatt.Core.Devices;
using VigilWatt.Core.Settings;

namespace ToolDock.App.Services;

/// <summary>
/// 贴片层的宿主访问点：移植自 VigilWatt 的 App.Services.Manager/Settings，
/// 这里直接指向 ToolDock 的 BatteryMonitor，避免贴片代码感知宿主差异。
/// </summary>
internal static class PillHost
{
    public static DeviceManager? Manager => BatteryMonitor.Instance.Manager;
    public static AppSettings? Settings => BatteryMonitor.Instance.Settings;

    public static bool Ready => Manager is not null && Settings is not null;
}

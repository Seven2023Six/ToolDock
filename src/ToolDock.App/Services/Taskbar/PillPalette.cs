using Gdi = System.Drawing;

namespace ToolDock.App.Services;

/// <summary>任务栏贴片用的调色板（对齐 VigilWatt UiPalette 的 RGB 值，但直接产出 GDI+ 颜色）。</summary>
internal static class PillPalette
{
    public static readonly Gdi.Color Charging = Gdi.Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF);
    public static readonly Gdi.Color Low = Gdi.Color.FromArgb(0xFF, 0xE2, 0x44, 0x5C);
    public static readonly Gdi.Color Mid = Gdi.Color.FromArgb(0xFF, 0xFA, 0xAA, 0x17);
    public static readonly Gdi.Color Good = Gdi.Color.FromArgb(0xFF, 0x34, 0xC7, 0x59);
    public static readonly Gdi.Color Offline = Gdi.Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93);

    /// <summary>电量状态 → 状态色（VigilWatt 同款阈值：≤20 低、≤50 中、其余正常、充电优先）。</summary>
    public static Gdi.Color BatteryLevel(int? percent, bool charging)
    {
        if (charging)
        {
            return Charging;
        }
        return percent switch
        {
            null => Offline,
            <= 20 => Low,
            <= 50 => Mid,
            _ => Good,
        };
    }

    public static Gdi.Color ToDrawing(Gdi.Color color) => color;
}

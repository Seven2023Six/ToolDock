using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ToolDock.App.Services;

/// <summary>
/// DWM 原生窗口材质：Mica + 深色标题栏。
/// 对应 VigilWatt 的 ApplyBackdrop：材质失败时调用方回退纯色，窗口照常可用。
/// DWMWA_SYSTEMBACKDROP_TYPE 仅 Win11 22H2+ 生效，老系统返回错误码 → 回退。
/// </summary>
internal static class NativeBackdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int BackdropNone = 1;
    private const int BackdropMica = 2;
    private const int BackdropAcrylic = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int sizeOfValue);

    /// <summary>同步标题栏与 Mica 明暗（dark = 跟随深色主题；false 为浅色）。</summary>
    public static void EnableDarkMode(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // 标题栏配色失败不影响功能
        }
    }

    /// <summary>尝试启用 Mica。返回 false 表示系统不支持，调用方铺纯色背景。</summary>
    public static bool TryApplyMica(Window window) => TrySetBackdrop(window, BackdropMica);

    /// <summary>尝试启用亚克力（Acrylic）。返回 false 表示系统不支持。</summary>
    public static bool TryApplyAcrylic(Window window) => TrySetBackdrop(window, BackdropAcrylic);

    /// <summary>关闭 DWM 材质，窗口回到纯色。</summary>
    public static void SetBackdropNone(Window window) => TrySetBackdrop(window, BackdropNone);

    private static bool TrySetBackdrop(Window window, int backdropType)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int backdrop = backdropType;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }
}

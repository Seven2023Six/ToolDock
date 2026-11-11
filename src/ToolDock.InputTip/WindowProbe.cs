using System.Diagnostics;
using System.Text;

namespace ToolDock.InputTip;

internal sealed class MonitorInfo
{
    public int Index { get; init; }
    public Win32.RECT Bounds { get; init; }
    public Win32.RECT Work { get; init; }
    public double Scale { get; init; } = 1.0;
    public bool Primary { get; init; }
}

internal sealed class ScreenLayout
{
    public List<MonitorInfo> Monitors { get; } = [];

    public MonitorInfo? FromRect(Win32.RECT rect)
    {
        var center = new Win32.POINT { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 };
        return FromPoint(center) ?? FromPoint(new Win32.POINT { X = rect.Left, Y = rect.Top }) ?? Primary;
    }

    public MonitorInfo? FromPoint(Win32.POINT point)
    {
        foreach (var monitor in Monitors)
        {
            if (point.X >= monitor.Bounds.Left && point.X < monitor.Bounds.Right &&
                point.Y >= monitor.Bounds.Top && point.Y < monitor.Bounds.Bottom)
            {
                return monitor;
            }
        }
        return null;
    }

    public MonitorInfo? Primary => Monitors.FirstOrDefault(m => m.Primary) ?? Monitors.FirstOrDefault();

    public MonitorInfo? ByIndex(int index) => Monitors.FirstOrDefault(m => m.Index == index);
}

internal sealed class WindowSnapshot
{
    public IntPtr Hwnd { get; init; }
    public IntPtr FocusedHwnd { get; init; }
    public uint Pid { get; init; }
    public string Process { get; init; } = "";
    public string Title { get; init; } = "";
    public string Class { get; init; } = "";
    public string ControlClass { get; init; } = "";
    public bool Maximized { get; init; }
    public bool Fullscreen { get; init; }
    public bool Normal => !Maximized && !Fullscreen;
    public Win32.RECT Rect { get; init; }
    public bool Valid => Hwnd != IntPtr.Zero;

    public static readonly WindowSnapshot Empty = new();
}

/// <summary>前台窗口 / 显示器 / DPI 探测。坐标一律为物理像素（引擎线程按 Per-Monitor V2 运行）。</summary>
internal static class WindowProbe
{
    private static ScreenLayout? _cachedScreens;
    private static long _cachedScreensAt;
    private static bool _cachedLocked;
    private static long _cachedLockedAt;
    private static readonly Dictionary<uint, (string Name, long CheckedAt)> ProcessNames = new();

    /// <summary>显示器枚举带 500ms 缓存：轮询每帧都会用到，避免反复 EnumDisplayMonitors。</summary>
    public static ScreenLayout CaptureScreens()
    {
        long now = (long)Win32.GetTickCount64();
        if (_cachedScreens is not null && now - _cachedScreensAt < 500)
        {
            return _cachedScreens;
        }
        _cachedScreens = CaptureScreensCore();
        _cachedScreensAt = now;
        return _cachedScreens;
    }

    private static ScreenLayout CaptureScreensCore()
    {
        var layout = new ScreenLayout();
        var monitors = new List<(IntPtr Handle, Win32.MONITORINFO Info)>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Win32.RECT rect, IntPtr data) =>
        {
            var info = new Win32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfoW(monitor, ref info))
            {
                monitors.Add((monitor, info));
            }
            return true;
        }, IntPtr.Zero);

        // 与 AHK 的 MonitorGet 一致：按屏幕左→右、上→下排序后编号 1..N
        monitors.Sort((a, b) =>
        {
            int result = a.Info.rcMonitor.Left.CompareTo(b.Info.rcMonitor.Left);
            return result != 0 ? result : a.Info.rcMonitor.Top.CompareTo(b.Info.rcMonitor.Top);
        });

        int index = 1;
        foreach (var (handle, info) in monitors)
        {
            double scale = 1.0;
            try
            {
                if (Win32.GetDpiForMonitor(handle, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                {
                    scale = dpiX / 96.0;
                }
            }
            catch
            {
                // Shcore 不可用（Win7）：按 100% 处理
            }
            layout.Monitors.Add(new MonitorInfo
            {
                Index = index++,
                Bounds = info.rcMonitor,
                Work = info.rcWork,
                Scale = scale,
                Primary = (info.dwFlags & 1) != 0,
            });
        }
        return layout;
    }

    public static WindowSnapshot Probe()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return WindowSnapshot.Empty;
        }

        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        string process = GetProcessName(pid);

        var titleBuffer = new StringBuilder(1024);
        Win32.GetWindowTextW(hwnd, titleBuffer, titleBuffer.Capacity);
        var classBuffer = new StringBuilder(256);
        Win32.GetClassNameW(hwnd, classBuffer, classBuffer.Capacity);

        IntPtr focused = GetFocusedWindow(hwnd);
        string controlClass = "";
        if (focused != IntPtr.Zero && focused != hwnd)
        {
            var controlBuffer = new StringBuilder(256);
            Win32.GetClassNameW(focused, controlBuffer, controlBuffer.Capacity);
            controlClass = controlBuffer.ToString();
        }

        Win32.RECT rect = GetPhysicalRect(hwnd);
        bool maximized = Win32.IsZoomed(hwnd);
        bool fullscreen = IsFullscreen(hwnd, rect);

        return new WindowSnapshot
        {
            Hwnd = hwnd,
            FocusedHwnd = focused,
            Pid = pid,
            Process = process,
            Title = titleBuffer.ToString(),
            Class = classBuffer.ToString(),
            ControlClass = controlClass,
            Maximized = maximized,
            Fullscreen = fullscreen,
            Rect = rect,
        };
    }

    /// <summary>前台窗口内真正获得焦点的子窗口（对齐上游 getFocusedHwnd）。</summary>
    public static IntPtr GetFocusedWindow(IntPtr fallback = default)
    {
        IntPtr foreground = fallback != IntPtr.Zero ? fallback : Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        uint thread = Win32.GetWindowThreadProcessId(foreground, out _);
        var info = new Win32.GUITHREADINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (Win32.GetGUIThreadInfo(thread, ref info))
        {
            if (info.hwndFocus != IntPtr.Zero)
            {
                return info.hwndFocus;
            }
        }
        return foreground;
    }

    /// <summary>DWM 扩展边框（含阴影裁掉后的真实可视区域），失败回退 GetWindowRect。</summary>
    public static Win32.RECT GetPhysicalRect(IntPtr hwnd)
    {
        try
        {
            if (Win32.DwmGetWindowAttribute(hwnd, 9, out Win32.RECT rect, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) == 0)
            {
                return rect;
            }
        }
        catch
        {
        }
        Win32.GetWindowRect(hwnd, out Win32.RECT fallback);
        return fallback;
    }

    public static bool IsFullscreen(IntPtr hwnd, Win32.RECT? knownRect = null)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        long style = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE);
        if ((style & Win32.WS_CAPTION) != 0)
        {
            return false;
        }
        Win32.RECT rect = knownRect ?? GetPhysicalRect(hwnd);
        var layout = CaptureScreens();
        var monitor = layout.FromRect(rect);
        if (monitor is null)
        {
            return false;
        }
        int screenWidth = monitor.Bounds.Width;
        int screenHeight = monitor.Bounds.Height;
        return rect.Width >= screenWidth * 0.98 && rect.Height >= screenHeight * 0.98;
    }

    public static bool IsLocked()
    {
        long now = (long)Win32.GetTickCount64();
        if (now - _cachedLockedAt < 2000)
        {
            return _cachedLocked;
        }
        _cachedLockedAt = now;
        try
        {
            if (Process.GetProcessesByName("LogonUI").Length > 0)
            {
                _cachedLocked = true;
                return true;
            }
        }
        catch
        {
        }
        IntPtr desktop = Win32.OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero)
        {
            _cachedLocked = true;
            return true;
        }
        Win32.CloseDesktop(desktop);
        _cachedLocked = false;
        return false;
    }

    private static string GetProcessName(uint pid)
    {
        long now = (long)Win32.GetTickCount64();
        if (ProcessNames.TryGetValue(pid, out var cached) && now - cached.CheckedAt < 2000)
        {
            return cached.Name;
        }
        string name = "";
        try
        {
            using var instance = Process.GetProcessById((int)pid);
            name = instance.ProcessName + ".exe";
        }
        catch
        {
            name = "";
        }
        if (ProcessNames.Count > 64)
        {
            ProcessNames.Clear();
        }
        ProcessNames[pid] = (name, now);
        return name;
    }

    /// <summary>Windows 空闲时间（键盘 + 鼠标），毫秒。</summary>
    public static uint IdleMilliseconds()
    {
        var info = new Win32.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.LASTINPUTINFO>() };
        if (!Win32.GetLastInputInfo(ref info))
        {
            return 0;
        }
        return unchecked((uint)Win32.GetTickCount64() - info.dwTime);
    }

    public static double ScaleForPoint(Win32.POINT point, ScreenLayout layout)
        => layout.FromPoint(point)?.Scale ?? layout.Primary?.Scale ?? 1.0;

    public static int ToPhysical(int value, double scale) => (int)Math.Round(value * scale);
}

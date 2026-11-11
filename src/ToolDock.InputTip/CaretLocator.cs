using System.Runtime.InteropServices;

namespace ToolDock.InputTip;

/// <summary>
/// 光标位置捕获（移植自 InputTip var.ahk 的 GetCaretPosEx / Tebayaki 的 GetCaretPosEx）。
/// 支持 GUI / MSAA / ACC 三种通道；UIA / WPF / HOOK 在拿不到时按 GUI → MSAA 回退。
/// </summary>
internal static class CaretLocator
{
    private static readonly Guid IidAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private const uint ObjidCaret = 0xFFFFFFF8;

    // MSAA 是跨进程 COM 调用，遇到无响应的宿主（Chromium 偶发）会长时间阻塞。
    // 用一个带超时的单槽后台调用：超时就放弃本次捕获，不拖住引擎轮询线程。
    private static int _msaaBusy;
    private static IntPtr _lastFailedHwnd;
    private static long _lastFailedAt;

    public static bool TryGet(IntPtr hwnd, string captureChain, out Win32.RECT rect, out string mode)
    {
        rect = default;
        mode = "";
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        string[] modes;
        if (!string.IsNullOrWhiteSpace(captureChain))
        {
            modes = captureChain.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else
        {
            modes = [];
        }

        if (modes.Length == 0)
        {
            if (TryGui(hwnd, out rect))
            {
                mode = "GUI";
                return true;
            }
            string className = GetClassName(hwnd);
            modes = className.StartsWith("Windows.UI.", StringComparison.OrdinalIgnoreCase) ||
                    className.StartsWith("Microsoft.UI.", StringComparison.OrdinalIgnoreCase)
                ? ["UIA", "HOOK", "MSAA"]
                : ["HOOK", "UIA", "MSAA"];
        }

        foreach (string candidate in modes)
        {
            if (TryMode(candidate, hwnd, out rect))
            {
                mode = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool TryMode(string mode, IntPtr hwnd, out Win32.RECT rect)
    {
        rect = default;
        switch (mode.ToUpperInvariant())
        {
            case "GUI":
                return TryGui(hwnd, out rect);
            case "MSAA":
                return TryAccessibleWithTimeout(hwnd, out rect);
            case "ACC":
                return TryAccessibleWithTimeout(Win32.GetForegroundWindow(), out rect);
            case "UIA":
            case "WPF":
                // UIA 通道需要 IUIAutomation 的 vtable 调用；这里先走 MSAA（Chrome/VS Code 等同样可用）
                return TryAccessibleWithTimeout(hwnd, out rect) || TryGui(hwnd, out rect);
            case "HOOK":
            case "HOOK_DLL":
                // 远程注入取光标位置代价高且易被安全软件拦截：按 GUI → MSAA 回退
                return TryGui(hwnd, out rect) || TryAccessibleWithTimeout(hwnd, out rect);
            default:
                return false;
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        Win32.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>GetGUIThreadInfo：经典 Win32 编辑控件的插入符位置。</summary>
    public static bool TryGui(IntPtr hwnd, out Win32.RECT rect)
    {
        rect = default;
        uint thread = Win32.GetWindowThreadProcessId(hwnd, out _);
        var info = new Win32.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (!Win32.GetGUIThreadInfo(thread, ref info) || info.hwndCaret == IntPtr.Zero)
        {
            return false;
        }
        var point = new Win32.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        Win32.ClientToScreen(info.hwndCaret, ref point);
        rect = new Win32.RECT
        {
            Left = point.X,
            Top = point.Y,
            Right = point.X + Math.Max(1, info.rcCaret.Width),
            Bottom = point.Y + Math.Max(1, info.rcCaret.Height),
        };
        return true;
    }

    /// <summary>带 180ms 超时的 MSAA 调用；同一窗口连续失败后冷却 2 秒，避免每帧都卡。</summary>
    private static bool TryAccessibleWithTimeout(IntPtr hwnd, out Win32.RECT rect)
    {
        rect = default;
        long now = (long)Win32.GetTickCount64();
        if (hwnd == _lastFailedHwnd && now - _lastFailedAt < 2000)
        {
            return false;
        }
        if (Interlocked.CompareExchange(ref _msaaBusy, 1, 0) != 0)
        {
            return false;
        }

        var result = new AccessibleResult();
        _ = Task.Run(() =>
        {
            try
            {
                Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED);
                result.Success = TryAccessible(hwnd, ObjidCaret, out Win32.RECT value);
                result.Rect = value;
            }
            catch
            {
                result.Success = false;
            }
            finally
            {
                try
                {
                    Win32.CoUninitialize();
                }
                catch
                {
                }
                Volatile.Write(ref _msaaBusy, 0);
                result.Completed.Set();
            }
        });

        if (!result.Completed.Wait(180))
        {
            _lastFailedHwnd = hwnd;
            _lastFailedAt = now;
            return false;
        }
        if (!result.Success)
        {
            _lastFailedHwnd = hwnd;
            _lastFailedAt = now;
            return false;
        }
        rect = result.Rect;
        return true;
    }

    private sealed class AccessibleResult
    {
        public readonly ManualResetEventSlim Completed = new(false);
        public bool Success;
        public Win32.RECT Rect;

        public AccessibleResult()
        {
            // 后台任务完成后释放等待信号；成功/失败由 Success 区分
        }
    }

    /// <summary>MSAA：AccessibleObjectFromWindow(OBJID_CARET) + IAccessible::accLocation（vtable 22）。</summary>
    private static unsafe bool TryAccessible(IntPtr hwnd, uint objectId, out Win32.RECT rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        IntPtr accessible = IntPtr.Zero;
        try
        {
            Guid iid = IidAccessible;
            if (Win32.AccessibleObjectFromWindow(hwnd, objectId, ref iid, out accessible) != 0 || accessible == IntPtr.Zero)
            {
                return false;
            }

            IntPtr* vtable = *(IntPtr**)accessible;
            if (vtable is null)
            {
                return false;
            }
            // accLocation(long*, long*, long*, long*, VARIANT varChild)
            var accLocation = (delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int*, int*, IntPtr, int>)vtable[22];
            if (accLocation is null)
            {
                return false;
            }

            int left = 0, top = 0, width = 0, height = 0;
            IntPtr variant = Marshal.AllocHGlobal(24);
            try
            {
                for (int i = 0; i < 24; i++)
                {
                    Marshal.WriteByte(variant, i, 0);
                }
                Marshal.WriteInt16(variant, 0, 3);  // VT_I4
                Marshal.WriteInt32(variant, 8, 0); // varChild = 0（自身）
                int hr = accLocation(accessible, &left, &top, &width, &height, variant);
                if (hr != 0 || (left == 0 && top == 0))
                {
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(variant);
            }

            rect = new Win32.RECT
            {
                Left = left,
                Top = top,
                Right = left + Math.Max(1, width),
                Bottom = top + Math.Max(1, height),
            };
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (accessible != IntPtr.Zero)
            {
                try
                {
                    IntPtr* vtable = *(IntPtr**)accessible;
                    var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
                    release(accessible);
                }
                catch
                {
                }
            }
        }
    }
}

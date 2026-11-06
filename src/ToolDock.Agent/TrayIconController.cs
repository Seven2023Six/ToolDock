using System.Runtime.InteropServices;
using ToolDock.Agent.Native;

namespace ToolDock.Agent;

/// <summary>
/// 唯一托盘图标的拥有者（架构公理 1）。纯 Win32 实现：隐藏消息窗口 + Shell_NotifyIconW，
/// 不拖任何 UI 框架——这是 NativeAOT 常驻体积的关键。
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WM_APP + 1;

    // WndProc 委托必须终身保活，否则 native 回调进托管代码会崩
    private static readonly NativeMethods.WndProc WndProcThunk = WndProc;
    private static TrayIconController? _instance;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;
    private uint _taskbarCreatedMsg;
    private readonly List<(string Text, Action Action)> _pluginItems = new();
    private readonly Dictionary<string, string> _statusLines = new();

    /// <summary>用户点击托盘（左键/双击）要求打开主界面。</summary>
    public event Action? OpenRequested;

    /// <summary>用户从托盘菜单选择"退出"。</summary>
    public event Action? ExitRequested;

    public void Initialize()
    {
        _instance = this;

        var wndClass = new NativeMethods.WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcThunk),
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = "ToolDockAgentWnd",
        };
        NativeMethods.RegisterClassW(ref wndClass);

        _hwnd = NativeMethods.CreateWindowExW(0, "ToolDockAgentWnd", "ToolDockAgent", 0,
            0, 0, 0, 0, (IntPtr)NativeMethods.HWND_MESSAGE, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("创建托盘消息窗口失败");
        }

        _hIcon = TrayIconFactory.Create(
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON) <= 16 ? 16 : 32);
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        AddIcon();
    }

    /// <summary>阻塞运行消息循环，直到托盘菜单"退出"。</summary>
    public void RunMessageLoop()
    {
        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
    }

    public void RequestExit() => NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public void AddPluginItem(string text, Action action) => _pluginItems.Add((text, action));

    public void UpdateStatusLine(string pluginId, string? text)
    {
        if (text is null)
        {
            _statusLines.Remove(pluginId);
        }
        else
        {
            _statusLines[pluginId] = text;
        }
        ModifyIcon(NativeMethods.NIF_TIP);
    }

    public void ShowBalloon(string title, string message)
    {
        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NativeMethods.NIF_INFO,
            szInfo = message.Length > 255 ? message[..255] : message,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            dwInfoFlags = NativeMethods.NIIF_INFO,
        };
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
    }

    public void Dispose()
    {
        RemoveIcon();
        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void AddIcon()
    {
        var nid = BuildIconData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid);
    }

    private void RemoveIcon()
    {
        if (!_added)
        {
            return;
        }
        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
        };
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid);
        _added = false;
    }

    private void ModifyIcon(uint flags)
    {
        if (!_added)
        {
            return;
        }
        var nid = BuildIconData(flags);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
    }

    private NativeMethods.NOTIFYICONDATAW BuildIconData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = BuildTip(),
    };

    private string BuildTip()
    {
        var tip = "工具坞 ToolDock";
        foreach (var line in _statusLines.Values)
        {
            tip += "\n" + line;
        }
        return tip.Length > 127 ? tip[..127] : tip;
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        try
        {
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)1, "打开工具坞");
            uint nextId = 100;
            if (_pluginItems.Count > 0)
            {
                NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
                foreach (var (text, _) in _pluginItems)
                {
                    NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)nextId, text);
                    nextId++;
                }
            }
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)2, "退出");

            NativeMethods.GetCursorPos(out var pt);
            // TrackPopupMenu 前必须 SetForegroundWindow，否则点击菜单外不消失（经典坑）
            NativeMethods.SetForegroundWindow(_hwnd);
            uint cmd = (uint)NativeMethods.TrackPopupMenu(menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_NONOTIFY | NativeMethods.TPM_RETURNCMD,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd == 1)
            {
                OpenRequested?.Invoke();
            }
            else if (cmd == 2)
            {
                ExitRequested?.Invoke();
            }
            else if (cmd >= 100 && cmd - 100 < (uint)_pluginItems.Count)
            {
                _pluginItems[(int)(cmd - 100)].Action();
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _instance;
        if (self is null)
        {
            return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        if (msg == CallbackMessage)
        {
            uint mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse is 0x0202 /*WM_LBUTTONUP*/ or 0x0203 /*WM_LBUTTONDBLCLK*/)
            {
                self.OpenRequested?.Invoke();
            }
            else if (mouse == 0x0205 /*WM_RBUTTONUP*/)
            {
                self.ShowMenu();
            }
            return IntPtr.Zero;
        }

        // Explorer 重启后托盘图标会被清掉，TaskbarCreated 广播时补挂回来
        if (self._taskbarCreatedMsg != 0 && msg == self._taskbarCreatedMsg)
        {
            self._added = false;
            self.AddIcon();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_CLOSE)
        {
            self.RemoveIcon();
            NativeMethods.DestroyWindow(hwnd);
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_DESTROY)
        {
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

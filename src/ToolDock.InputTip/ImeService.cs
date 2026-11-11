using System.Runtime.InteropServices;
using System.Text;

namespace ToolDock.InputTip;

/// <summary>
/// 输入法状态检测与切换（移植自 InputTip 的 IME.ahk / Tebayaki 的 IME 库）。
/// 状态判定优先看键盘布局（US/JP/KR 直接返回），中文键盘再通过
/// WM_IME_CONTROL 的打开状态/转换模式判断 CN/EN；general 模式带策略学习，
/// custom 模式按用户规则匹配。
/// </summary>
internal sealed class ImeService
{
    private sealed record LangInfo(int LangId, int Klid, int ConversionMode);

    private static readonly Dictionary<string, LangInfo> LangMap = new()
    {
        ["US"] = new LangInfo(0x09, 0x0409, 0),
        ["CN"] = new LangInfo(0x04, 0x0804, 1025),
        ["JP"] = new LangInfo(0x11, 0x0411, 9),
        ["KR"] = new LangInfo(0x12, 0x0412, 0),
    };

    private sealed class GeneralStrategy
    {
        public int Strategy;               // 0 复合兜底 / 1 纯状态码 / 2 纯转换码
        public long LastChangedTime;
        public string PendingState = "";
        public IntPtr LastHwnd;
        public int LastOpened = -1;
        public int LastConversionMode = -1;
        public bool IsNonBinaryIme;
        public int CnValue;
        public int EnValue;
        public int PendingNonBinary;
        public long NonBinaryTime;
        public long HwndChangedTime;
        public bool IsHwndInitPending;
    }

    private readonly InputTipSettings _settings;
    private readonly GeneralStrategy _general = new();
    private string _lastState = "EN";

    public ImeService(InputTipSettings settings)
    {
        _settings = settings;
    }

    public string LastState => _lastState;

    public void Reset()
    {
        _lastState = "EN";
        _general.Strategy = 0;
        _general.LastChangedTime = 0;
        _general.PendingState = "";
        _general.LastHwnd = IntPtr.Zero;
        _general.LastOpened = -1;
        _general.LastConversionMode = -1;
        _general.IsNonBinaryIme = false;
        _general.CnValue = 0;
        _general.EnValue = 0;
        _general.PendingNonBinary = 0;
        _general.NonBinaryTime = 0;
        _general.HwndChangedTime = 0;
        _general.IsHwndInitPending = false;
    }

    /// <summary>是否处于"窗口切换后的初始化观察期"（此时不触发提示/边框刷新）。</summary>
    public bool IsHwndInitPending => _general.IsHwndInitPending;

    /// <summary>
    /// 获取当前状态（CN/EN/Caps/US/JP/KR）。
    /// </summary>
    /// <param name="snapshot">前台窗口快照，用于布局/进程判断。</param>
    /// <param name="windowChanged">本帧是否发生了进程/标题/类名变化。</param>
    public string GetInputModeText(WindowSnapshot snapshot, bool windowChanged)
    {
        if ((Win32.GetKeyState(Win32.VK_CAPITAL) & 1) != 0)
        {
            _general.IsHwndInitPending = false;
            return _lastState = "Caps";
        }

        IntPtr hwnd = snapshot.FocusedHwnd != IntPtr.Zero ? snapshot.FocusedHwnd : snapshot.Hwnd;
        try
        {
            int langId = (int)((long)GetKeyboardLayout(hwnd) & 0xFF);
            foreach (var (langState, info) in LangMap)
            {
                if (langId == info.LangId)
                {
                    if (langState != "CN")
                    {
                        return _lastState = langState;
                    }
                    break;
                }
            }

            int opened = GetOpenStatus(hwnd);
            int conversionMode = GetConversionMode(hwnd);

            if (_settings.InputMethodDetectionMode == "general" || _settings.ImeRules.Count == 0)
            {
                return _lastState = EvaluateGeneral(snapshot, hwnd, opened, conversionMode, windowChanged);
            }

            string state = _settings.InputMethodBaseState;
            foreach (var rule in _settings.ImeRules)
            {
                if (MatchRule(opened, rule.OpenRule) && MatchRule(conversionMode, rule.ConversionRule))
                {
                    state = rule.State;
                    break;
                }
            }
            return _lastState = state;
        }
        catch
        {
            return _lastState;
        }
    }

    private string EvaluateGeneral(WindowSnapshot snapshot, IntPtr hwnd, int opened, int conversionMode, bool windowChanged)
    {
        var gs = _general;
        long now = (long)Win32.GetTickCount64();
        bool skip = false;
        string state = _lastState;

        if (gs.LastHwnd != hwnd || windowChanged)
        {
            gs.LastHwnd = hwnd;
            gs.HwndChangedTime = now;
            gs.PendingState = "";
            gs.LastChangedTime = 0;
            gs.PendingNonBinary = 0;
            gs.NonBinaryTime = 0;

            if (!string.Equals(snapshot.Process, "explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                gs.IsHwndInitPending = true;
                skip = true;
            }
            else
            {
                gs.IsHwndInitPending = false;
                gs.LastOpened = opened;
                gs.LastConversionMode = conversionMode;
            }
        }

        if (!skip && gs.IsHwndInitPending)
        {
            if (now - gs.HwndChangedTime < 200)
            {
                skip = true;
            }
            else
            {
                gs.LastOpened = opened;
                gs.LastConversionMode = conversionMode;
                gs.IsHwndInitPending = false;
            }
        }

        if (!skip)
        {
            bool openedChanged = opened != gs.LastOpened;
            bool conversionChanged = conversionMode != gs.LastConversionMode;

            if (openedChanged || conversionChanged)
            {
                string currentFeature = opened + "," + conversionMode;
                if (gs.PendingState != currentFeature)
                {
                    gs.PendingState = currentFeature;
                    gs.LastChangedTime = now;
                    skip = true;
                }
                else if (now - gs.LastChangedTime < 200)
                {
                    skip = true;
                }
                else
                {
                    if (conversionChanged)
                    {
                        if (gs.Strategy == 1 && gs.IsNonBinaryIme)
                        {
                            ResetNonBinary(gs);
                        }
                        gs.Strategy = 2;
                    }
                    else if (openedChanged)
                    {
                        if (gs.Strategy == 2 && gs.IsNonBinaryIme)
                        {
                            gs.IsNonBinaryIme = false;
                        }
                        gs.Strategy = 1;
                        if (opened > 1)
                        {
                            if (gs.PendingNonBinary != opened)
                            {
                                gs.PendingNonBinary = opened;
                                gs.NonBinaryTime = now;
                                skip = true;
                            }
                            else if (now - gs.NonBinaryTime < 200)
                            {
                                skip = true;
                            }
                            else
                            {
                                gs.IsNonBinaryIme = true;
                                gs.CnValue = opened;
                                gs.EnValue = gs.LastOpened is -1 or > 1 ? 0 : gs.LastOpened;
                                gs.PendingNonBinary = 0;
                                gs.NonBinaryTime = 0;
                            }
                        }
                        else if (gs.LastOpened is >= 0 and <= 1)
                        {
                            if (gs.IsNonBinaryIme)
                            {
                                ResetNonBinary(gs);
                            }
                        }
                        else
                        {
                            gs.PendingNonBinary = 0;
                            gs.NonBinaryTime = 0;
                        }
                    }
                    gs.LastOpened = opened;
                    gs.LastConversionMode = conversionMode;
                    gs.PendingState = "";
                    gs.LastChangedTime = 0;
                }
            }
            else
            {
                gs.PendingState = "";
                gs.LastChangedTime = 0;
            }
        }

        if (skip)
        {
            return _lastState;
        }

        switch (gs.Strategy)
        {
            case 1:
                state = gs.IsNonBinaryIme
                    ? (opened == gs.CnValue ? "CN" : "EN")
                    : (opened != 0 ? "CN" : "EN");
                break;
            case 2:
                state = (conversionMode & 1) != 0 ? "CN" : "EN";
                break;
            default:
                if (gs.IsNonBinaryIme)
                {
                    state = opened == gs.CnValue ? "CN" : "EN";
                }
                else if (conversionMode == 0)
                {
                    state = opened != 0 ? "CN" : "EN";
                }
                else if (opened == 0)
                {
                    state = "EN";
                }
                else
                {
                    state = (conversionMode & 1) != 0 ? "CN" : "EN";
                }
                break;
        }
        return state;
    }

    private static void ResetNonBinary(GeneralStrategy gs)
    {
        gs.IsNonBinaryIme = false;
        gs.CnValue = 0;
        gs.EnValue = 0;
        gs.PendingNonBinary = 0;
        gs.NonBinaryTime = 0;
    }

    public (int OpenStatus, int ConversionMode) CheckInputMode(IntPtr hwnd)
        => (GetOpenStatus(hwnd), GetConversionMode(hwnd));

    /// <summary>切换到指定状态/键盘布局。</summary>
    public bool SetInputMode(string targetState, int? opened = null, int? conversionMode = null, IntPtr hwnd = default)
    {
        hwnd = hwnd == IntPtr.Zero ? GetFocusedWindow() : hwnd;
        switch (targetState)
        {
            case "US":
                return SwitchKeyboard("US");
            case "JP":
            case "KR":
                SwitchKeyboard(targetState);
                if (opened is not null || conversionMode is not null)
                {
                    Thread.Sleep(50);
                    if (opened is not null)
                    {
                        SetOpenStatus(opened.Value, hwnd);
                    }
                    if (conversionMode is not null)
                    {
                        SetConversionMode(conversionMode.Value, hwnd);
                    }
                }
                return true;
        }

        SwitchKeyboard("CN");

        if (_settings.InputMethodDetectionMode != "general")
        {
            switch (targetState)
            {
                case "CN":
                    SetOpenStatus(1, hwnd);
                    SetConversionMode(LangMap["CN"].ConversionMode, hwnd);
                    break;
                case "EN":
                    SetOpenStatus(0, hwnd);
                    break;
            }
            return true;
        }

        var gs = _general;
        switch (gs.Strategy)
        {
            case 1:
                int status = targetState == "CN"
                    ? (gs.IsNonBinaryIme ? gs.CnValue : 1)
                    : (gs.IsNonBinaryIme ? gs.EnValue : 0);
                SetOpenStatus(status, hwnd);
                break;
            case 2:
                SetConversionMode(targetState == "CN" ? LangMap["CN"].ConversionMode : 0, hwnd);
                break;
            default:
                if (gs.IsNonBinaryIme)
                {
                    SetOpenStatus(targetState == "CN" ? gs.CnValue : gs.EnValue, hwnd);
                }
                else if (targetState == "CN")
                {
                    SetOpenStatus(1, hwnd);
                    SetConversionMode(LangMap["CN"].ConversionMode, hwnd);
                }
                else
                {
                    SetOpenStatus(0, hwnd);
                    SetConversionMode(0, hwnd);
                }
                break;
        }

        Thread.Sleep(20);
        gs.LastOpened = GetOpenStatus(hwnd);
        gs.LastConversionMode = GetConversionMode(hwnd);
        return true;
    }

    private static bool MatchRule(int value, string rule)
    {
        if (string.IsNullOrEmpty(rule))
        {
            return true;
        }
        return rule switch
        {
            "evenNum" => (value & 1) == 0,
            "oddNum" => (value & 1) != 0,
            _ => ("/" + rule + "/").Contains("/" + value + "/", StringComparison.Ordinal),
        };
    }

    public int GetOpenStatus(IntPtr hwnd)
    {
        try
        {
            IntPtr imeWnd = Win32.ImmGetDefaultIMEWnd(hwnd);
            Win32.SendMessageTimeoutW(imeWnd, Win32.WM_IME_CONTROL, (IntPtr)0x5, IntPtr.Zero, 0,
                (uint)_settings.InputMethodDetectionTimeout, out IntPtr result);
            return (int)(long)result;
        }
        catch
        {
            return 0;
        }
    }

    public void SetOpenStatus(int status, IntPtr hwnd)
    {
        try
        {
            IntPtr imeWnd = Win32.ImmGetDefaultIMEWnd(hwnd);
            Win32.SendMessageTimeoutW(imeWnd, Win32.WM_IME_CONTROL, (IntPtr)0x6, (IntPtr)status, 0,
                (uint)_settings.InputMethodDetectionTimeout, out _);
        }
        catch
        {
        }
    }

    public int GetConversionMode(IntPtr hwnd)
    {
        try
        {
            IntPtr imeWnd = Win32.ImmGetDefaultIMEWnd(hwnd);
            Win32.SendMessageTimeoutW(imeWnd, Win32.WM_IME_CONTROL, (IntPtr)0x1, IntPtr.Zero, 0,
                (uint)_settings.InputMethodDetectionTimeout, out IntPtr result);
            return (int)(long)result;
        }
        catch
        {
            return 0;
        }
    }

    public void SetConversionMode(int mode, IntPtr hwnd)
    {
        try
        {
            IntPtr imeWnd = Win32.ImmGetDefaultIMEWnd(hwnd);
            Win32.SendMessageTimeoutW(imeWnd, Win32.WM_IME_CONTROL, (IntPtr)0x2, (IntPtr)mode, 0,
                (uint)_settings.InputMethodDetectionTimeout, out _);
        }
        catch
        {
        }
    }

    public static IntPtr GetKeyboardLayout(IntPtr hwnd)
    {
        uint thread = Win32.GetWindowThreadProcessId(hwnd, out _);
        return Win32.GetKeyboardLayout(thread);
    }

    private static bool SetKeyboardLayout(IntPtr layout, IntPtr hwnd)
    {
        try
        {
            Win32.SendMessageW(hwnd, Win32.WM_INPUTLANGCHANGEREQUEST, (IntPtr)1, layout);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IntPtr[] GetKeyboardLayoutList()
    {
        int count = Win32.GetKeyboardLayoutList(0, null);
        if (count <= 0)
        {
            return [];
        }
        var list = new IntPtr[count];
        Win32.GetKeyboardLayoutList(count, list);
        return list;
    }

    /// <summary>切换到指定语言键盘布局（CN/US/JP/KR），必要时加载布局。</summary>
    public static bool SwitchKeyboard(string state)
    {
        if (!LangMap.TryGetValue(state, out var info))
        {
            return false;
        }
        IntPtr hwnd = GetFocusedWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        foreach (IntPtr layout in GetKeyboardLayoutList())
        {
            if (((long)layout & 0xFF) == info.LangId)
            {
                return SetKeyboardLayout(layout, hwnd);
            }
        }
        IntPtr loaded = Win32.LoadKeyboardLayoutW(info.Klid.ToString("x8"), 0x101);
        return loaded != IntPtr.Zero && SetKeyboardLayout(loaded, hwnd);
    }

    public static IntPtr GetFocusedWindow()
    {
        IntPtr foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        return WindowProbe.GetFocusedWindow(foreground);
    }

    /// <summary>当前键盘布局的显示名（供设置页/日志展示）。</summary>
    public static string DescribeKeyboardLayout()
    {
        var buffer = new StringBuilder(16);
        IntPtr layout = GetKeyboardLayout(Win32.GetForegroundWindow());
        return layout == IntPtr.Zero ? "" : "0x" + ((long)layout & 0xFFFF).ToString("X4");
    }

    /// <summary>SendInput 模拟按键（支持 {LShift}、{Ctrl Down}{Space Down} 这类序列）。</summary>
    public static void SendKeys(string sequence)
    {
        var inputs = new List<Win32.INPUT>();
        int index = 0;
        while (index < sequence.Length)
        {
            int open = sequence.IndexOf('{', index);
            if (open < 0)
            {
                string text = sequence[index..];
                AppendUnicode(inputs, text);
                break;
            }
            if (open > index)
            {
                AppendUnicode(inputs, sequence[index..open]);
            }
            int close = sequence.IndexOf('}', open);
            if (close < 0)
            {
                AppendUnicode(inputs, sequence[open..]);
                break;
            }
            string token = sequence[(open + 1)..close].Trim();
            bool up = false;
            if (token.EndsWith(" up", StringComparison.OrdinalIgnoreCase))
            {
                up = true;
                token = token[..^3].Trim();
            }
            else if (token.EndsWith(" down", StringComparison.OrdinalIgnoreCase))
            {
                token = token[..^5].Trim();
            }
            ushort vk = VirtualKeyFromName(token);
            if (vk != 0)
            {
                inputs.Add(KeyInput(vk, up));
            }
            index = close + 1;
        }

        if (inputs.Count > 0)
        {
            Win32.SendInput((uint)inputs.Count, inputs.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>());
        }
    }

    private static void AppendUnicode(List<Win32.INPUT> inputs, string text)
    {
        foreach (char c in text)
        {
            inputs.Add(UnicodeInput(c, up: false));
            inputs.Add(UnicodeInput(c, up: true));
        }
    }

    private static Win32.INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        u = new Win32.INPUTUNION
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0,
            },
        },
    };

    private static Win32.INPUT UnicodeInput(char c, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        u = new Win32.INPUTUNION
        {
            ki = new Win32.KEYBDINPUT
            {
                wScan = c,
                dwFlags = Win32.KEYEVENTF_UNICODE | (up ? Win32.KEYEVENTF_KEYUP : 0),
            },
        },
    };

    public static ushort VirtualKeyFromName(string name)
    {
        name = name.Trim();
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }
            short scan = Win32.VkKeyScanW(name[0]);
            if (scan != -1)
            {
                return (ushort)(scan & 0xFF);
            }
        }
        return name.ToUpperInvariant() switch
        {
            "LSHIFT" => Win32.VK_LSHIFT,
            "RSHIFT" => Win32.VK_RSHIFT,
            "SHIFT" => Win32.VK_SHIFT,
            "LCTRL" or "LCONTROL" => Win32.VK_LCONTROL,
            "RCTRL" or "RCONTROL" => Win32.VK_RCONTROL,
            "CTRL" or "CONTROL" => Win32.VK_CONTROL,
            "LALT" or "LMENU" => Win32.VK_LMENU,
            "RALT" or "RMENU" => Win32.VK_RMENU,
            "ALT" or "MENU" => Win32.VK_MENU,
            "LWIN" => Win32.VK_LWIN,
            "RWIN" => Win32.VK_RWIN,
            "SPACE" => Win32.VK_SPACE,
            "ESC" or "ESCAPE" => Win32.VK_ESCAPE,
            "CAPSLOCK" or "CAPS" => Win32.VK_CAPITAL,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "BACKSPACE" or "BS" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            _ => (ushort)0,
        };
    }
}

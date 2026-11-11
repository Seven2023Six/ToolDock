using System.Runtime.InteropServices;
using System.Text;

namespace ToolDock.InputTip;

/// <summary>引擎诊断日志出口（Agent 插件接到文件日志）。</summary>
public static class InputTipDiagnostics
{
    public static Action<string>? Log { get; set; }
}

/// <summary>
/// 输入法状态提示引擎：专用 STA 线程 + 消息循环，轮询前台窗口与输入法状态，
/// 驱动鼠标样式 / 悬浮小窗 / 光标符号 / 鼠标符号 / 窗口边框，并执行规则与热键。
/// 由常驻 Agent 进程内托管，没有独立进程和托盘。
/// </summary>
public sealed class InputTipEngine : IDisposable
{
    public event Action<string>? StateChanged;
    public event Action<bool>? PausedChanged;
    public event Action<string>? TrayTipChanged;
    public event Action? ExitRequested;
    public event Action? RestartRequested;

    /// <summary>诊断日志（异常/窗口创建失败等），由 Agent 插件接到文件日志。</summary>
    public Action<string>? Log { get; set; }

    private readonly InputTipSettings _settings;
    private readonly RuleEngine _rules;
    private readonly ImeService _ime;
    private readonly CursorService _cursor;
    private readonly string _defaultSymbolDirectory;
    private readonly string _defaultCursorDirectory;

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _paused;
    private volatile bool _disposed;
    private Win32.HookProc? _keyboardHook;
    private IntPtr _hookHandle;
    private uint _threadId;
    private uint _ownPid;

    private long _lastTick;
    private long _lastStatusUpdate;
    private long _lastKeyStatsSave;

    // 窗口/状态缓存
    private WindowSnapshot _snapshot = WindowSnapshot.Empty;
    private string _state = "EN";
    private string _lastProcess = "";
    private string _lastTitle = "";
    private string _lastClass = "";
    private string _lastControl = "";
    private string _lastOverlayState = "";
    private string _lastExportState = "";
    private string _lastCaptureMode = "";
    private bool _hasProcessChange;
    private bool _hasTitleChange;
    private bool _hasClassChange;
    private bool _needShowCaret;
    private bool _caretVisible;
    private long _caretShownAt;
    private string _caretKey = "";
    private string _caretSuppressedKey = "";
    private string _cursorKey = "";
    private bool _cursorSymbolHidden;
    private long _cursorSymbolShownAt;
    private long _lastCursorMoveTick;
    private Win32.POINT _lastCursorPosition;
    private bool _shiftAloneDown;
    private bool _shiftAloneFlag;

    // 提示窗口
    private readonly Dictionary<string, OverlayWindow[]> _overlayWindows = new();
    private readonly Dictionary<string, SymbolWindow[]> _caretWindows = new();
    private readonly Dictionary<string, SymbolWindow[]> _cursorWindows = new();
    private readonly List<BorderFrameWindow> _borderFrames = [];
    private TooltipWindow? _tooltip;
    private IntPtr _borderHwnd;
    private string _borderFingerprint = "";
    private bool _tooltipStateCode;
    private bool _tooltipCaptureMode;

    // 定时任务
    private readonly List<(long Due, Action Action)> _timers = [];
    private readonly List<InputTipRule> _activeHotkeyRules = [];
    private readonly List<InputTipRule> _activeTextRules = [];
    private readonly List<InputTipRule> _activeHotkeyMonitorRules = [];
    private long _idleTimerDue;
    private long _idleTimerDelay;
    private string _idleTimerTrigger = "";
    private readonly List<string> _hotkeyMonitorBuffer = [];
    private int _hotkeyMonitorMaxLength;
    private long _lastHotkeyMonitorTick;
    private readonly StringBuilder _textBuffer = new();
    private readonly HashSet<int> _pressedKeys = [];
    private readonly Dictionary<string, long> _hotkeyDebounce = new(StringComparer.Ordinal);
    private int _lastKeyVk;

    // 按键统计
    private long _keyCount;
    private string _keyStatsDate = "";

    public InputTipEngine(InputTipSettings settings, string defaultSymbolDirectory, string defaultCursorDirectory)
    {
        _settings = settings;
        _rules = new RuleEngine(settings);
        _ime = new ImeService(settings);
        _cursor = new CursorService(settings);
        _defaultSymbolDirectory = defaultSymbolDirectory;
        _defaultCursorDirectory = defaultCursorDirectory;
        _keyStatsDate = DateTime.Now.ToString("yyyy-MM-dd");
        LoadKeyStats();
    }

    public bool IsPaused => _paused;
    public string CurrentState => _state;
    public string LastCaptureMode => _lastCaptureMode;

    public void Start()
    {
        if (_thread is { IsAlive: true })
        {
            return;
        }
        _stop = false;
        _thread = new Thread(EngineThread)
        {
            IsBackground = true,
            Name = "InputTipEngine",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        if (_thread is { IsAlive: true })
        {
            _thread.Join(3000);
        }
        _thread = null;
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return;
        }
        _paused = paused;
        if (paused)
        {
            Schedule(0, SuspendNow);
        }
        else
        {
            Schedule(0, ResumeNow);
        }
    }

    public void TogglePause() => SetPaused(!_paused);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        try
        {
            _cursor.Revert();
        }
        catch
        {
        }
    }

    /// <summary>销毁提示窗口。Win32 窗口必须在创建它的线程上销毁，只能在引擎线程退出前调用。</summary>
    private void DisposeWindows()
    {
        foreach (var windows in _overlayWindows.Values)
        {
            foreach (var window in windows)
            {
                window.Dispose();
            }
        }
        _overlayWindows.Clear();
        foreach (var windows in _caretWindows.Values)
        {
            foreach (var window in windows)
            {
                window.Dispose();
            }
        }
        _caretWindows.Clear();
        foreach (var windows in _cursorWindows.Values)
        {
            foreach (var window in windows)
            {
                window.Dispose();
            }
        }
        _cursorWindows.Clear();
        ResetBorderFrames();
        _tooltip?.Dispose();
        _tooltip = null;
    }

    // ------------------------------------------------------------------
    // 线程与主循环
    // ------------------------------------------------------------------

    private void EngineThread()
    {
        _threadId = Win32.GetCurrentThreadId();
        _ownPid = Win32.GetCurrentProcessId();
        try
        {
            Win32.SetThreadDpiAwarenessContext((IntPtr)(-4)); // Per-Monitor V2
            Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED);
            _keyboardHook = KeyboardHook;
            _hookHandle = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _keyboardHook, Win32.GetModuleHandleW(null), 0);
            if (_hookHandle == IntPtr.Zero)
            {
                // 钩子失败不影响轮询式提示，只是热键/文本监控不可用
            }
            _lastTick = 0;
            _lastStatusUpdate = 0;
            // 全局热键规则不依赖前台窗口：启动即装载，避免空闲后的第一次按键被漏掉
            UpdateWindowHotkeys(WindowSnapshot.Empty);
            Log?.Invoke($"引擎线程启动（键盘钩子={( _hookHandle != IntPtr.Zero ? "已安装" : "安装失败")}）");

            while (!_stop)
            {
                PumpMessages();
                long now = (long)Win32.GetTickCount64();
                int interval = Math.Clamp(_settings.PollInterval, 5, 100);
                if (now - _lastTick >= interval)
                {
                    _lastTick = now;
                    try
                    {
                        Tick(now);
                    }
                    catch (Exception exception)
                    {
                        // 单帧异常不能终止引擎，但必须留痕
                        Log?.Invoke("Tick 异常：" + exception);
                    }
                }
                RunScheduled(now);
                Win32.MsgWaitForMultipleObjectsEx(0, [], 10, Win32.QS_ALLINPUT, Win32.MWMO_INPUTAVAILABLE);
            }
        }
        catch (Exception exception)
        {
            Log?.Invoke("引擎线程异常退出：" + exception);
        }
        finally
        {
            try
            {
                HideAllTips();
                _cursor.Revert();
                DisposeWindows();
            }
            catch
            {
            }
            if (_hookHandle != IntPtr.Zero)
            {
                Win32.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            try
            {
                Win32.CoUninitialize();
            }
            catch
            {
            }
            _threadId = 0;
        }
    }

    private static void PumpMessages()
    {
        while (Win32.PeekMessageW(out Win32.MSG msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    private void Tick(long now)
    {
        if (_paused || WindowProbe.IsLocked())
        {
            ResetWindowCache();
            return;
        }

        uint idle = WindowProbe.IdleMilliseconds();
        int leaveDelay = Math.Clamp(_settings.PollInterval, 5, 100) + 500;
        if (idle > leaveDelay)
        {
            return;
        }

        _snapshot = WindowProbe.Probe();
        if (!_snapshot.Valid)
        {
            return;
        }

        _hasProcessChange = !string.Equals(_lastProcess, _snapshot.Process, StringComparison.Ordinal);
        _hasTitleChange = !string.Equals(_lastTitle, _snapshot.Title, StringComparison.Ordinal);
        _hasClassChange = !string.Equals(_lastClass, _snapshot.Class, StringComparison.Ordinal);
        bool controlChanged = !string.Equals(_lastControl, _snapshot.ControlClass, StringComparison.Ordinal);
        bool windowChanged = _hasProcessChange || _hasTitleChange || _hasClassChange || controlChanged;

        if (_hasProcessChange)
        {
            InitMonitor(_snapshot);
        }

        if (windowChanged)
        {
            _needShowCaret = _settings.CaretSymbolType != 0;
            if (_settings.CaretSymbolType != 0 && _snapshot.Pid != _ownPid &&
                (_rules.MatchDisplay(_settings.CaretSymbolRules.Where(r => r.Trigger == "hide"), _snapshot) ||
                 !_rules.MatchDisplay(_settings.CaretSymbolRules.Where(r => r.Trigger == "show"), _snapshot)))
            {
                HideCaretSymbol();
                _needShowCaret = false;
            }

            _lastProcess = _snapshot.Process;
            _lastTitle = _snapshot.Title;
            _lastClass = _snapshot.Class;
            _lastControl = _snapshot.ControlClass;
            _lastOverlayState = "";
            _caretVisible = false;
            _lastCursorMoveTick = 0;

            UpdateWindowHotkeys(_snapshot);
            RunTriggers(ReturnTriggers(_snapshot));
        }

        string state = _ime.GetInputModeText(_snapshot, windowChanged);
        if (string.IsNullOrEmpty(state))
        {
            return;
        }
        if (state != _state)
        {
            _state = state;
            StateChanged?.Invoke(state);
        }

        // 光标符号
        string captureChain = _rules.GetCaptureChain(_snapshot);
        if (!captureChain.Contains("JAB", StringComparison.OrdinalIgnoreCase))
        {
            ShowCaretSymbolEx(captureChain);
        }
        else
        {
            HideCaretSymbol();
        }


        if (_ime.IsHwndInitPending)
        {
            return;
        }

        UpdateBorder(state, now);

        // 鼠标符号
        UpdateCursorSymbol(state, idle);

        // 鼠标样式
        _cursor.Load(state);

        // 悬浮小窗
        UpdateOverlay(state, windowChanged);

        // 导出状态
        if (_settings.ExportState && state != _lastExportState)
        {
            try
            {
                File.WriteAllText(_settings.ExportStatePath, state, Encoding.UTF8);
            }
            catch
            {
            }
            _lastExportState = state;
        }

        // 托盘状态行（10 秒一次，避免刷屏）
        if (now - _lastStatusUpdate >= 10_000)
        {
            _lastStatusUpdate = now;
            UpdateTrayTip();
        }
    }

    private void ResetWindowCache()
    {
        _lastProcess = "";
        _lastTitle = "";
        _lastClass = "";
        _lastControl = "";
        _lastOverlayState = "";
        _caretVisible = false;
        _needShowCaret = false;
    }

    // ------------------------------------------------------------------
    // 提示：光标符号
    // ------------------------------------------------------------------

    private void ShowCaretSymbolEx(string captureChain)
    {
        if (_settings.CaretSymbolType == 0)
        {
            HideCaretSymbol();
            return;
        }
        if (!_needShowCaret)
        {
            return;
        }
        if (!CaretLocator.TryGet(_snapshot.Hwnd, captureChain, out Win32.RECT caret, out string mode))
        {
            HideCaretSymbol();
            return;
        }
        _lastCaptureMode = mode;

        Win32.RECT windowRect = WindowProbe.GetPhysicalRect(_snapshot.Hwnd);
        if (caret.Top < windowRect.Top || caret.Top > windowRect.Bottom)
        {
            HideCaretSymbol();
            return;
        }

        var screen = WindowProbe.CaptureScreens().FromRect(windowRect);
        double scale = screen?.Scale ?? 1.0;
        var style = _settings.CaretSymbolStyles.TryGetValue(_state, out var s) ? s : new SymbolStyle();

        int offsetY = _settings.CaretSymbolOriginY == "below" ? caret.Bottom : caret.Top;
        int x, y;
        switch (_settings.CaretSymbolType)
        {
            case 1:
                x = caret.Left + WindowProbe.ToPhysical(style.PictureOffsetX, scale);
                y = offsetY + WindowProbe.ToPhysical(style.PictureOffsetY, scale);
                break;
            case 2:
                x = caret.Left + WindowProbe.ToPhysical(style.ShapeOffsetX, scale);
                y = offsetY + WindowProbe.ToPhysical(style.ShapeOffsetY, scale);
                break;
            default:
                x = caret.Left + WindowProbe.ToPhysical(style.TextOffsetX, scale);
                y = offsetY + WindowProbe.ToPhysical(style.TextOffsetY, scale);
                break;
        }

        // 屏幕偏移 + 窗口级偏移
        if (screen is not null)
        {
            var screenOffset = _settings.CaretScreenOffsets.TryGetValue(screen.Index.ToString(), out var so) ? so : null;
            if (screenOffset is not null)
            {
                x += WindowProbe.ToPhysical(screenOffset.X, scale);
                y += WindowProbe.ToPhysical(screenOffset.Y, scale);
            }
            var offsetRule = _rules.GetWindowOffsetRule(_snapshot);
            if (offsetRule is not null)
            {
                var (ox, oy) = RuleEngine.ParseScreenOffset(offsetRule.Offset, screen.Index);
                x += WindowProbe.ToPhysical(ox, scale);
                y += WindowProbe.ToPhysical(oy, scale);
            }
            var (cx, cy) = ParseCaptureOffset(captureChain, _rules.GetCaptureOffset(_snapshot), mode);
            x += WindowProbe.ToPhysical(cx, scale);
            y += WindowProbe.ToPhysical(cy, scale);
        }

        string key = $"{x},{y},{_state},{_settings.CaretSymbolType}";
        if ((_caretVisible && key == _caretKey) || key == _caretSuppressedKey)
        {
            return;
        }

        SymbolWindow window = GetSymbolWindow(_caretWindows, "caret", _state, _settings.CaretSymbolType,
            _settings.CaretSymbolTextEdgeStyle, _settings.CaretSymbolShapeEdgeStyle,
            _settings.CaretSymbolTextCornerPreference, _settings.CaretSymbolShapeCornerPreference);
        if (!window.IsValid)
        {
            return;
        }
        HideCaretWindows();
        window.ShowAt(style, x, y, scale, _settings.SymbolDirectory, _defaultSymbolDirectory);
        _caretVisible = true;
        _caretKey = key;
        _caretShownAt = (long)Win32.GetTickCount64();
    }

    private static (int X, int Y) ParseCaptureOffset(string captureChain, string captureOffset, string mode)
    {
        if (string.IsNullOrWhiteSpace(captureChain) || string.IsNullOrWhiteSpace(captureOffset))
        {
            return (0, 0);
        }
        string[] modes = captureChain.Split('>');
        string[] offsets = captureOffset.Split('>');
        for (int i = 0; i < modes.Length && i < offsets.Length; i++)
        {
            if (!string.Equals(modes[i].Trim(), mode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string[] parts = offsets[i].Split('/');
            if (parts.Length >= 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
            {
                return (x, y);
            }
        }
        return (0, 0);
    }

    private void HideCaretWindows()
    {
        foreach (var windows in _caretWindows.Values)
        {
            foreach (var window in windows)
            {
                window.Hide();
            }
        }
        _caretVisible = false;
    }

    private void HideCaretSymbol()
    {
        HideCaretWindows();
        _caretKey = "";
        _caretSuppressedKey = "";
    }

    // ------------------------------------------------------------------
    // 提示：鼠标符号
    // ------------------------------------------------------------------

    private void UpdateCursorSymbol(string state, uint idle)
    {
        if (_settings.CursorSymbolType == 0)
        {
            HideCursorSymbol();
            return;
        }
        if (WindowProbe.IdleMilliseconds() > Math.Max(100, _settings.PollInterval + 500))
        {
            return;
        }

        Win32.GetCursorPos(out Win32.POINT cursor);
        bool moved = cursor.X != _lastCursorPosition.X || cursor.Y != _lastCursorPosition.Y;
        if (moved)
        {
            _lastCursorPosition = cursor;
            _lastCursorMoveTick = (long)Win32.GetTickCount64();
            _cursorSymbolHidden = false;
        }

        bool allowed = _settings.CursorSymbolShowMode == "whitelist"
            ? _snapshot.Pid == _ownPid || _rules.MatchDisplay(_settings.CursorSymbolRules.Where(r => r.Trigger == "show"), _snapshot)
            : !(_snapshot.Pid != _ownPid && _rules.MatchDisplay(_settings.CursorSymbolRules.Where(r => r.Trigger == "hide"), _snapshot));

        if (!allowed)
        {
            HideCursorSymbol();
            return;
        }
        if (_cursorSymbolHidden && !moved)
        {
            return;
        }

        var style = _settings.CursorSymbolStyles.TryGetValue(state, out var s) ? s : new SymbolStyle();
        var screen = WindowProbe.CaptureScreens().FromPoint(cursor);
        double scale = screen?.Scale ?? 1.0;
        int x = cursor.X, y = cursor.Y;
        if (screen is not null && _settings.CursorScreenOffsets.TryGetValue(screen.Index.ToString(), out var offset))
        {
            x += WindowProbe.ToPhysical(offset.X, scale);
            y += WindowProbe.ToPhysical(offset.Y, scale);
        }
        switch (_settings.CursorSymbolType)
        {
            case 1:
                x += WindowProbe.ToPhysical(style.PictureOffsetX, scale);
                y += WindowProbe.ToPhysical(style.PictureOffsetY, scale);
                break;
            case 2:
                x += WindowProbe.ToPhysical(style.ShapeOffsetX, scale);
                y += WindowProbe.ToPhysical(style.ShapeOffsetY, scale);
                break;
            default:
                x += WindowProbe.ToPhysical(style.TextOffsetX, scale);
                y += WindowProbe.ToPhysical(style.TextOffsetY, scale);
                break;
        }

        string key = $"{x},{y},{state},{_settings.CursorSymbolType}";
        if (key == _cursorKey)
        {
            return;
        }
        SymbolWindow window = GetSymbolWindow(_cursorWindows, "cursor", state, _settings.CursorSymbolType,
            _settings.CursorSymbolTextEdgeStyle, _settings.CursorSymbolShapeEdgeStyle,
            _settings.CursorSymbolTextCornerPreference, _settings.CursorSymbolShapeCornerPreference);
        if (window.IsValid)
        {
            window.ShowAt(style, x, y, scale, _settings.SymbolDirectory, _defaultSymbolDirectory);
            _cursorKey = key;
        }
        _cursorSymbolShownAt = (long)Win32.GetTickCount64();
    }

    private void HideCursorSymbol()
    {
        foreach (var windows in _cursorWindows.Values)
        {
            foreach (var window in windows)
            {
                window.Hide();
            }
        }
        _cursorSymbolHidden = true;
        _cursorKey = "";
    }

    private SymbolWindow GetSymbolWindow(Dictionary<string, SymbolWindow[]> cache, string prefix, string state,
        int type, int textEdge, int shapeEdge, int textCorner, int shapeCorner)
    {
        SymbolKind kind = type switch
        {
            1 => SymbolKind.Picture,
            2 => SymbolKind.Shape,
            _ => SymbolKind.Text,
        };
        if (!cache.TryGetValue(state, out var windows) || windows[0].Kind != kind)
        {
            if (windows is not null)
            {
                foreach (var old in windows)
                {
                    old.Dispose();
                }
            }
            windows =
            [
                new SymbolWindow(kind, kind == SymbolKind.Text ? textEdge : shapeEdge,
                    kind == SymbolKind.Text ? textCorner : shapeCorner),
            ];
            cache[state] = windows;
        }
        return windows[0];
    }

    // ------------------------------------------------------------------
    // 提示：悬浮小窗
    // ------------------------------------------------------------------

    private void UpdateOverlay(string state, bool windowChanged)
    {
        if (!_settings.OverlayActive)
        {
            if (_lastOverlayState != "")
            {
                HideOverlay();
                _lastOverlayState = "";
            }
            return;
        }

        bool reshow = state != _lastOverlayState ||
                      (_settings.OverlayReshowOnTitleChange && _hasTitleChange) ||
                      (_settings.OverlayReshowOnClassChange && _hasClassChange) ||
                      (_settings.OverlayReshowOnProcessChange && _hasProcessChange);
        if (!reshow)
        {
            return;
        }
        _lastOverlayState = state;

        if ((_settings.OverlayShowOnMaximized == false && _snapshot.Maximized) ||
            (_settings.OverlayShowOnFullscreen == false && _snapshot.Fullscreen) ||
            (_settings.OverlayShowOnNormal == false && _snapshot.Normal))
        {
            HideOverlay();
            return;
        }

        bool allowed = _settings.OverlayShowMode switch
        {
            "blacklist" => !(_snapshot.Pid != _ownPid && _rules.MatchDisplay(_settings.OverlayRules.Where(r => r.Trigger == "hide"), _snapshot)),
            "whitelist" => _snapshot.Pid == _ownPid || _rules.MatchDisplay(_settings.OverlayRules.Where(r => r.Trigger == "show"), _snapshot),
            _ => true,
        };
        if (!allowed)
        {
            HideOverlay();
            return;
        }

        ShowOverlay(state);
    }

    private void ShowOverlay(string state)
    {
        HideOverlay();
        if (!_settings.OverlayStyles.TryGetValue(state, out var style) || string.IsNullOrEmpty(style.Text))
        {
            return;
        }

        var layout = WindowProbe.CaptureScreens();
        var focus = layout.FromRect(_snapshot.Rect);
        var windowRect = WindowProbe.GetPhysicalRect(_snapshot.Hwnd);
        if (!_overlayWindows.TryGetValue(state, out var windows))
        {
            windows = new OverlayWindow[Math.Max(1, layout.Monitors.Count)];
            for (int i = 0; i < windows.Length; i++)
            {
                windows[i] = new OverlayWindow(_settings.OverlayEdgeStyle, _settings.OverlayCornerPreference);
            }
            _overlayWindows[state] = windows;
        }

        foreach (var monitor in layout.Monitors)
        {
            if (_settings.OverlayOnlyFocusScreen && focus is not null && monitor.Index != focus.Index)
            {
                continue;
            }
            OverlayWindow window = windows[Math.Clamp(monitor.Index - 1, 0, windows.Length - 1)];
            var (width, height) = window.Measure(style, monitor.Scale);
            int offsetX = WindowProbe.ToPhysical(style.OffsetX, monitor.Scale);
            int offsetY = WindowProbe.ToPhysical(style.OffsetY, monitor.Scale);
            var (x, y) = ComputeOverlayPosition(style.BasePosition, monitor, windowRect, width, height);
            window.ShowAt(style, x + offsetX, y + offsetY, monitor.Scale);
            if (style.BasePosition.Contains("Window", StringComparison.Ordinal))
            {
                break;
            }
        }

        int delay = Math.Max(0, _settings.OverlayHideDelay);
        if (delay > 0)
        {
            Schedule(delay, HideOverlay);
        }
    }

    private static (int X, int Y) ComputeOverlayPosition(string basePosition, MonitorInfo monitor,
        Win32.RECT windowRect, int width, int height)
    {
        int left = monitor.Bounds.Left, top = monitor.Bounds.Top;
        int right = monitor.Bounds.Right, bottom = monitor.Bounds.Bottom;
        int workLeft = monitor.Work.Left, workTop = monitor.Work.Top;
        int workRight = monitor.Work.Right, workBottom = monitor.Work.Bottom;
        int winX = windowRect.Left, winY = windowRect.Top, winW = windowRect.Width, winH = windowRect.Height;

        return basePosition switch
        {
            "top" => (left + (right - left) / 2 - width / 2, top),
            "bottom" => (left + (right - left) / 2 - width / 2, bottom - height),
            "left" => (left, top + (bottom - top) / 2 - height / 2),
            "right" => (right - width, top + (bottom - top) / 2 - height / 2),
            "topLeft" => (left, top),
            "topRight" => (right - width, top),
            "bottomLeft" => (left, bottom - height),
            "bottomRight" => (right - width, bottom - height),
            "bottomWorkArea" => (workLeft + (workRight - workLeft) / 2 - width / 2, workBottom - height),
            "bottomLeftWorkArea" => (workLeft, workBottom - height),
            "bottomRightWorkArea" => (workRight - width, workBottom - height),
            "topWindow" => (winX + winW / 2 - width / 2, winY),
            "bottomWindow" => (winX + winW / 2 - width / 2, winY + winH - height),
            "leftWindow" => (winX, winY + winH / 2 - height / 2),
            "rightWindow" => (winX + winW - width, winY + winH / 2 - height / 2),
            "topLeftWindow" => (winX, winY),
            "topRightWindow" => (winX + winW - width, winY),
            "bottomLeftWindow" => (winX, winY + winH - height),
            "bottomRightWindow" => (winX + winW - width, winY + winH - height),
            "centerWindow" => (winX + winW / 2 - width / 2, winY + winH / 2 - height / 2),
            _ => (left + (right - left) / 2 - width / 2, top + (bottom - top) / 2 - height / 2),
        };
    }

    private void HideOverlay()
    {
        foreach (var windows in _overlayWindows.Values)
        {
            foreach (var window in windows)
            {
                window.Hide();
            }
        }
    }

    // ------------------------------------------------------------------
    // 提示：窗口边框
    // ------------------------------------------------------------------

    private void UpdateBorder(string state, long now)
    {
        if (!_settings.BorderActive)
        {
            if (_borderFingerprint != "")
            {
                ResetBorder();
                _borderFingerprint = "";
            }
            return;
        }

        bool pinned = ((long)Win32.GetWindowLongPtr(_snapshot.Hwnd, Win32.GWL_EXSTYLE) & 0x8) != 0;
        var style = _settings.BorderStyles.TryGetValue(state, out var s) ? s : new BorderStyle();
        string colorText = pinned ? _settings.BorderColorPinned : style.Color;
        int width = pinned ? _settings.BorderWidthPinned : style.Width;

        bool allowShow = !string.IsNullOrWhiteSpace(colorText);
        if (allowShow && !pinned)
        {
            allowShow = _settings.BorderShowMode switch
            {
                "blacklist" => !(_snapshot.Pid != _ownPid && _rules.MatchDisplay(_settings.BorderRules.Where(r => r.Trigger == "hide"), _snapshot)),
                "whitelist" => _snapshot.Pid == _ownPid || _rules.MatchDisplay(_settings.BorderRules.Where(r => r.Trigger == "show"), _snapshot),
                _ => true,
            };
        }

        string fingerprint = $"{colorText}_{width}_{_snapshot.Maximized}_{_snapshot.Fullscreen}_{allowShow}_{_snapshot.Hwnd}";
        bool reshow = fingerprint != _borderFingerprint ||
                      (_settings.BorderReshowOnTitleChange && _hasTitleChange) ||
                      (_settings.BorderReshowOnClassChange && _hasClassChange) ||
                      (_settings.BorderReshowOnProcessChange && _hasProcessChange);
        if (!reshow)
        {
            return;
        }
        _borderFingerprint = fingerprint;

        if (!allowShow ||
            (!_settings.BorderShowOnMaximized && _snapshot.Maximized) ||
            (!_settings.BorderShowOnFullscreen && _snapshot.Fullscreen) ||
            (!_settings.BorderShowOnNormal && _snapshot.Normal))
        {
            ResetBorder();
            return;
        }

        int color = InputTipSettings.ParseColor(colorText, 0);
        ShowBorder(color, Math.Max(1, width), _snapshot);
        if (_settings.BorderHideDelay > 0)
        {
            Schedule(_settings.BorderHideDelay, ResetBorder);
        }
    }

    private void ShowBorder(int color, int width, WindowSnapshot snapshot)
    {
        if (_borderHwnd != IntPtr.Zero && _borderHwnd != snapshot.Hwnd && Win32.IsWindow(_borderHwnd))
        {
            ResetDwmBorder(_borderHwnd);
        }
        _borderHwnd = snapshot.Hwnd;

        if (snapshot.Fullscreen || snapshot.Maximized)
        {
            ResetBorderFrames();
            var layout = WindowProbe.CaptureScreens();
            var monitor = layout.FromRect(snapshot.Rect);
            if (monitor is null)
            {
                return;
            }
            Win32.RECT area = snapshot.Fullscreen ? monitor.Bounds : monitor.Work;
            int w = area.Width, h = area.Height;
            var configs = new (string Side, int X, int Y, int W, int H)[]
            {
                ("Top", area.Left, area.Top, w, width),
                ("Bottom", area.Left, area.Bottom - width, w, width),
                ("Left", area.Left, area.Top + width, width, h - 2 * width),
                ("Right", area.Right - width, area.Top + width, width, h - 2 * width),
            };
            foreach (var (side, x, y, cw, ch) in configs)
            {
                bool show = snapshot.Fullscreen
                    ? side switch
                    {
                        "Top" => _settings.BorderShowOnFullscreenTop,
                        "Bottom" => _settings.BorderShowOnFullscreenBottom,
                        "Left" => _settings.BorderShowOnFullscreenLeft,
                        _ => _settings.BorderShowOnFullscreenRight,
                    }
                    : side switch
                    {
                        "Top" => _settings.BorderShowOnMaximizedTop,
                        "Bottom" => _settings.BorderShowOnMaximizedBottom,
                        "Left" => _settings.BorderShowOnMaximizedLeft,
                        _ => _settings.BorderShowOnMaximizedRight,
                    };
                if (!show || cw <= 0 || ch <= 0)
                {
                    continue;
                }
                var frame = new BorderFrameWindow(color);
                frame.ShowAt(color, x, y, cw, ch);
                _borderFrames.Add(frame);
            }
        }
        else
        {
            ResetBorderFrames();
            int colorRef = (int)Win32.ColorRef(color);
            Win32.DwmSetWindowAttribute(snapshot.Hwnd, Win32.DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
            Win32.DwmSetWindowAttribute(snapshot.Hwnd, Win32.DWMWA_BORDER_THICKNESS, ref width, sizeof(int));
        }
    }

    private void ResetBorder()
    {
        ResetBorderFrames();
        if (_borderHwnd != IntPtr.Zero && Win32.IsWindow(_borderHwnd))
        {
            ResetDwmBorder(_borderHwnd);
        }
        _borderHwnd = IntPtr.Zero;
    }

    private static void ResetDwmBorder(IntPtr hwnd)
    {
        int reset = -1;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_COLOR, ref reset, sizeof(int));
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_THICKNESS, ref reset, sizeof(int));
    }

    private void ResetBorderFrames()
    {
        foreach (var frame in _borderFrames)
        {
            frame.Dispose();
        }
        _borderFrames.Clear();
    }

    // ------------------------------------------------------------------
    // 规则与动作
    // ------------------------------------------------------------------

    private static readonly string[] WindowTriggerKeys =
    [
        "hotkey",
        "switchStateCaps-CapsLock",
        "switchStateCN-IME", "switchStateCN-LShift", "switchStateCN-RShift", "switchStateCN-CtrlSpace",
        "switchStateEN-IME", "switchStateEN-LShift", "switchStateEN-RShift", "switchStateEN-CtrlSpace",
        "switchStateCN/EN-IME", "switchStateCN/EN-LShift", "switchStateCN/EN-RShift", "switchStateCN/EN-CtrlSpace",
        "switchKeyboardCN", "switchKeyboardUS",
        "switchKeyboardJP", "switchKeyboardJPHiragana", "switchKeyboardJPKatakana", "switchKeyboardJPHalfKana",
        "switchKeyboardJPFullAlpha", "switchKeyboardJPHalfAlpha",
        "switchKeyboardKR", "switchKeyboardKRHangul", "switchKeyboardKRAlpha",
        "setWindowTop", "cancelWindowTop", "toggleWindowTop", "exit", "restart", "pause", "resume", "toggle",
        "showStateCode", "showCaptureMode",
        "ignoreStateSwitch", "ignoreKeyboardSwitch",
    ];

    private List<string> ReturnTriggers(WindowSnapshot snapshot)
    {
        var matched = new List<InputTipRule>();
        foreach (string trigger in WindowTriggerKeys)
        {
            matched.AddRange(_rules.MatchWindowRules(
                _settings.WindowRules.Where(r => r.Trigger == trigger), snapshot, _hasProcessChange));
        }

        var bestByGroup = new Dictionary<string, InputTipRule>(StringComparer.Ordinal);
        var nonConflict = new List<InputTipRule>();
        foreach (var rule in matched)
        {
            string group = ConflictGroup(rule.Trigger);
            if (group.Length == 0)
            {
                nonConflict.Add(rule);
            }
            else if (!bestByGroup.TryGetValue(group, out var current) || Priority(rule) > Priority(current))
            {
                bestByGroup[group] = rule;
            }
        }

        var result = bestByGroup.Values.Select(r => r.Trigger).ToList();
        result.AddRange(nonConflict.Select(r => r.Trigger));
        return result;
    }

    private static double Priority(InputTipRule rule)
    {
        double process = string.IsNullOrEmpty(rule.Process) ? 0 : 50;
        double condition = rule.Condition switch
        {
            "classAndTitle" => 20 + rule.Class.Length + rule.Title.Length,
            "class" => 10 + rule.Class.Length,
            "title" => 10 + rule.Title.Length,
            _ => 0,
        };
        return process + condition;
    }

    private static string ConflictGroup(string trigger)
    {
        if (trigger.Contains("switchState", StringComparison.Ordinal)) return "switchState";
        if (trigger.Contains("switchKeyboard", StringComparison.Ordinal)) return "switchKeyboard";
        if (trigger.Contains("ignore", StringComparison.Ordinal)) return "ignore";
        if (trigger.Contains("windowTop", StringComparison.Ordinal)) return "windowTop";
        if (trigger is "exit" or "restart" or "pause" or "resume" or "toggle") return "appControl";
        if (trigger is "showStateCode" or "showCaptureMode") return "show";
        return "";
    }

    public void RunTriggers(IEnumerable<string> triggers)
    {
        foreach (string trigger in triggers)
        {
            try
            {
                ExecuteTrigger(trigger);
            }
            catch
            {
            }
        }
    }

    private void ExecuteTrigger(string trigger)
    {
        switch (trigger)
        {
            case "switchStateCaps-CapsLock": SwitchState("Caps", "{CapsLock}"); break;
            case "switchStateCN-IME": SwitchState("CN", "IME"); break;
            case "switchStateCN-LShift": SwitchState("CN", "{LShift}"); break;
            case "switchStateCN-RShift": SwitchState("CN", "{RShift}"); break;
            case "switchStateCN-CtrlSpace": SwitchState("CN", "{Ctrl Down}{Space Down}{Ctrl Up}{Space Up}"); break;
            case "switchStateEN-IME": SwitchState("EN", "IME"); break;
            case "switchStateEN-LShift": SwitchState("EN", "{LShift}"); break;
            case "switchStateEN-RShift": SwitchState("EN", "{RShift}"); break;
            case "switchStateEN-CtrlSpace": SwitchState("EN", "{Ctrl Down}{Space Down}{Ctrl Up}{Space Up}"); break;
            case "switchStateCN/EN-IME": SwitchState(_state == "CN" ? "EN" : "CN", "IME"); break;
            case "switchStateCN/EN-LShift": SwitchState(_state == "CN" ? "EN" : "CN", "{LShift}"); break;
            case "switchStateCN/EN-RShift": SwitchState(_state == "CN" ? "EN" : "CN", "{RShift}"); break;
            case "switchStateCN/EN-CtrlSpace": SwitchState(_state == "CN" ? "EN" : "CN", "{Ctrl Down}{Space Down}{Ctrl Up}{Space Up}"); break;
            case "switchKeyboardCN": SwitchKeyboard("CN"); break;
            case "switchKeyboardUS": SwitchKeyboard("US"); break;
            case "switchKeyboardJP": SwitchKeyboard("JP"); break;
            case "switchKeyboardJPHiragana": SwitchKeyboard("JP", 1, 9); break;
            case "switchKeyboardJPKatakana": SwitchKeyboard("JP", 1, 11); break;
            case "switchKeyboardJPHalfKana": SwitchKeyboard("JP", 1, 3); break;
            case "switchKeyboardJPFullAlpha": SwitchKeyboard("JP", 1, 8); break;
            case "switchKeyboardJPHalfAlpha": SwitchKeyboard("JP", 0, 0); break;
            case "switchKeyboardKR": SwitchKeyboard("KR"); break;
            case "switchKeyboardKRHangul": SwitchKeyboard("KR", 1, 1); break;
            case "switchKeyboardKRAlpha": SwitchKeyboard("KR", 1, 0); break;
            case "toggle": TogglePause(); break;
            case "pause": SetPaused(true); break;
            case "resume": SetPaused(false); break;
            case "exit": ExitRequested?.Invoke(); break;
            case "restart": RestartRequested?.Invoke(); break;
            case "showStateCode":
                _tooltipCaptureMode = false;
                _tooltipStateCode = !_tooltipStateCode;
                if (!_tooltipStateCode) HideTooltip();
                break;
            case "showCaptureMode":
                _tooltipStateCode = false;
                _tooltipCaptureMode = !_tooltipCaptureMode;
                if (!_tooltipCaptureMode) HideTooltip();
                break;
            case "toggleWindowTop":
            {
                long style = (long)Win32.GetWindowLongPtr(_snapshot.Hwnd, Win32.GWL_EXSTYLE);
                SetTopmost((style & 0x8) == 0);
                break;
            }
            case "setWindowTop": SetTopmost(true); break;
            case "cancelWindowTop": SetTopmost(false); break;
        }
    }

    private void SetTopmost(bool topmost)
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        long style = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        long updated = topmost ? style | 0x8 : style & ~0x8L;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (IntPtr)updated);
        Win32.SetWindowPos(hwnd, topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST,
            0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>切换输入法状态（对齐上游 switchState：延迟 50ms、避让修饰键/鼠标按下、失败重试一次）。</summary>
    private void SwitchState(string target, string method)
    {
        if (_rules.MatchWindowRules(_settings.WindowRules.Where(r => r.Trigger == "ignoreStateSwitch"), _snapshot, true).Count > 0)
        {
            return;
        }
        Schedule(50, () =>
        {
            if (AnyModifierDown() || _ime.IsHwndInitPending ||
                (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0 ||
                (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0)
            {
                return;
            }

            string current = _ime.GetInputModeText(_snapshot, false);
            if (string.IsNullOrEmpty(current) || current == target)
            {
                return;
            }
            if (current == "Caps")
            {
                if (_settings.KeepCapsLockWhenStateSwitch)
                {
                    return;
                }
                ImeService.SendKeys("{CapsLock}");
                Thread.Sleep(50);
                current = _ime.GetInputModeText(_snapshot, false);
                if (string.IsNullOrEmpty(current) || current == target)
                {
                    return;
                }
            }
            else if (target == "Caps")
            {
                ImeService.SendKeys("{CapsLock}");
                return;
            }

            if (method == "IME")
            {
                _ime.SetInputMode(target);
            }
            else
            {
                ImeService.SendKeys(method);
            }

            Schedule(200, () =>
            {
                string after = _ime.GetInputModeText(_snapshot, false);
                if (!string.IsNullOrEmpty(after) && after != target)
                {
                    if (method == "IME")
                    {
                        _ime.SetInputMode(target);
                    }
                    else
                    {
                        ImeService.SendKeys(method);
                    }
                }
            });
        });
    }

    private void SwitchKeyboard(string state, int? opened = null, int? conversionMode = null)
    {
        if (_rules.MatchWindowRules(_settings.WindowRules.Where(r => r.Trigger == "ignoreKeyboardSwitch"), _snapshot, true).Count > 0)
        {
            return;
        }
        if (opened is null && conversionMode is null &&
            _ime.GetInputModeText(_snapshot, false) == "Caps" &&
            !_settings.KeepCapsLockWhenKeyboardSwitch)
        {
            ImeService.SendKeys("{CapsLock}");
        }
        if (opened is not null || conversionMode is not null)
        {
            _ime.SetInputMode(state, opened, conversionMode);
        }
        else
        {
            ImeService.SwitchKeyboard(state);
        }
    }

    private static bool AnyModifierDown()
    {
        return (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0 ||
               (Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0 ||
               (Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0 ||
               (Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) != 0 ||
               (Win32.GetAsyncKeyState(Win32.VK_RWIN) & 0x8000) != 0;
    }

    // ------------------------------------------------------------------
    // 监控：空闲计时器 / 文本监控 / 热键监控 / 窗口热键
    // ------------------------------------------------------------------

    private void InitMonitor(WindowSnapshot snapshot)
    {
        // 空闲计时器
        ClearIdleTimer();
        foreach (var rule in _rules.ProcessRules(_settings.WindowRules.Where(r => r.Condition == "idleTimer"),
                     snapshot.Process, includeDefault: false))
        {
            if (!int.TryParse(rule.IdleTimer, out int delay) || delay <= 0)
            {
                continue;
            }
            if (_rules.MatchWindowRules(_settings.WindowRules.Where(r => r.Trigger == rule.Trigger), snapshot, false).Count == 0)
            {
                continue;
            }
            StartIdleTimer(delay, rule.Trigger);
            break;
        }

        // 文本监控
        _activeTextRules.Clear();
        foreach (var rule in _rules.ProcessRules(_settings.WindowRules.Where(r => r.Condition == "textMonitor"),
                     snapshot.Process, includeDefault: false))
        {
            if (!string.IsNullOrEmpty(rule.TextMonitor))
            {
                _activeTextRules.Add(rule);
            }
        }
        _textBuffer.Clear();

        // 热键序列监控
        _activeHotkeyMonitorRules.Clear();
        _hotkeyMonitorBuffer.Clear();
        _hotkeyMonitorMaxLength = 0;
        foreach (var rule in _rules.ProcessRules(_settings.WindowRules.Where(r => r.Condition == "hotkeyMonitor"),
                     snapshot.Process, includeDefault: false))
        {
            if (string.IsNullOrEmpty(rule.HotkeyMonitor))
            {
                continue;
            }
            _activeHotkeyMonitorRules.Add(rule);
            foreach (string sequence in rule.HotkeyMonitor.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                _hotkeyMonitorMaxLength = Math.Max(_hotkeyMonitorMaxLength,
                    sequence.Split('>', StringSplitOptions.RemoveEmptyEntries).Length);
            }
        }
    }

    private void StartIdleTimer(int delay, string trigger)
    {
        _idleTimerDelay = delay;
        _idleTimerTrigger = trigger;
        _idleTimerDue = (long)Win32.GetTickCount64() + delay;
    }

    private void ClearIdleTimer()
    {
        _idleTimerDelay = 0;
        _idleTimerTrigger = "";
        _idleTimerDue = 0;
    }

    private void UpdateWindowHotkeys(WindowSnapshot snapshot)
    {
        _activeHotkeyRules.Clear();
        foreach (var rule in _settings.HotkeyRules)
        {
            if (string.IsNullOrEmpty(rule.Hotkey) || string.IsNullOrEmpty(rule.Trigger))
            {
                continue;
            }
            if (string.IsNullOrEmpty(rule.Process))
            {
                _activeHotkeyRules.Add(rule);
                continue;
            }
            if (string.Equals(rule.Process, snapshot.Process, StringComparison.OrdinalIgnoreCase) ||
                _rules.RegexMatch(snapshot.Process, rule.Process))
            {
                if (_rules.MatchCondition(rule, snapshot))
                {
                    _activeHotkeyRules.Add(rule);
                }
            }
        }
    }

    private void RunScheduled(long now)
    {
        // 空闲计时器
        if (_idleTimerDelay > 0 && now >= _idleTimerDue)
        {
            string trigger = _idleTimerTrigger;
            ClearIdleTimer();
            RunTriggers([trigger]);
            InitMonitor(_snapshot);
        }

        // 光标符号隐藏延迟
        if (_caretVisible && _settings.CaretSymbolHideDelay > 0 &&
            now - _caretShownAt >= _settings.CaretSymbolHideDelay &&
            (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) == 0 &&
            WindowProbe.IdleMilliseconds() >= _settings.CaretSymbolHideDelay)
        {
            _caretSuppressedKey = _caretKey;
            HideCaretWindows();
        }

        // 鼠标符号隐藏延迟
        if (!_cursorSymbolHidden && _settings.CursorSymbolHideDelay > 0 &&
            WindowProbe.IdleMilliseconds() >= _settings.CursorSymbolHideDelay)
        {
            HideCursorSymbol();
        }

        // 状态码/捕获模式提示
        if (_tooltipStateCode || _tooltipCaptureMode)
        {
            UpdateTooltip();
        }
        else
        {
            HideTooltip();
        }

        for (int i = _timers.Count - 1; i >= 0; i--)
        {
            if (now >= _timers[i].Due)
            {
                Action action = _timers[i].Action;
                _timers.RemoveAt(i);
                try
                {
                    action();
                }
                catch
                {
                }
            }
        }
    }

    private void Schedule(long delay, Action action)
    {
        long due = (long)Win32.GetTickCount64() + Math.Max(0, delay);
        lock (_timers)
        {
            _timers.Add((due, action));
        }
    }

    private void UpdateTooltip()
    {
        _tooltip ??= new TooltipWindow();
        Win32.GetCursorPos(out Win32.POINT cursor);
        string text = _tooltipStateCode
            ? BuildStateCodeText()
            : " 捕获模式: " + (string.IsNullOrEmpty(_lastCaptureMode) ? "无" : _lastCaptureMode);
        _tooltip.ShowText(text, cursor.X + 16, cursor.Y + 20, 1.0, _settings.MenuFontSize > 12 ? 13 : _settings.MenuFontSize);
    }

    private string BuildStateCodeText()
    {
        var (opened, conversion) = _ime.CheckInputMode(_snapshot.FocusedHwnd != IntPtr.Zero ? _snapshot.FocusedHwnd : _snapshot.Hwnd);
        return $" 状态码: {opened}\n 转换码: {conversion}";
    }

    private void HideTooltip() => _tooltip?.Hide();

    // ------------------------------------------------------------------
    // 键盘钩子
    // ------------------------------------------------------------------

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                int message = (int)wParam;
                bool down = message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                OnKey((int)info.vkCode, down, (int)info.scanCode);
            }
        }
        catch
        {
        }
        return Win32.CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private void OnKey(int vk, bool down, int scanCode)
    {
        if (_paused)
        {
            return;
        }

        // 按键统计
        if (down && _pressedKeys.Add(vk))
        {
            CountKey();
        }
        else if (!down)
        {
            _pressedKeys.Remove(vk);
        }

        // Shift 单键（~LShift Up）
        if (vk is Win32.VK_LSHIFT or Win32.VK_RSHIFT)
        {
            if (down)
            {
                _shiftAloneFlag = true;
                _shiftAloneDown = true;
            }
            else if (_shiftAloneDown)
            {
                _shiftAloneDown = false;
                if (_shiftAloneFlag && _lastKeyVk == vk)
                {
                    _shiftAloneFlag = false;
                    RunHotkeyRules(vk, up: true);
                }
                _shiftAloneFlag = false;
            }
        }

        // 文本监控
        if (down && !IsModifierKey(vk))
        {
            char character = ToCharacter(vk, scanCode);
            if (character != '\0' && !char.IsControl(character))
            {
                _textBuffer.Append(character);
                if (_textBuffer.Length > 50)
                {
                    _textBuffer.Remove(0, _textBuffer.Length - 50);
                }
                foreach (var rule in _activeTextRules)
                {
                    if (_rules.RegexMatch(_textBuffer.ToString(), rule.TextMonitor))
                    {
                        _textBuffer.Clear();
                        RunTriggers([rule.Trigger]);
                        break;
                    }
                }
            }
        }

        // 热键序列监控
        if (down)
        {
            string token = BuildHotkeyToken(vk);
            if (token.Length > 0 && _hotkeyMonitorMaxLength > 0)
            {
                long now = (long)Win32.GetTickCount64();
                if (now - _lastHotkeyMonitorTick > 5000)
                {
                    _hotkeyMonitorBuffer.Clear();
                }
                _lastHotkeyMonitorTick = now;
                _hotkeyMonitorBuffer.Add(token);
                while (_hotkeyMonitorBuffer.Count > _hotkeyMonitorMaxLength)
                {
                    _hotkeyMonitorBuffer.RemoveAt(0);
                }
                foreach (var rule in _activeHotkeyMonitorRules)
                {
                    foreach (string sequence in rule.HotkeyMonitor.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] parts = sequence.Split('>', StringSplitOptions.RemoveEmptyEntries);
                        if (MatchesSequence(_hotkeyMonitorBuffer, parts))
                        {
                            _hotkeyMonitorBuffer.Clear();
                            _textBuffer.Clear();
                            string trigger = rule.Trigger;
                            Schedule(50, () =>
                            {
                                if (!AnyModifierDown())
                                {
                                    RunTriggers([trigger]);
                                }
                            });
                            break;
                        }
                    }
                }
            }
        }

        // 热键规则
        RunHotkeyRules(vk, up: !down);

        if (down)
        {
            _lastKeyVk = vk;
        }
    }

    private bool MatchesSequence(List<string> buffer, string[] sequence)
    {
        if (buffer.Count < sequence.Length)
        {
            return false;
        }
        int offset = buffer.Count - sequence.Length;
        for (int i = 0; i < sequence.Length; i++)
        {
            if (!string.Equals(buffer[offset + i], NormalizeHotkey(sequence[i]), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private void RunHotkeyRules(int vk, bool up)
    {
        foreach (var rule in _activeHotkeyRules)
        {
            if (!HotkeyMatches(rule.Hotkey, vk, up))
            {
                continue;
            }
            long now = (long)Win32.GetTickCount64();
            if (_hotkeyDebounce.TryGetValue(rule.Hotkey, out long last) && now - last < 300)
            {
                continue;
            }
            _hotkeyDebounce[rule.Hotkey] = now;
            RunTriggers([rule.Trigger]);
        }
    }

    private static bool HotkeyMatches(string hotkey, int vk, bool up)
    {
        var spec = ParseHotkey(hotkey);
        if (spec.VirtualKey == 0 || spec.VirtualKey != vk || spec.Up != up)
        {
            return false;
        }
        if ((spec.Modifiers & Win32.MOD_CONTROL) != 0 && (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) == 0) return false;
        if ((spec.Modifiers & Win32.MOD_ALT) != 0 && (Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) == 0) return false;
        if ((spec.Modifiers & Win32.MOD_SHIFT) != 0 && (Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) == 0) return false;
        if ((spec.Modifiers & Win32.MOD_WIN) != 0 &&
            (Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) == 0 &&
            (Win32.GetAsyncKeyState(Win32.VK_RWIN) & 0x8000) == 0)
        {
            return false;
        }
        return true;
    }

    private static (int VirtualKey, uint Modifiers, bool Up) ParseHotkey(string hotkey)
    {
        string text = (hotkey ?? "").Trim();
        if (text.StartsWith('~'))
        {
            text = text[1..].Trim();
        }
        bool up = false;
        if (text.EndsWith(" Up", StringComparison.OrdinalIgnoreCase))
        {
            up = true;
            text = text[..^3].Trim();
        }
        uint modifiers = 0;
        int index = 0;
        while (index < text.Length)
        {
            char c = text[index];
            switch (c)
            {
                case '^': modifiers |= Win32.MOD_CONTROL; index++; continue;
                case '!': modifiers |= Win32.MOD_ALT; index++; continue;
                case '+': modifiers |= Win32.MOD_SHIFT; index++; continue;
                case '#': modifiers |= Win32.MOD_WIN; index++; continue;
            }
            break;
        }
        string keyName = text[index..].Trim();
        return (ImeService.VirtualKeyFromName(keyName), modifiers, up);
    }

    private static string BuildHotkeyToken(int vk)
    {
        var builder = new StringBuilder();
        if ((Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0) builder.Append('^');
        if ((Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0) builder.Append('!');
        if ((Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0) builder.Append('+');
        if ((Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) != 0 ||
            (Win32.GetAsyncKeyState(Win32.VK_RWIN) & 0x8000) != 0) builder.Append('#');

        string key = KeyName(vk);
        if (key.Length == 0)
        {
            return "";
        }
        builder.Append(key);
        return builder.ToString().ToLowerInvariant();
    }

    private static string NormalizeHotkey(string hotkey)
    {
        var spec = ParseHotkey(hotkey);
        var builder = new StringBuilder();
        if ((spec.Modifiers & Win32.MOD_CONTROL) != 0) builder.Append('^');
        if ((spec.Modifiers & Win32.MOD_ALT) != 0) builder.Append('!');
        if ((spec.Modifiers & Win32.MOD_SHIFT) != 0) builder.Append('+');
        if ((spec.Modifiers & Win32.MOD_WIN) != 0) builder.Append('#');
        builder.Append(KeyName(spec.VirtualKey));
        return builder.ToString().ToLowerInvariant();
    }

    private static string KeyName(int vk)
    {
        if (vk is >= 'A' and <= 'Z')
        {
            return ((char)vk).ToString();
        }
        if (vk is >= '0' and <= '9')
        {
            return ((char)vk).ToString();
        }
        return vk switch
        {
            0xBF => "/",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xC0 => "`",
            0x20 => "space",
            0x0D => "enter",
            0x09 => "tab",
            0x1B => "esc",
            0x08 => "backspace",
            0x2E => "delete",
            0x25 => "left",
            0x26 => "up",
            0x27 => "right",
            0x28 => "down",
            0x24 => "home",
            0x23 => "end",
            0x21 => "pgup",
            0x22 => "pgdn",
            0xA0 => "lshift",
            0xA1 => "rshift",
            0x10 => "shift",
            0xA2 => "lctrl",
            0xA3 => "rctrl",
            0x11 => "ctrl",
            0xA4 => "lalt",
            0xA5 => "ralt",
            0x12 => "alt",
            0x5B => "lwin",
            0x5C => "rwin",
            _ => "vk" + vk.ToString("X2"),
        };
    }

    private static bool IsModifierKey(int vk)
        => vk is Win32.VK_SHIFT or Win32.VK_CONTROL or Win32.VK_MENU or Win32.VK_LSHIFT or Win32.VK_RSHIFT
            or Win32.VK_LCONTROL or Win32.VK_RCONTROL or Win32.VK_LMENU or Win32.VK_RMENU or Win32.VK_LWIN or Win32.VK_RWIN;

    private static char ToCharacter(int vk, int scanCode)
    {
        try
        {
            byte[] keyboardState = new byte[256];
            if (!Win32.GetKeyboardState(keyboardState))
            {
                return '\0';
            }
            char[] buffer = new char[8];
            IntPtr layout = Win32.GetKeyboardLayout(Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _));
            int count = Win32.ToUnicodeEx((uint)vk, (uint)scanCode, keyboardState, buffer, buffer.Length, 0, layout);
            return count > 0 ? buffer[0] : '\0';
        }
        catch
        {
            return '\0';
        }
    }

    // ------------------------------------------------------------------
    // 暂停/恢复、托盘提示、按键统计
    // ------------------------------------------------------------------

    private void SuspendNow()
    {
        HideAllTips();
        _cursor.Revert();
        _activeHotkeyRules.RemoveAll(r => r.Trigger is not ("resume" or "toggle"));
        PausedChanged?.Invoke(true);
        UpdateTrayTip();
    }

    private void ResumeNow()
    {
        UpdateWindowHotkeys(_snapshot);
        if (_settings.CursorActive)
        {
            _cursor.Load(_state, change: true);
        }
        PausedChanged?.Invoke(false);
        UpdateTrayTip();
    }

    private void HideAllTips()
    {
        HideOverlay();
        HideCaretSymbol();
        HideCursorSymbol();
        ResetBorder();
        HideTooltip();
        _lastOverlayState = "";
        _borderFingerprint = "";
    }

    private void UpdateTrayTip()
    {
        string template = _settings.EnableCustomTrayTip ? _settings.TrayTipTemplate : "工具坞 · 输入法提示\n%appState%";
        string keyStats = _settings.EnableKeyStats ? _settings.KeyStatsTemplate : "";
        string tip = (template + keyStats)
            .Replace("%appState%", _paused ? "已暂停" : "运行中")
            .Replace("%keyCount%", _keyCount.ToString())
            .Replace("%\\n%", "\n");
        TrayTipChanged?.Invoke(tip);
    }

    private void CountKey()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (today != _keyStatsDate)
        {
            _keyStatsDate = today;
            _keyCount = 0;
        }
        _keyCount++;
        long now = (long)Win32.GetTickCount64();
        if (now - _lastKeyStatsSave > 30_000)
        {
            _lastKeyStatsSave = now;
            SaveKeyStats();
        }
    }

    private string KeyStatsPath => Path.Combine(_settings.ConfigDirectory, "stats.txt");

    private void LoadKeyStats()
    {
        try
        {
            if (File.Exists(KeyStatsPath))
            {
                string[] lines = File.ReadAllLines(KeyStatsPath);
                if (lines.Length >= 2 && lines[0] == _keyStatsDate && long.TryParse(lines[1], out long count))
                {
                    _keyCount = count;
                }
            }
        }
        catch
        {
        }
    }

    private void SaveKeyStats()
    {
        try
        {
            File.WriteAllText(KeyStatsPath, _keyStatsDate + "\n" + _keyCount);
        }
        catch
        {
        }
    }

    private static bool IsCapsLockOn()
    {
        byte[] state = new byte[256];
        return Win32.GetKeyboardState(state) && (state[Win32.VK_CAPITAL] & 1) != 0;
    }
}

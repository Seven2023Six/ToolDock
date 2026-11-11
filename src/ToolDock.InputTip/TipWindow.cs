using System.Runtime.InteropServices;

namespace ToolDock.InputTip;

/// <summary>
/// 分层提示窗口基类：无边框、置顶、不抢焦点、鼠标穿透。
/// 用 GDI 绘制内容，LWA_ALPHA 控制整体透明度，LWA_COLORKEY 控制抠色（图片符号）。
/// </summary>
internal abstract class LayeredWindow : IDisposable
{
    private const string ClassName = "ToolDock.InputTip.LayeredWindow";
    private static ushort _classAtom;
    private static Win32.WndProc? _wndProc;
    private static readonly Dictionary<IntPtr, LayeredWindow> Instances = new();

    protected IntPtr Handle { get; private set; }
    protected int Width { get; private set; }
    protected int Height { get; private set; }
    protected byte Alpha { get; private set; } = 255;
    protected uint ColorKey { get; private set; }
    protected bool UseColorKey { get; private set; }
    protected double Scale { get; private set; } = 1.0;

    protected LayeredWindow(int extraExStyle = 0)
    {
        EnsureClass();
        int exStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST |
                      Win32.WS_EX_NOACTIVATE | extraExStyle;
        Handle = Win32.CreateWindowExW(exStyle, ClassName, "", Win32.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (Handle != IntPtr.Zero)
        {
            Instances[Handle] = this;
        }
        else
        {
            InputTipDiagnostics.Log?.Invoke($"创建提示窗口失败，错误码={Marshal.GetLastWin32Error()}");
        }
    }

    public bool IsValid => Handle != IntPtr.Zero;
    public bool IsVisible { get; private set; }

    private static void EnsureClass()
    {
        if (_classAtom != 0)
        {
            return;
        }
        _wndProc = WndProc;
        var windowClass = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Win32.GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        _classAtom = Win32.RegisterClassExW(ref windowClass);
        if (_classAtom == 0)
        {
            InputTipDiagnostics.Log?.Invoke($"注册窗口类失败，错误码={Marshal.GetLastWin32Error()}");
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hwnd, out var window))
        {
            try
            {
                return window.HandleMessage(msg, wParam, lParam);
            }
            catch (Exception exception)
            {
                // 窗口过程里逃逸的异常会直接终止整个 Agent 进程
                InputTipDiagnostics.Log?.Invoke("提示窗口消息异常：" + exception.Message);
            }
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
                return (IntPtr)Win32.HTTRANSPARENT;
            case Win32.WM_MOUSEACTIVATE:
                return (IntPtr)Win32.MA_NOACTIVATE;
            case Win32.WM_ERASEBKGND:
                return (IntPtr)1;
            case Win32.WM_PAINT:
                Paint();
                return IntPtr.Zero;
            default:
                return Win32.DefWindowProcW(Handle, msg, wParam, lParam);
        }
    }

    private void Paint()
    {
        IntPtr dc = Win32.BeginPaint(Handle, out Win32.PAINTSTRUCT paint);
        try
        {
            Win32.GetClientRect(Handle, out Win32.RECT client);
            try
            {
                OnPaint(dc, client);
            }
            catch (Exception exception)
            {
                InputTipDiagnostics.Log?.Invoke("提示窗口绘制异常：" + exception.Message);
            }
        }
        finally
        {
            Win32.EndPaint(Handle, ref paint);
        }
    }

    protected abstract void OnPaint(IntPtr dc, Win32.RECT client);

    protected void SetTransparency(byte alpha, uint colorKey, bool useColorKey)
    {
        Alpha = alpha;
        ColorKey = colorKey;
        UseColorKey = useColorKey;
        uint flags = Win32.LWA_ALPHA | (useColorKey ? Win32.LWA_COLORKEY : 0u);
        Win32.SetLayeredWindowAttributes(Handle, colorKey, alpha, flags);
    }

    protected void SetCornerPreference(int preference)
    {
        if (preference <= 0)
        {
            return;
        }
        int value = preference;
        Win32.DwmSetWindowAttribute(Handle, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    protected void SetEdgeStyle(int style)
    {
        int extra = style switch
        {
            1 => Win32.WS_EX_DLGMODALFRAME,
            2 => Win32.WS_EX_CLIENTEDGE,
            3 => Win32.WS_EX_STATICEDGE,
            _ => 0,
        };
        if (extra == 0)
        {
            return;
        }
        long current = (long)Win32.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(Handle, Win32.GWL_EXSTYLE, (IntPtr)(current | (long)extra));
    }

    public void Show(int x, int y, int width, int height, double scale = 1.0)
    {
        if (!IsValid)
        {
            return;
        }
        Scale = scale;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, x, y, Width, Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        Win32.InvalidateRect(Handle, IntPtr.Zero, true);
        Win32.UpdateWindow(Handle);
        IsVisible = true;
    }

    public void Move(int x, int y)
    {
        if (!IsValid)
        {
            return;
        }
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, x, y, 0, 0,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);
    }

    public void Hide()
    {
        if (!IsValid)
        {
            return;
        }
        Win32.ShowWindow(Handle, Win32.SW_HIDE);
        IsVisible = false;
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Instances.Remove(Handle);
            Win32.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
        IsVisible = false;
    }

    // ---- 绘制辅助 ----

    protected static IntPtr CreateUiFont(int pointSize, int weight, string family, double scale)
    {
        int dpi = (int)Math.Round(96 * scale);
        int height = -MulDiv(pointSize, dpi, 72);
        return Win32.CreateFontW(height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, family);
    }

    private static int MulDiv(int number, int numerator, int denominator)
        => (int)Math.Round((double)number * numerator / denominator);

    protected static Win32.SIZE MeasureText(string text, int pointSize, int weight, string family, double scale)
    {
        IntPtr dc = Win32.GetDC(IntPtr.Zero);
        IntPtr font = CreateUiFont(pointSize, weight, family, scale);
        IntPtr previous = Win32.SelectObject(dc, font);
        try
        {
            Win32.GetTextExtentPoint32W(dc, text, text.Length, out Win32.SIZE size);
            return size;
        }
        finally
        {
            Win32.SelectObject(dc, previous);
            Win32.DeleteObject(font);
            Win32.ReleaseDC(IntPtr.Zero, dc);
        }
    }

    /// <summary>把窗口裁成圆角（圆角半径按像素给），用于悬浮小窗/符号/提示的胶囊外观。</summary>
    protected void SetRoundedRegion(int radius)
    {
        if (!IsValid || Width <= 0 || Height <= 0)
        {
            return;
        }
        int value = Math.Max(0, Math.Min(radius, Math.Min(Width, Height) / 2));
        if (value <= 0)
        {
            return;
        }
        IntPtr region = Win32.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, value * 2, value * 2);
        if (region != IntPtr.Zero)
        {
            Win32.SetWindowRgn(Handle, region, true);
        }
    }

    /// <summary>圆角描边（1px），给胶囊加一条细微的明暗边。</summary>
    protected static void DrawRoundedBorder(IntPtr dc, Win32.RECT rect, int radius, int rgb)
    {
        IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, 1, Win32.ColorRef(rgb));
        IntPtr previousPen = Win32.SelectObject(dc, pen);
        IntPtr previousBrush = Win32.SelectObject(dc, Win32.GetStockObject(Win32.NULL_BRUSH));
        try
        {
            Win32.RoundRect(dc, rect.Left + 1, rect.Top + 1, rect.Right - 1, rect.Bottom - 1, radius * 2, radius * 2);
        }
        finally
        {
            Win32.SelectObject(dc, previousBrush);
            Win32.SelectObject(dc, previousPen);
            Win32.DeleteObject(pen);
        }
    }

    protected static int Blend(int rgb, int with, double ratio)
    {
        int r = (int)Math.Round(((rgb >> 16) & 0xFF) * (1 - ratio) + ((with >> 16) & 0xFF) * ratio);
        int g = (int)Math.Round(((rgb >> 8) & 0xFF) * (1 - ratio) + ((with >> 8) & 0xFF) * ratio);
        int b = (int)Math.Round((rgb & 0xFF) * (1 - ratio) + (with & 0xFF) * ratio);
        return (r << 16) | (g << 8) | b;
    }

    protected static void Fill(IntPtr dc, Win32.RECT rect, int rgb)
    {
        IntPtr brush = Win32.CreateSolidBrush(Win32.ColorRef(rgb));
        Win32.FillRect(dc, ref rect, brush);
        Win32.DeleteObject(brush);
    }

    protected static void DrawTextCentered(IntPtr dc, string text, Win32.RECT rect, int rgb,
        int pointSize, int weight, string family, double scale)
    {
        IntPtr font = CreateUiFont(pointSize, weight, family, scale);
        IntPtr previous = Win32.SelectObject(dc, font);
        try
        {
            Win32.SetBkMode(dc, Win32.TRANSPARENT);
            Win32.SetTextColor(dc, Win32.ColorRef(rgb));
            Win32.DrawTextW(dc, text, text.Length, ref rect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        }
        finally
        {
            Win32.SelectObject(dc, previous);
            Win32.DeleteObject(font);
        }
    }

    /// <summary>多行文本（保留换行符），用于状态码/捕获模式提示。</summary>
    protected static void DrawTextBlock(IntPtr dc, string text, Win32.RECT rect, int rgb,
        int pointSize, int weight, string family, double scale)
    {
        IntPtr font = CreateUiFont(pointSize, weight, family, scale);
        IntPtr previous = Win32.SelectObject(dc, font);
        try
        {
            Win32.SetBkMode(dc, Win32.TRANSPARENT);
            Win32.SetTextColor(dc, Win32.ColorRef(rgb));
            Win32.DrawTextW(dc, text, text.Length, ref rect, Win32.DT_LEFT | Win32.DT_NOPREFIX);
        }
        finally
        {
            Win32.SelectObject(dc, previous);
            Win32.DeleteObject(font);
        }
    }
}

/// <summary>悬浮小窗：一个状态一块窗口，每个显示器各一块。</summary>
internal sealed class OverlayWindow : LayeredWindow
{
    private OverlayStyle _style = new();

    public OverlayWindow(int edgeStyle, int cornerPreference) : base(0)
    {
        SetEdgeStyle(edgeStyle);
        SetCornerPreference(cornerPreference);
    }

    private static (int PadX, int PadY) Padding(double scale)
        => ((int)Math.Round(14 * scale), (int)Math.Round(7 * scale));

    public (int Width, int Height) Measure(OverlayStyle style, double scale)
    {
        _style = style;
        string text = style.Text ?? "";
        var size = text.Length == 0 ? new Win32.SIZE() : MeasureText(text, style.Size, style.Weight, style.Font, scale);
        var (padX, padY) = Padding(scale);
        return (Math.Max(1, size.cx + padX * 2), Math.Max(1, size.cy + padY * 2));
    }

    public void ShowAt(OverlayStyle style, int x, int y, double scale)
    {
        _style = style;
        var (width, height) = Measure(style, scale);
        bool useColorKey = string.IsNullOrWhiteSpace(style.BgColor);
        SetTransparency((byte)Math.Clamp(style.Transparent, 0, 255), useColorKey ? 0x00010101u : 0, useColorKey);
        Show(x, y, width, height, scale);
        if (!useColorKey)
        {
            SetRoundedRegion(height / 2);
        }
    }

    protected override void OnPaint(IntPtr dc, Win32.RECT client)
    {
        int bg = InputTipSettings.ParseColor(_style.BgColor, 0);
        bool hasBackground = !string.IsNullOrWhiteSpace(_style.BgColor);
        Fill(dc, client, bg);
        if (hasBackground)
        {
            DrawRoundedBorder(dc, client, client.Height / 2, Blend(bg, 0x000000, 0.28));
        }
        var (padX, padY) = Padding(Scale);
        var textRect = new Win32.RECT
        {
            Left = client.Left + padX,
            Top = client.Top + padY,
            Right = client.Right - padX,
            Bottom = client.Bottom - padY,
        };
        int color = InputTipSettings.ParseColor(_style.TextColor, 0xFFFFFF);
        DrawTextCentered(dc, _style.Text ?? "", textRect, color, _style.Size, _style.Weight, _style.Font, Scale);
    }
}

internal enum SymbolKind
{
    Picture = 1,
    Shape = 2,
    Text = 3,
}

/// <summary>光标符号 / 鼠标符号窗口（图片、方块、文字）。</summary>
internal sealed class SymbolWindow : LayeredWindow
{
    private readonly SymbolKind _kind;
    private SymbolStyle _style = new();
    private PngDecoder.Bitmap? _bitmap;
    private string _loadedPath = "";
    private byte[]? _composited;

    public SymbolWindow(SymbolKind kind, int edgeStyle, int cornerPreference) : base(0)
    {
        _kind = kind;
        SetEdgeStyle(edgeStyle);
        SetCornerPreference(cornerPreference);
    }

    public SymbolKind Kind => _kind;

    public (int Width, int Height) Measure(SymbolStyle style, double scale)
    {
        _style = style;
        switch (_kind)
        {
            case SymbolKind.Picture:
                return (Math.Max(1, (int)Math.Round(style.PictureWidth * scale)),
                        Math.Max(1, (int)Math.Round(style.PictureHeight * scale)));
            case SymbolKind.Shape:
                return (Math.Max(1, (int)Math.Round(style.ShapeWidth * scale)),
                        Math.Max(1, (int)Math.Round(style.ShapeHeight * scale)));
            default:
            {
                string text = style.TextContent ?? "";
                var size = text.Length == 0
                    ? new Win32.SIZE()
                    : MeasureText(text, style.TextSize, style.TextWeight, style.TextFont, scale);
                int padX = (int)Math.Round(7 * scale);
                int padY = (int)Math.Round(3 * scale);
                return (Math.Max(1, size.cx + padX * 2), Math.Max(1, size.cy + padY * 2));
            }
        }
    }

    public void ShowAt(SymbolStyle style, int x, int y, double scale, string symbolDirectory, string defaultDirectory)
    {
        _style = style;
        var (width, height) = Measure(style, scale);
        switch (_kind)
        {
            case SymbolKind.Picture:
            {
                string path = ResolvePicture(style.PicturePath, symbolDirectory, defaultDirectory);
                if (!string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase) || _composited is null)
                {
                    _loadedPath = path;
                    _bitmap = PngDecoder.Decode(path);
                    _composited = _bitmap is null
                        ? null
                        : PngDecoder.CompositeOnBackground(_bitmap, 0x010101);
                }
                SetTransparency(255, 0x00010101u, useColorKey: true);
                break;
            }
            case SymbolKind.Shape:
                SetTransparency((byte)Math.Clamp(style.ShapeTransparent, 0, 255), 0, useColorKey: false);
                break;
            default:
                SetTransparency((byte)Math.Clamp(style.TextTransparent, 0, 255), 0, useColorKey: false);
                break;
        }
        Show(x, y, width, height, scale);
        if (_kind == SymbolKind.Text)
        {
            SetRoundedRegion(height / 2);
        }
        else if (_kind == SymbolKind.Shape)
        {
            SetRoundedRegion((int)Math.Round(6 * scale));
        }
    }

    private static string ResolvePicture(string name, string symbolDirectory, string defaultDirectory)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }
        if (Path.IsPathRooted(name))
        {
            return name;
        }
        string custom = Path.Combine(symbolDirectory, name);
        if (File.Exists(custom))
        {
            return custom;
        }
        return Path.Combine(defaultDirectory, name);
    }

    protected override void OnPaint(IntPtr dc, Win32.RECT client)
    {
        switch (_kind)
        {
            case SymbolKind.Picture:
                PaintPicture(dc, client);
                break;
            case SymbolKind.Shape:
            {
                int color = InputTipSettings.ParseColor(_style.ShapeColor, 0);
                Fill(dc, client, color);
                break;
            }
            default:
            {
                int bg = InputTipSettings.ParseColor(_style.TextBgColor, 0);
                Fill(dc, client, bg);
                if (!string.IsNullOrWhiteSpace(_style.TextBgColor))
                {
                    DrawRoundedBorder(dc, client, client.Height / 2, Blend(bg, 0x000000, 0.28));
                }
                int color = InputTipSettings.ParseColor(_style.TextColor, 0xFFFFFF);
                DrawTextCentered(dc, _style.TextContent ?? "", client, color,
                    _style.TextSize, _style.TextWeight, _style.TextFont, Scale);
                break;
            }
        }
    }

    private void PaintPicture(IntPtr dc, Win32.RECT client)
    {
        Fill(dc, client, 0x010101);
        if (_composited is null || _bitmap is null)
        {
            return;
        }
        var info = new Win32.BITMAPINFO
        {
            bmiHeader = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = _bitmap.Width,
                biHeight = -_bitmap.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        IntPtr buffer = Marshal.AllocHGlobal(_composited.Length);
        try
        {
            Marshal.Copy(_composited, 0, buffer, _composited.Length);
            Win32.StretchDIBits(dc, 0, 0, client.Width, client.Height,
                0, 0, _bitmap.Width, _bitmap.Height, buffer, ref info, 0, 0x00CC0020 /* SRCCOPY */);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>最大化 / 全屏窗口的四条边框（普通窗口走 DWM，不需要这个）。</summary>
internal sealed class BorderFrameWindow : LayeredWindow
{
    private int _color;

    public BorderFrameWindow(int color) : base(0)
    {
        _color = color;
        SetTransparency(255, 0, useColorKey: false);
    }

    public void ShowAt(int color, int x, int y, int width, int height)
    {
        _color = color;
        Show(x, y, width, height);
    }

    protected override void OnPaint(IntPtr dc, Win32.RECT client)
        => Fill(dc, client, _color);
}

/// <summary>状态码 / 捕获模式提示（跟随鼠标的两行小窗）。</summary>
internal sealed class TooltipWindow : LayeredWindow
{
    private string _text = "";
    private int _fontSize = 13;

    public TooltipWindow() : base(0)
    {
        SetCornerPreference(2);
    }

    public void ShowText(string text, int x, int y, double scale, int fontSize = 13)
    {
        _text = text;
        _fontSize = fontSize;
        int width = 0, height = 0;
        foreach (string line in text.Split('\n'))
        {
            var size = MeasureText(line, _fontSize, 400, "Microsoft YaHei", scale);
            width = Math.Max(width, size.cx);
            height += size.cy;
        }
        int padding = (int)Math.Round(8 * scale);
        SetTransparency(235, 0, useColorKey: false);
        Show(x, y, Math.Max(width, 80) + padding * 2, height + padding, scale);
        SetRoundedRegion((int)Math.Round(6 * scale));
    }

    protected override void OnPaint(IntPtr dc, Win32.RECT client)
    {
        Fill(dc, client, 0x1F1F1F);
        var rect = client;
        rect.Left += (int)Math.Round(8 * Scale);
        rect.Top += (int)Math.Round(4 * Scale);
        rect.Right -= (int)Math.Round(4 * Scale);
        DrawTextBlock(dc, _text, rect, 0xF0F0F0, _fontSize, 400, "Microsoft YaHei", Scale);
    }
}

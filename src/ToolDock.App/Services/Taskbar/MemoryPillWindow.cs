using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Gdi = System.Drawing;
using Gdi2 = System.Drawing.Drawing2D;
using GdiImaging = System.Drawing.Imaging;
using ToolDock.Core;

namespace ToolDock.App.Services;

/// <summary>
/// 任务栏内存贴片：与电量贴片同款做法——原生分层窗口 SetParent 到任务栏（Shell_TrayWnd 的子窗口），
/// 用 UpdateLayeredWindow 提交 32 位 ARGB 位图。位置默认排在电量贴片**左边并留出间隔**，
/// 点击贴片直接触发内存清理，右键弹出菜单。
/// </summary>
internal sealed class MemoryPillWindow : IDisposable
{
    private const string ClassName = "ToolDockMemoryPill";
    private const string WindowTitle = "工具坞 内存";
    private const int FollowIntervalMs = 1000;
    private const double MinHeightDip = 22;
    private const double MaxHeightDip = 40;
    private const double TaskbarMarginDip = 6;
    private const double EdgeGapDip = 6;      // 距任务栏右端（无电量贴片时）
    private const double SeparationDip = 10;  // 与电量贴片之间的间隔

    private static readonly Gdi.Bitmap MeasureBmp = new(1, 1, GdiImaging.PixelFormat.Format32bppPArgb);
    private static readonly Gdi.Graphics MeasureG = Gdi.Graphics.FromImage(MeasureBmp);

    private static MemoryPillWindow? _current;
    private static bool _classRegistered;

    private readonly Action _onClick;
    private readonly Action _onHide;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private IntPtr _taskbar;
    private Gdi.Bitmap? _bitmap;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldDib;
    private IntPtr _dibBits;
    private int _dibW;
    private int _dibH;
    private int _pillW = 80;
    private int _pillH = 30;
    private int _lastPercent = -1;
    private bool _hidden;
    private WndProcDelegate? _wndProc;

    public MemoryPillWindow(IntPtr taskbar, Action onClick, Action onHide)
    {
        _current?.Dispose();
        _current = this;
        _taskbar = taskbar;
        _onClick = onClick;
        _onHide = onHide;

        EnsureWindow();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FollowIntervalMs) };
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Update();
    }

    public bool IsWindowValid => _hwnd != IntPtr.Zero && Native.IsWindow(_hwnd);

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd))
        {
            return;
        }
        ReleaseWindow();

        RegisterClassOnce();
        var hInstance = Native.GetModuleHandleW(null);
        _hwnd = Native.CreateWindowExW(
            (uint)(Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
            ClassName, WindowTitle, Native.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Diag("create failed err=" + Marshal.GetLastWin32Error());
            return;
        }

        Native.SetParent(_hwnd, _taskbar);
        Native.ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
    }

    private void RegisterClassOnce()
    {
        if (_classRegistered)
        {
            return;
        }
        _classRegistered = true;
        _wndProc = WndProc;
        var wc = new Native.WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Native.GetModuleHandleW(null),
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };
        Native.RegisterClassW(ref wc);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_LBUTTONUP:
                _onClick();
                return IntPtr.Zero;
            case Native.WM_RBUTTONUP:
                ShowMenu();
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = Native.CreatePopupMenu();
        try
        {
            Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)1, "立即清理内存");
            Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)2, "打开内存清理");
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
            Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)3, "隐藏内存贴片");

            var pt = Native.GetCursorPos();
            Native.SetForegroundWindow(_hwnd);
            int cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            Native.PostMessageW(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case 1:
                    _onClick();
                    break;
                case 2:
                    MemoryPillService.OpenMemCleanPage?.Invoke();
                    break;
                case 3:
                    _onHide();
                    break;
            }
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    /// <summary>每秒重算位置并刷新百分比。</summary>
    private void Update()
    {
        try
        {
            UpdateCore();
        }
        catch (Exception exception)
        {
            Diag("update failed: " + exception.GetType().Name + " " + exception.Message);
        }
    }

    private void UpdateCore()
    {
        if (_hwnd == IntPtr.Zero || _hidden)
        {
            return;
        }
        if (!Native.IsWindow(_taskbar))
        {
            _taskbar = Native.FindWindowW(Native.TaskbarClassName, null);
            if (_taskbar == IntPtr.Zero)
            {
                return;
            }
            Native.SetParent(_hwnd, _taskbar);
        }
        if (!Native.GetWindowRect(_taskbar, out var taskbarRect))
        {
            return;
        }

        var taskbar = new PixelRect(taskbarRect.Left, taskbarRect.Top,
            taskbarRect.Right - taskbarRect.Left, taskbarRect.Bottom - taskbarRect.Top);
        if (TaskbarLayout.IsTaskbarHidden(taskbar))
        {
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            return;
        }

        uint dpi = Native.GetDpiForWindow(_taskbar);
        if (dpi == 0)
        {
            dpi = 96;
        }
        double scale = dpi / 96.0;

        int percent = (int)(CleanupCore.ReadMemorySnapshot()?.LoadPercent ?? 0);
        bool repaint = percent != _lastPercent || _bitmap is null;

        _pillH = TaskbarLayout.PillHeight(taskbar, scale, TaskbarMarginDip, MinHeightDip, MaxHeightDip);

        // 先渲染（渲染时按文字实际宽度算出 _pillW），再定位——顺序不能反
        if (repaint)
        {
            Render(percent, scale);
            _lastPercent = percent;
        }

        var (x, y) = ComputePosition(taskbar, scale);
        // 子窗口坐标相对父窗口客户区：定位数学算的是屏幕坐标，这里换算一次
        var pt = new Native.POINT { X = x, Y = y };
        Native.ScreenToClient(_taskbar, ref pt);
        Upload(pt.X, pt.Y);
        Native.ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
    }

    /// <summary>优先排在电量贴片左侧并留间隔；没有电量贴片时贴托盘左边。</summary>
    private (int X, int Y) ComputePosition(PixelRect taskbar, double scale)
    {
        int gap = (int)Math.Round(EdgeGapDip * scale);
        int separation = 0;   // 与电量贴片紧贴，分隔交给竖线（视觉上是一条连续胶囊）

        if (TaskbarLayout.IsHorizontal(taskbar))
        {
            var battery = TaskbarPillWindow.CurrentWindowHandle;
                int anchorRight;
            if (battery != IntPtr.Zero && Native.GetWindowRect(battery, out var br))
            {
                anchorRight = br.Left - separation;
            }
            else
            {
                IntPtr tray = Native.FindWindowW(Native.TrayNotifyClassName, null);
                anchorRight = tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr)
                    ? tr.Left - gap
                    : taskbar.Right - gap;
            }
            int x = Math.Clamp(anchorRight - _pillW, taskbar.Left + TaskbarLayout.EdgeInsetPx,
                Math.Max(taskbar.Left + TaskbarLayout.EdgeInsetPx, taskbar.Right - _pillW - TaskbarLayout.EdgeInsetPx));
            int y = taskbar.Top + (taskbar.Height - _pillH) / 2;
            return (x, y);
        }

        // 竖向任务栏：排在电量贴片上方
        var batteryV = TaskbarPillWindow.CurrentWindowHandle;
        int anchorBottom = batteryV != IntPtr.Zero && Native.GetWindowRect(batteryV, out var bv)
            ? bv.Top - separation
            : taskbar.Bottom - gap;
        int yv = Math.Clamp(anchorBottom - _pillH, taskbar.Top + TaskbarLayout.EdgeInsetPx,
            Math.Max(taskbar.Top + TaskbarLayout.EdgeInsetPx, taskbar.Bottom - _pillH - TaskbarLayout.EdgeInsetPx));
        int xv = taskbar.Left + (taskbar.Width - _pillW) / 2;
        return (xv, yv);
    }

    // 与电量贴片同源尺寸（TaskbarPillRenderer 的常量）
    private const double PadH = 6;
    private const double IconBoxDip = 15;
    private const double IconTextGapDip = 4;
    private const double PercentFontSizeDip = 13;

    /// <summary>宽度余量（像素）：与电量贴片同因同解——量出来的字宽比实际绘制窄，不补就会截掉 "%"。</summary>
    private const int SlackPx = 8;

    private void Render(int percent, double scale)
    {
        _bitmap?.Dispose();

        float s = (float)scale;
        float pad = (float)(PadH * scale);
        float iconSize = (float)(IconBoxDip * scale);
        float gap = (float)(IconTextGapDip * scale);
        float fontPx = (float)(PercentFontSizeDip * scale);

        // 字号与电量贴片一致（13dip），同样加粗
        using var font = new Gdi.Font("Microsoft YaHei UI", fontPx, Gdi.FontStyle.Bold, Gdi.GraphicsUnit.Pixel);
        // 测量与绘制用同一个 StringFormat，否则量出来比画出来窄，最右的 "%" 会被窗口裁掉
        using var sf = (Gdi.StringFormat)Gdi.StringFormat.GenericTypographic.Clone();
        sf.FormatFlags |= Gdi.StringFormatFlags.NoWrap | Gdi.StringFormatFlags.MeasureTrailingSpaces;
        sf.LineAlignment = Gdi.StringAlignment.Center;

        string label = percent + "%";
        float textW = MeasureG.MeasureString(label, font, new Gdi.PointF(0, 0), sf).Width;

        // 右端给竖线留 2dip 间距，再加与电量贴片同等的字宽余量（SlackPx）
        _pillW = (int)Math.Ceiling(pad + iconSize + gap + textW + 2 * scale + pad) + SlackPx;

        var bmp = new Gdi.Bitmap(_pillW, _pillH, GdiImaging.PixelFormat.Format32bppPArgb);
        using (var g = Gdi.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Gdi2.SmoothingMode.AntiAlias;
            // 灰度抗锯齿（与电量贴片一致）。ClearType 是按颜色通道混色，混到透明黑背景上
            // 会让文字边缘糊出一圈黑描边，所以不能用。
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Gdi.Color.Transparent);

            // 与电量贴片一致：先铺一层 alpha=1 的近透明底，整块贴片才都是命中区。
            // 不铺的话鼠标会穿透到任务栏，点空白处（图标与数字之外）就没反应。
            using (var hit = new Gdi.SolidBrush(Gdi.Color.FromArgb(1, 0, 0, 0)))
            {
                g.FillRectangle(hit, 0, 0, _pillW, _pillH);
            }

            // 与电量贴片同款：无底色、无边框，只有前景色（图标 / 文字 / 分隔竖线）
            bool light = MemoryPillService.IsLightTheme();
            var skin = TaskbarPillRenderer.SkinFor(light);

            // 文字配色与电量贴片一致（同一套 PillPalette 状态色）；内存是占用率，越高越差
            var state = percent switch
            {
                >= 85 => PillPalette.Low,
                >= 70 => PillPalette.Mid,
                _ => PillPalette.Good,
            };

            // 内存条图标：与电量贴片的设备图标同样占 15dip 方框，图形居中
            var iconBox = new Gdi.RectangleF(pad, (_pillH - iconSize) / 2f, iconSize, iconSize);
            float innerW = iconSize * 0.86f;
            var iconRect = new Gdi.RectangleF(
                iconBox.X + (iconSize - innerW) / 2f,
                iconBox.Y + (iconSize - innerW) / 2f,
                innerW,
                innerW);
            DrawMemoryIcon(g, skin.IconStroke, iconRect, s);

            using var textBrush = new Gdi.SolidBrush(state);
            g.DrawString(label, font, textBrush,
                new Gdi.RectangleF(iconBox.Right + gap, 0, _pillW - iconBox.Right - gap, _pillH),
                sf);

            // 右侧竖线：与电量贴片内部的格间分隔线同款（1px 宽、14dip 高、垂直居中）。
            // 位置贴住文字（4dip），不放到位图最右——位图末尾还留着防截断的 SlackPx 余量，
            // 画到最右会让竖线离 "%" 太远，和电量贴片那一侧不对称。
            float dividerH = (float)(14 * scale);
            float textEnd = iconBox.Right + gap + textW;
            float dividerX = Math.Min(_pillW - Math.Max(1f, s), textEnd + (float)(4 * scale));
            using var dividerPen = new Gdi.Pen(skin.Divider, Math.Max(1f, s));
            float top = (_pillH - dividerH) / 2f;
            g.DrawLine(dividerPen, dividerX, top, dividerX, top + dividerH);
        }
        _bitmap = bmp;
    }

    private void Upload(int x, int y)
    {
        if (_bitmap is null)
        {
            return;
        }
        EnsureDib(_pillW, _pillH);
        if (_dib == IntPtr.Zero)
        {
            return;
        }
        CopyToDib(_bitmap, _pillW, _pillH);

        var dst = new Native.POINT { X = x, Y = y };
        var size = new Native.SIZE { cx = _pillW, cy = _pillH };
        var src = new Native.POINT { X = 0, Y = 0 };
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        Native.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
    }

    private void EnsureDib(int w, int h)
    {
        if (_dib != IntPtr.Zero && _dibW == w && _dibH == h)
        {
            return;
        }
        ReleaseDib();
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        _dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
        _oldDib = Native.SelectObject(_memDc, _dib);
        _dibW = w;
        _dibH = h;
    }

    private void CopyToDib(Gdi.Bitmap bmp, int w, int h)
    {
        var data = bmp.LockBits(new Gdi.Rectangle(0, 0, w, h), GdiImaging.ImageLockMode.ReadOnly, GdiImaging.PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                IntPtr srcRow = data.Scan0 + y * data.Stride;
                IntPtr dstRow = _dibBits + y * w * 4;
                CopyMemory(dstRow, srcRow, (uint)(w * 4));
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private void ReleaseDib()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldDib != IntPtr.Zero)
            {
                Native.SelectObject(_memDc, _oldDib);
            }
            Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero)
        {
            Native.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
        _oldDib = IntPtr.Zero;
        _dibBits = IntPtr.Zero;
        _dibW = _dibH = 0;
    }

    private void ReleaseWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        ReleaseDib();
        _bitmap?.Dispose();
        _bitmap = null;
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
        try { _timer.Stop(); } catch { }
        ReleaseWindow();
    }

    /// <summary>
    /// 内存条字形（对齐工具坞新内存图标）：圆角长条外框 + 四根竖条 + 底部金手指。
    /// 只用灰色描边、不填状态色——与电量贴片的设备图标一致，状态色只给百分比文字。
    /// </summary>
    private static void DrawMemoryIcon(Gdi.Graphics g, Gdi.Color stroke, Gdi.RectangleF box, float scale)
    {
        float w = box.Width;
        float totalH = w * 0.80f;                        // 条体 + 引脚总高
        float top = box.Y + (box.Height - totalH) / 2f;
        float bodyH = totalH * 0.74f;
        float pinH = totalH - bodyH - 1f * scale;
        var body = new Gdi.RectangleF(box.X, top, w, bodyH);

        using (var path = RoundedRect(body, Math.Max(1f, 1.8f * scale)))
        using (var pen = new Gdi.Pen(stroke, Math.Max(1f, 1.3f * scale)))
        {
            g.DrawPath(pen, path);
        }

        using var brush = new Gdi.SolidBrush(stroke);

        // 四根竖条
        float barW = w * 0.11f;
        float barH = bodyH * 0.42f;
        float barFirst = body.X + w * 0.15f;
        float barStep = w * 0.185f;
        for (int i = 0; i < 4; i++)
        {
            g.FillRectangle(brush, barFirst + barStep * i, body.Y + bodyH * 0.22f, barW, barH);
        }

        // 底部金手指引脚
        float pinW = w * 0.09f;
        float pinFirst = body.X + w * 0.30f;
        float pinStep = w * 0.135f;
        for (int i = 0; i < 4; i++)
        {
            g.FillRectangle(brush, pinFirst + pinStep * i, body.Bottom, pinW, pinH);
        }
    }

    private static Gdi2.GraphicsPath RoundedRect(Gdi.RectangleF r, float radius)
    {
        var path = new Gdi2.GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

    private static void Diag(string line)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"mempill-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

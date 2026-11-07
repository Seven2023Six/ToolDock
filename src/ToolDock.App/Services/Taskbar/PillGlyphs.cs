using VigilWatt.Core.Models;
using Gdi = System.Drawing;
using Gdi2 = System.Drawing.Drawing2D;

namespace ToolDock.App.Services;

/// <summary>
/// 设备类型的 GDI+ 图形（任务栏贴片用）：移植自 VigilWatt DeviceIconFactory 的 GDI 部分——
/// 贴片是原生分层窗口、内容走 ARGB 位图，用不了 XAML 元素。
/// </summary>
internal static class PillGlyphs
{
    // ---- GDI+ 版本（任务栏贴片用）----
    // 贴片是**原生分层窗口**、内容走 ARGB 位图，用不了 XAML 元素，所以同一套造型在这里再画一遍。
    // 与上面 XAML 版的造型必须保持一致（同一份设计两套渲染器，改一处记得改另一处）。

    /// <summary>把设备图形画进 <paramref name="box"/>（像素、正方形），图形本体按名义尺寸居中。</summary>
    public static void DrawGlyph(Gdi.Graphics g, DeviceKind kind, Gdi.Color stroke, Gdi.RectangleF box, float scale)
    {
        var (nominalW, nominalH) = NominalSize(kind);
        var w = nominalW * scale;
        var h = nominalH * scale;
        var x = box.X + (box.Width - w) / 2f;
        var y = box.Y + (box.Height - h) / 2f;
        var rect = new Gdi.RectangleF(x, y, w, h);

        var penW = 1.2f * scale;
        using var pen = new Gdi.Pen(stroke, penW) { LineJoin = Gdi2.LineJoin.Round };
        using var fill = new Gdi.SolidBrush(stroke);

        switch (kind)
        {
            case DeviceKind.Keyboard:
                using (var p = RoundedRect(rect, 2.5f * scale))
                    g.DrawPath(pen, p);
                g.FillRectangle(fill, x + (w - 7 * scale) / 2f, y + h - 1.8f * scale - 1.4f * scale, 7 * scale, 1.4f * scale);
                break;

            case DeviceKind.Mouse:
                using (var p = RoundedRect(rect, 4.5f * scale))
                    g.DrawPath(pen, p);
                g.FillRectangle(fill, x + (w - penW) / 2f, y + 2 * scale, penW, 3.5f * scale);
                break;

            case DeviceKind.Headset:
            {
                DrawHeadsetBand(g, pen, x, y, w, scale);
                foreach (var left in new[] { true, false })
                {
                    var cx = left ? x : x + w - 2.6f * scale;
                    using var p = RoundedRect(new Gdi.RectangleF(cx, y + h - 4.5f * scale, 2.6f * scale, 4.5f * scale), 1.3f * scale);
                    g.FillPath(fill, p);
                }
                break;
            }

            case DeviceKind.Speaker:
                using (var p = RoundedRect(rect, 2.5f * scale))
                    g.DrawPath(pen, p);
                g.FillEllipse(fill, x + (w - 3 * scale) / 2f, y + 2.2f * scale, 3 * scale, 3 * scale);
                g.FillEllipse(fill, x + (w - 4.4f * scale) / 2f, y + h - 2 * scale - 4.4f * scale, 4.4f * scale, 4.4f * scale);
                break;

            case DeviceKind.Gamepad:
                using (var p = RoundedRect(rect, 4.5f * scale))
                    g.DrawPath(pen, p);
                foreach (var left in new[] { true, false })
                {
                    var cx = left ? x + 2 * scale : x + w - 2 * scale - 2.6f * scale;
                    g.FillEllipse(fill, cx, y + (h - 2.6f * scale) / 2f, 2.6f * scale, 2.6f * scale);
                }
                break;

            case DeviceKind.Computer:
            {
                var screenH = 9.5f * scale;
                using (var p = RoundedRect(new Gdi.RectangleF(x, y, w, screenH), 1.8f * scale))
                    g.DrawPath(pen, p);
                g.FillRectangle(fill, x + (w - 5 * scale) / 2f, y + h - 1.4f * scale, 5 * scale, 1.4f * scale);
                break;
            }

            case DeviceKind.Phone:
                using (var p = RoundedRect(rect, 2 * scale))
                    g.DrawPath(pen, p);
                g.FillRectangle(fill, x + (w - 3 * scale) / 2f, y + 1.2f * scale, 3 * scale, 1 * scale);
                g.FillRectangle(fill, x + (w - 2 * scale) / 2f, y + h - 2.1f * scale, 2 * scale, 0.9f * scale);
                break;

            case DeviceKind.Tablet:
                using (var p = RoundedRect(rect, 1.8f * scale))
                    g.DrawPath(pen, p);
                g.FillEllipse(fill, x + (w - 1.8f * scale) / 2f, y + h - 2.5f * scale, 1.8f * scale, 1.8f * scale);
                break;

            case DeviceKind.Watch:
            {
                foreach (var top in new[] { true, false })
                {
                    var bandY = top ? y : y + h - 2.6f * scale;
                    using var band = RoundedRect(
                        new Gdi.RectangleF(x + (w - 4 * scale) / 2f, bandY, 4 * scale, 2.6f * scale), 0.8f * scale);
                    g.FillPath(fill, band);
                }
                using var face = RoundedRect(
                    new Gdi.RectangleF(x + (w - 8.4f * scale) / 2f, y + (h - 8.4f * scale) / 2f, 8.4f * scale, 8.4f * scale),
                    2.4f * scale);
                g.DrawPath(pen, face);
                break;
            }

            default:
                using (var p = new Gdi2.GraphicsPath())
                {
                    p.AddEllipse(rect);
                    using var thin = new Gdi.Pen(stroke, 1.4f * scale);
                    g.DrawPath(thin, p);
                }
                break;
        }
    }

    /// <summary>头梁用对称贝塞尔并在名义框里居中。不用 DrawArc，见 runtime-pitfalls.md §16.9。</summary>
    private static void DrawHeadsetBand(Gdi.Graphics g, Gdi.Pen pen, float originX, float originY, float glyphW, float scale)
    {
        const float kappa = 0.5522848f; // 四分之一圆的贝塞尔系数，左右控制点镜像
        float bandW = 11f * scale;
        float bandX = originX + (glyphW - bandW) / 2f;
        float inset = pen.Width / 2f;
        float left = bandX + inset;
        float right = bandX + bandW - inset;
        float top = originY + inset;
        float bottom = originY + 7f * scale;
        float radius = (right - left) / 2f;
        float cx = (left + right) / 2f;
        float cy = top + radius;
        float k = kappa * radius;

        using var path = new Gdi2.GraphicsPath();
        path.AddLine(left, bottom, left, cy);
        path.AddBezier(left, cy, left, cy - k, cx - k, cy - radius, cx, cy - radius);
        path.AddBezier(cx, cy - radius, cx + k, cy - radius, right, cy - k, right, cy);
        path.AddLine(right, cy, right, bottom);
        g.DrawPath(pen, path);
    }

    private static (float W, float H) NominalSize(DeviceKind kind) => kind switch
    {
        DeviceKind.Keyboard => (13f, 9f),
        DeviceKind.Mouse => (9f, 13f),
        DeviceKind.Headset => (13f, 12f),
        DeviceKind.Speaker => (9.5f, 13f),
        DeviceKind.Gamepad => (14f, 9f),
        DeviceKind.Computer => (14f, 11f),
        DeviceKind.Phone => (8f, 13f),
        DeviceKind.Tablet => (11f, 13f),
        DeviceKind.Watch => (9f, 13f),
        _ => (9f, 9f),
    };

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
}

using Gdi = System.Drawing;
using Gdi2 = System.Drawing.Drawing2D;
using GdiImaging = System.Drawing.Imaging;

namespace ToolDock.App.Services;

/// <summary>
/// 功 能： 任务栏贴片悬停提示的位图绘制（类 Win11：圆角、细边框、轻阴影）。
/// 2026/10/15 17:20:00 xliu
/// 贴片跨进程子窗口用不了系统 ToolTip，自绘分层窗；样式约定见 runtime-pitfalls.md §16.10。
/// </summary>
public static class TaskbarTipRenderer
{
    public const double PadH = 10;
    public const double PadV = 6;
    public const double FontSize = 12;
    public const double Radius = 6;
    public const double MaxWidth = 240;

    public readonly record struct Skin(Gdi.Color Fill, Gdi.Color Border, Gdi.Color Text);

    public static Skin SkinFor(bool light) => light
        ? new Skin(
            Fill: Gdi.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3),
            Border: Gdi.Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0),
            Text: Gdi.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A))
        : new Skin(
            Fill: Gdi.Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C),
            Border: Gdi.Color.FromArgb(0xFF, 0x3F, 0x3F, 0x3F),
            Text: Gdi.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// 画提示（只有气泡本体，不画阴影）。旧实现叠了 6 圈递减 alpha 的黑色圆角阴影，
    /// 相邻圈互相叠加后紧贴气泡处接近 70% 黑，视觉上就是一圈突兀的黑边，故整体移除。
    /// </summary>
    public static Gdi.Bitmap Render(
        string text, Skin skin, float scale,
        out int widthPx, out int heightPx, out int bubbleW, out int bubbleH)
    {
        int padH = (int)Math.Round(PadH * scale);
        int padV = (int)Math.Round(PadV * scale);
        int maxTextW = (int)Math.Round(MaxWidth * scale);
        float radius = (float)(Radius * scale);

        using var font = new Gdi.Font("Microsoft YaHei UI", (float)(FontSize * scale),
            Gdi.FontStyle.Regular, Gdi.GraphicsUnit.Pixel);
        using var measureBmp = new Gdi.Bitmap(1, 1, GdiImaging.PixelFormat.Format32bppPArgb);
        using var measureG = Gdi.Graphics.FromImage(measureBmp);
        var textSize = measureG.MeasureString(text, font, maxTextW);

        bubbleW = Math.Max(1, (int)Math.Ceiling(textSize.Width) + padH * 2);
        bubbleH = Math.Max(1, (int)Math.Ceiling(textSize.Height) + padV * 2);
        widthPx = bubbleW;
        heightPx = bubbleH;

        var bmp = new Gdi.Bitmap(widthPx, heightPx, GdiImaging.PixelFormat.Format32bppPArgb);
        using var g = Gdi.Graphics.FromImage(bmp);
        g.SmoothingMode = Gdi2.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Gdi.Color.Transparent);

        using (var path = RoundRect(0, 0, bubbleW, bubbleH, radius))
        {
            using (var brush = new Gdi.SolidBrush(skin.Fill))
                g.FillPath(brush, path);
            using var pen = new Gdi.Pen(skin.Border, Math.Max(1f, scale));
            g.DrawPath(pen, path);
        }

        using (var brush = new Gdi.SolidBrush(skin.Text))
            g.DrawString(text, font, brush, new Gdi.RectangleF(padH, padV, bubbleW - padH * 2, bubbleH - padV * 2));

        return bmp;
    }

    private static Gdi2.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2f));
        var path = new Gdi2.GraphicsPath();
        if (r < 0.5f)
        {
            path.AddRectangle(new Gdi.RectangleF(x, y, w, h));
            return path;
        }
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

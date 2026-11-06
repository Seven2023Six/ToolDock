using System.Runtime.InteropServices;
using ToolDock.Agent.Native;

namespace ToolDock.Agent;

/// <summary>
/// 托盘图标：优先加载 assets 生成的嵌入式 BGRA 像素（真实图标），
/// 资源缺失时回退到程序绘制的占位图形。纯 GDI，AOT 友好。
/// </summary>
internal static class TrayIconFactory
{
    public static IntPtr Create(int size)
    {
        var pixels = TryLoadEmbedded(size) ?? DrawFallback();
        return CreateHiconFromBgra(pixels, size);
    }

    private static byte[]? TryLoadEmbedded(int size)
    {
        try
        {
            var name = $"ToolDock.Agent.TrayIconData.icon{size}.bgra";
            using var stream = typeof(TrayIconFactory).Assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer); // 不足会抛 EndOfStreamException，由外层 catch 归为回退
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr CreateHiconFromBgra(byte[] pixels, int size)
    {
        var bmi = new NativeMethods.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = size;
        bmi.bmiHeader.biHeight = -size; // 负值 = 自上而下，与 GDI+ LockBits 行序一致
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB

        IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            IntPtr colorBits;
            IntPtr hbmColor = NativeMethods.CreateDIBSection(hdc, ref bmi, 0, out colorBits, IntPtr.Zero, 0);
            if (hbmColor == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            Marshal.Copy(pixels, 0, colorBits, Math.Min(pixels.Length, size * size * 4));

            var maskBytes = new byte[size * size / 8];
            IntPtr maskPtr = Marshal.AllocHGlobal(maskBytes.Length);
            Marshal.Copy(maskBytes, 0, maskPtr, maskBytes.Length);
            IntPtr hbmMask = NativeMethods.CreateBitmap(size, size, 1, 1, maskPtr);
            Marshal.FreeHGlobal(maskPtr);

            var iconInfo = new NativeMethods.ICONINFO
            {
                fIcon = 1,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hbmMask,
                hbmColor = hbmColor,
            };
            IntPtr hIcon = NativeMethods.CreateIconIndirect(ref iconInfo);
            // CreateIconIndirect 内部拷贝位图，ours 可立即释放
            NativeMethods.DeleteObject(hbmColor);
            NativeMethods.DeleteObject(hbmMask);
            return hIcon;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>兜底：深色圆角方块 + 底部强调色"坞"条（嵌入式资源缺失时才用到）。</summary>
    private static byte[] DrawFallback()
    {
        const int size = 32;
        var pixels = new byte[size * size * 4]; // BGRA
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte b = 0, g = 0, r = 0, a = 0;
                if (InRoundedRect(x, y, 2, 2, 29, 29, 7))
                {
                    (b, g, r, a) = (32, 32, 32, 235);
                }
                if (InRoundedRect(x, y, 2, 2, 29, 29, 7) && !InRoundedRect(x, y, 3, 3, 28, 28, 6))
                {
                    (b, g, r, a) = (255, 255, 255, 200);
                }
                if (InRoundedRect(x, y, 8, 21, 23, 24, 2))
                {
                    (b, g, r, a) = (255, 194, 76, 255);
                }
                int offset = (y * size + x) * 4;
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = a;
            }
        }
        return pixels;
    }

    private static bool InRoundedRect(int x, int y, int x0, int y0, int x1, int y1, int radius)
    {
        if (x < x0 || x > x1 || y < y0 || y > y1)
        {
            return false;
        }
        int cx = x < x0 + radius ? x0 + radius : x > x1 - radius ? x1 - radius : x;
        int cy = y < y0 + radius ? y0 + radius : y > y1 - radius ? y1 - radius : y;
        int dx = x - cx;
        int dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }
}

using System.IO.Compression;
using System.Runtime.InteropServices;

// 把 Windows 光标（.cur）染色成蓝色，保留原形状与热点；.ani 直接复制。
// 用法：cursortint <sourceRoot> <outputRoot>
internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadCursorFromFileW(string path);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(IntPtr obj, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[]? bits, ref BITMAPINFO info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    private const uint BI_RGB = 0;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: cursortint <sourceRoot> <outputRoot>");
            return 1;
        }
        string sourceRoot = args[0];
        string outputRoot = args[1];
        Directory.CreateDirectory(outputRoot);

        var map = new (string Key, string Source, string Target)[]
        {
            ("Arrow", @"base\pointer.cur", "Arrow.cur"),
            ("IBeam", @"base\beam.cur", "IBeam.cur"),
            ("Wait", @"classic\busy.ani", "Wait.ani"),
            ("Crosshair", @"base\precision.cur", "Crosshair.cur"),
            ("UpArrow", @"base\alternate.cur", "UpArrow.cur"),
            ("SizeNWSE", @"base\dgn1.cur", "SizeNWSE.cur"),
            ("SizeNESW", @"base\dgn2.cur", "SizeNESW.cur"),
            ("SizeWE", @"base\horz.cur", "SizeWE.cur"),
            ("SizeNS", @"base\vert.cur", "SizeNS.cur"),
            ("SizeAll", @"base\move.cur", "SizeAll.cur"),
            ("No", @"base\unavailable.cur", "No.cur"),
            ("Hand", @"base\link.cur", "Hand.cur"),
            ("AppStarting", @"classic\working.ani", "AppStarting.ani"),
            ("Help", @"base\help.cur", "Help.cur"),
            ("NWPen", @"base\handwriting.cur", "NWPen.cur"),
            ("Pin", @"base\pin.cur", "Pin.cur"),
            ("Person", @"base\person.cur", "Person.cur"),
        };

        int ok = 0;
        foreach (var (key, relative, targetName) in map)
        {
            string source = Path.Combine(sourceRoot, relative);
            string target = Path.Combine(outputRoot, targetName);
            if (!File.Exists(source))
            {
                Console.WriteLine($"missing {source}");
                continue;
            }
            try
            {
                if (Path.GetExtension(source).Equals(".ani", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(source, target, true);
                    Console.WriteLine($"copied {targetName}");
                }
                else
                {
                    if (TintCursor(source, target))
                    {
                        Console.WriteLine($"tinted {targetName}");
                    }
                    else
                    {
                        File.Copy(source, target, true);
                        Console.WriteLine($"copied (tint failed) {targetName}");
                    }
                }
                ok++;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"failed {key}: {exception.Message}");
            }
        }
        Console.WriteLine($"done {ok}/{map.Length} -> {outputRoot}");
        return ok == map.Length ? 0 : 2;
    }

    private static bool TintCursor(string source, string target)
    {
        IntPtr cursor = LoadCursorFromFileW(source);
        if (cursor == IntPtr.Zero)
        {
            return false;
        }
        ICONINFO info = default;
        if (!GetIconInfo(cursor, out info))
        {
            return false;
        }
        try
        {
            IntPtr colorBitmap = info.hbmColor;
            IntPtr maskBitmap = info.hbmMask;
            if (colorBitmap == IntPtr.Zero)
            {
                return false;
            }
            if (GetObjectW(colorBitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bitmap) == 0)
            {
                return false;
            }
            int width = bitmap.bmWidth;
            int height = bitmap.bmHeight;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            byte[] bgra = ReadBitmap(colorBitmap, width, height, out int bpp);
            if (bgra.Length == 0)
            {
                return false;
            }
            if (bpp < 32 || IsAllZeroAlpha(bgra))
            {
                // 旧式光标没有 alpha：用 AND 掩码决定透明
                byte[] mask = ReadMask(maskBitmap, width, height);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = (y * width + x) * 4;
                        bool transparent = (mask[(y * width + x)] & 1) != 0;
                        bgra[index + 3] = transparent ? (byte)0 : (byte)255;
                    }
                }
            }

            TintBlue(bgra);
            byte[] png = EncodePng(width, height, bgra);
            WriteCur(target, width, height, info.xHotspot, info.yHotspot, png);
            return true;
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    private static byte[] ReadBitmap(IntPtr bitmap, int width, int height, out int bpp)
    {
        bpp = 32;
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };
        byte[] buffer = new byte[width * height * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            int lines = GetDIBits(dc, bitmap, 0, (uint)height, buffer, ref info, BI_RGB);
            if (lines <= 0)
            {
                return [];
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        return buffer;
    }

    private static byte[] ReadMask(IntPtr mask, int width, int height)
    {
        byte[] result = new byte[width * height];
        if (mask == IntPtr.Zero)
        {
            return result;
        }
        int stride = ((width + 31) / 32) * 4;
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 1,
                biCompression = BI_RGB,
            },
        };
        byte[] buffer = new byte[stride * height];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, mask, 0, (uint)height, buffer, ref info, BI_RGB) <= 0)
            {
                return result;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int bit = (buffer[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1;
                result[y * width + x] = (byte)bit;
            }
        }
        return result;
    }

    private static bool IsAllZeroAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static void TintBlue(byte[] bgra)
    {
        // 暗部（描边）保留，亮部染成 #3A8CFF；亮度作为混合强度
        for (int i = 0; i < bgra.Length; i += 4)
        {
            if (bgra[i + 3] == 0)
            {
                continue;
            }
            int b = bgra[i];
            int g = bgra[i + 1];
            int r = bgra[i + 2];
            double luminance = (299 * r + 587 * g + 114 * b) / 1000.0;
            double t = luminance / 255.0;
            bgra[i] = (byte)Math.Round(b * (1 - t) + 255 * t);
            bgra[i + 1] = (byte)Math.Round(g * (1 - t) + 140 * t);
            bgra[i + 2] = (byte)Math.Round(r * (1 - t) + 58 * t);
        }
    }

    private static void WriteCur(string path, int width, int height, int hotspotX, int hotspotY, byte[] png)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write((byte)(width >= 256 ? 0 : width));
        writer.Write((byte)(height >= 256 ? 0 : height));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)hotspotX);
        writer.Write((ushort)hotspotY);
        writer.Write((uint)png.Length);
        writer.Write((uint)22);
        writer.Write(png);
    }

    // ---- PNG 编码（RGBA，无过滤） ----

    private static byte[] EncodePng(int width, int height, byte[] bgra)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        using (var ihdr = new MemoryStream())
        {
            WriteBigEndian(ihdr, width);
            WriteBigEndian(ihdr, height);
            ihdr.WriteByte(8);  // bit depth
            ihdr.WriteByte(6);  // RGBA
            ihdr.WriteByte(0);
            ihdr.WriteByte(0);
            ihdr.WriteByte(0);
            WriteChunk(output, "IHDR", ihdr.ToArray());
        }

        byte[] raw;
        using (var rawStream = new MemoryStream())
        {
            for (int y = 0; y < height; y++)
            {
                rawStream.WriteByte(0);
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    rawStream.WriteByte(bgra[i + 2]);
                    rawStream.WriteByte(bgra[i + 1]);
                    rawStream.WriteByte(bgra[i]);
                    rawStream.WriteByte(bgra[i + 3]);
                }
            }
            raw = rawStream.ToArray();
        }

        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var z = new ZLibStream(compressedStream, CompressionLevel.Optimal, true))
            {
                z.Write(raw);
            }
            compressed = compressedStream.ToArray();
        }
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        WriteBigEndian(stream, data.Length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = Crc32(typeBytes, data);
        WriteBigEndian(stream, (int)crc);
    }

    private static void WriteBigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] first, byte[] second)
    {
        _crcTable ??= BuildCrcTable();
        uint crc = 0xFFFFFFFF;
        foreach (byte b in first) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in second) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }
}

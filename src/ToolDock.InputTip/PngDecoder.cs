using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ToolDock.InputTip;

/// <summary>
/// 极简 PNG 解码（8 位、非隔行）：RGBA/RGB/灰度/调色板。
/// 输入法提示的默认符号是 PNG，NativeAOT 下不引 System.Drawing，这里直接解成 BGRA。
/// </summary>
internal static class PngDecoder
{
    public sealed class Bitmap
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        /// <summary>BGRA，非预乘。</summary>
        public required byte[] Pixels { get; init; }
    }

    public static Bitmap? Decode(string path)
    {
        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    public static Bitmap? Decode(byte[] data)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(signature))
        {
            return null;
        }

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();
        int offset = 8;
        while (offset + 8 <= data.Length)
        {
            int length = ReadBigEndian(data, offset);
            string type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            int dataOffset = offset + 8;
            if (dataOffset + length > data.Length)
            {
                break;
            }
            switch (type)
            {
                case "IHDR":
                    width = ReadBigEndian(data, dataOffset);
                    height = ReadBigEndian(data, dataOffset + 4);
                    bitDepth = data[dataOffset + 8];
                    colorType = data[dataOffset + 9];
                    interlace = data[dataOffset + 12];
                    break;
                case "PLTE":
                    palette = data.AsSpan(dataOffset, length).ToArray();
                    break;
                case "tRNS":
                    transparency = data.AsSpan(dataOffset, length).ToArray();
                    break;
                case "IDAT":
                    idat.Write(data, dataOffset, length);
                    break;
                case "IEND":
                    offset = data.Length;
                    break;
            }
            offset = dataOffset + length + 4; // CRC
        }

        if (width <= 0 || height <= 0 || bitDepth != 8 || interlace != 0)
        {
            return null;
        }

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };
        if (channels == 0 || (colorType == 3 && palette is null))
        {
            return null;
        }

        byte[] compressed = idat.ToArray();
        int stride = width * channels;
        byte[] raw = new byte[height * (stride + 1)];
        try
        {
            using var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }
        }
        catch
        {
            return null;
        }

        byte[] pixels = new byte[width * height * 4];
        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * (stride + 1);
            byte filter = raw[rowOffset];
            Buffer.BlockCopy(raw, rowOffset + 1, current, 0, stride);
            Unfilter(filter, current, previous, channels);
            for (int x = 0; x < width; x++)
            {
                int src = x * channels;
                byte r, g, b, a = 255;
                switch (colorType)
                {
                    case 0:
                        r = g = b = current[src];
                        break;
                    case 2:
                        r = current[src];
                        g = current[src + 1];
                        b = current[src + 2];
                        break;
                    case 3:
                        int index = current[src] * 3;
                        if (index + 2 >= palette!.Length)
                        {
                            r = g = b = 0;
                        }
                        else
                        {
                            r = palette[index];
                            g = palette[index + 1];
                            b = palette[index + 2];
                        }
                        if (transparency is not null && current[src] < transparency.Length)
                        {
                            a = transparency[current[src]];
                        }
                        break;
                    case 4:
                        r = g = b = current[src];
                        a = current[src + 1];
                        break;
                    default:
                        r = current[src];
                        g = current[src + 1];
                        b = current[src + 2];
                        a = current[src + 3];
                        break;
                }
                int dst = (y * width + x) * 4;
                pixels[dst] = b;
                pixels[dst + 1] = g;
                pixels[dst + 2] = r;
                pixels[dst + 3] = a;
            }
            (previous, current) = (current, previous);
        }

        return new Bitmap { Width = width, Height = height, Pixels = pixels };
    }

    private static void Unfilter(byte filter, byte[] current, byte[] previous, int bpp)
    {
        switch (filter)
        {
            case 1:
                for (int i = bpp; i < current.Length; i++)
                {
                    current[i] = (byte)(current[i] + current[i - bpp]);
                }
                break;
            case 2:
                for (int i = 0; i < current.Length; i++)
                {
                    current[i] = (byte)(current[i] + previous[i]);
                }
                break;
            case 3:
                for (int i = 0; i < current.Length; i++)
                {
                    int left = i >= bpp ? current[i - bpp] : 0;
                    current[i] = (byte)(current[i] + ((left + previous[i]) >> 1));
                }
                break;
            case 4:
                for (int i = 0; i < current.Length; i++)
                {
                    int a = i >= bpp ? current[i - bpp] : 0;
                    int b = previous[i];
                    int c = i >= bpp ? previous[i - bpp] : 0;
                    current[i] = (byte)(current[i] + Paeth(a, b, c));
                }
                break;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int ReadBigEndian(byte[] data, int offset)
        => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    /// <summary>把带 alpha 的图合成到纯色背景上（对齐上游 WinSetTransColor 的取色键做法）。</summary>
    public static byte[] CompositeOnBackground(Bitmap bitmap, int backgroundRgb)
    {
        int bgR = (backgroundRgb >> 16) & 0xFF;
        int bgG = (backgroundRgb >> 8) & 0xFF;
        int bgB = backgroundRgb & 0xFF;
        byte[] output = new byte[bitmap.Pixels.Length];
        for (int i = 0; i < bitmap.Pixels.Length; i += 4)
        {
            int alpha = bitmap.Pixels[i + 3];
            int b = bitmap.Pixels[i];
            int g = bitmap.Pixels[i + 1];
            int r = bitmap.Pixels[i + 2];
            int inv = 255 - alpha;
            output[i] = (byte)((b * alpha + bgB * inv) / 255);
            output[i + 1] = (byte)((g * alpha + bgG * inv) / 255);
            output[i + 2] = (byte)((r * alpha + bgR * inv) / 255);
            output[i + 3] = 255;
        }
        return output;
    }
}

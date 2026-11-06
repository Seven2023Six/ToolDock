using System.Text.Json;

namespace ToolDock.Core;

/// <summary>管道帧：4 字节小端长度 + UTF-8 JSON。读写两端的唯一编解码出口。</summary>
public static class PipeFrame
{
    private const int MaxMessageBytes = 1_000_000;

    public static async Task<PipeMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(stream, lenBuf, cancellationToken).ConfigureAwait(false))
        {
            return null; // 对端正常关闭
        }
        int length = BitConverter.ToInt32(lenBuf, 0);
        if (length is < 1 or > MaxMessageBytes)
        {
            return null; // 帧损坏，视为断连
        }
        var buf = new byte[length];
        if (!await ReadExactAsync(stream, buf, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return JsonSerializer.Deserialize(buf, ToolDockJsonContext.Default.PipeMessage);
    }

    public static async Task WriteAsync(Stream stream, PipeMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ToolDockJsonContext.Default.PipeMessage);
        var frame = new byte[4 + json.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), json.Length);
        json.CopyTo(frame, 4);
        // ConfigureAwait(false)：App 会在 UI 线程同步等待这些 IO，续延绝不能回 UI 线程（否则死锁）
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }
            offset += n;
        }
        return true;
    }
}

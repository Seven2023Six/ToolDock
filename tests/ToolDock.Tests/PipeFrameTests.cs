using System.IO;
using System.Text;
using System.Text.Json;
using ToolDock.Core;

namespace ToolDock.Tests;

/// <summary>管道帧编解码：字节级协议是 Agent/App 之间的硬契约，必须锁死。</summary>
public class PipeFrameTests
{
    [Fact]
    public async Task RoundTrip_PreservesFields()
    {
        using var stream = new MemoryStream();
        var message = new PipeMessage
        {
            T = "welcome",
            Ver = 2,
            Mask = 0xE7,
            AutoClean = true,
            Theme = "Light",
        };

        await PipeFrame.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;
        var read = await PipeFrame.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("welcome", read!.T);
        Assert.Equal(2, read.Ver);
        Assert.Equal(0xE7UL, read.Mask);
        Assert.True(read.AutoClean);
        Assert.Equal("Light", read.Theme);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_OnEmptyStream()
    {
        using var stream = new MemoryStream();
        Assert.Null(await PipeFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_OnOversizedLength()
    {
        using var stream = new MemoryStream();
        await stream.WriteAsync(BitConverter.GetBytes(2_000_000)); // 超过 1MB 上限
        stream.Position = 0;
        Assert.Null(await PipeFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_OnTruncatedBody()
    {
        using var stream = new MemoryStream();
        await stream.WriteAsync(BitConverter.GetBytes(100)); // 声明 100 字节但只给 3 字节
        await stream.WriteAsync(new byte[] { 0x7B, 0x7D, 0x20 });
        stream.Position = 0;
        Assert.Null(await PipeFrame.ReadAsync(stream, CancellationToken.None));
    }

    /// <summary>协议演进：新版本新增字段时，旧解析端必须容忍未知成员而不是断连。</summary>
    [Fact]
    public void UnknownJsonMember_IsIgnored()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"t\":\"welcome\",\"futureField\":123,\"mask\":231}");
        var message = JsonSerializer.Deserialize(json, ToolDockJsonContext.Default.PipeMessage);

        Assert.NotNull(message);
        Assert.Equal("welcome", message!.T);
        Assert.Equal(231UL, message.Mask);
    }
}

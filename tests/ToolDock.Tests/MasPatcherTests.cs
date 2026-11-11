using ToolDock.App.Services.Mas;

namespace ToolDock.Tests;

/// <summary>MAS 汉化补丁与版本解析：补丁表是"官方脚本 → 中文菜单"的唯一转换点。</summary>
public class MasPatcherTests
{
    [Fact]
    public void Apply_ReplacesKnownMenuStrings()
    {
        const string payload =
            "echo:                 Activation Methods:\r\n" +
            "Choose a menu option using your keyboard [1,2,3...E,H,0] :\r\n";

        int applied = MasPatcher.Apply(payload, out string patched);

        Assert.Equal(2, applied);
        Assert.Contains("激活方式：", patched);
        Assert.Contains("请用键盘选择菜单选项", patched);
        Assert.DoesNotContain("Activation Methods:", patched);
    }

    [Fact]
    public void Apply_LeavesUnknownContentUntouched()
    {
        int applied = MasPatcher.Apply("no menu here", out string patched);

        Assert.Equal(0, applied);
        Assert.Equal("no menu here", patched);
    }

    [Theory]
    [InlineData("@set masver=3.12", "v3.12")]
    [InlineData("prefix @set masver=1.0 suffix", "v1.0")]
    [InlineData("nothing", null)]
    public void ParseVersion_Extracts(string text, string? expected)
    {
        Assert.Equal(expected, MasPatcher.ParseVersion(text));
    }

    [Fact]
    public void BuildContent_StartsWithPatchStampAndContainsTranslation()
    {
        string content = MasPatcher.BuildContent("echo:             [0] Exit", out _);

        Assert.StartsWith("@::: ToolDock mas-cn/1\r\n", content);
        Assert.Contains("echo:             [0] 退出", content);
    }

    /// <summary>GBK 每个汉字 2 字节；写读往返必须无损（控制台按 cp936 解析）。</summary>
    [Fact]
    public void GbkEncoding_RoundTripsChinese()
    {
        const string text = "激活方式";

        byte[] bytes = MasPatcher.GbkEncoding().GetBytes(text);
        Assert.Equal(text.Length * 2, bytes.Length);
        Assert.Equal(text, MasPatcher.GbkEncoding().GetString(bytes));
    }
}

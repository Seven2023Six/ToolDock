using ToolDock.App.Services.Mas;

namespace ToolDock.Tests;

/// <summary>MAS 更新：引导脚本解析与哈希工具。网络路径不在单测里覆盖。</summary>
public class MasUpdaterTests
{
    private const string Bootstrap =
        "# bootstrap\n" +
        "$URLs = @(\n" +
        "    'https://example.test/a.cmd',\n" +
        "    'https://example.test/b.cmd'\n" +
        ")\n" +
        "$releaseHash = '850F979665FB93999ACAE93F4790C1FF8ED2041532060B7966A121C2D29A0BFA'\n";

    [Fact]
    public void TryParseBootstrap_ParsesUrlsAndHash()
    {
        bool ok = MasUpdater.TryParseBootstrap(Bootstrap, out var urls, out string hash);

        Assert.True(ok);
        Assert.Equal(2, urls.Count);
        Assert.Contains("https://example.test/a.cmd", urls);
        Assert.Equal("850F979665FB93999ACAE93F4790C1FF8ED2041532060B7966A121C2D29A0BFA", hash);
    }

    [Fact]
    public void TryParseBootstrap_RejectsMalformedContent()
    {
        Assert.False(MasUpdater.TryParseBootstrap("hello", out var urls, out string hash));
        Assert.Empty(urls);
        Assert.Equal("", hash);
    }

    [Fact]
    public void TryParseBootstrap_RejectsHashWithWrongLength()
    {
        string text = "$URLs = @('https://example.test/a.cmd')\n$releaseHash = 'ABC123'\n";
        Assert.False(MasUpdater.TryParseBootstrap(text, out _, out _));
    }

    [Fact]
    public void Sha256_MatchesKnownVector()
    {
        // 空串 SHA-256 的标准向量
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            MasUpdater.Sha256(""));
    }
}

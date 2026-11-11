using ToolDock.InputTip;

namespace ToolDock.Tests;

/// <summary>规则引擎的纯函数部分：正则匹配与屏幕偏移解析。</summary>
public class RuleEngineTests
{
    private static RuleEngine CreateEngine() => new(new InputTipSettings());

    [Fact]
    public void RegexMatch_ValidPattern_Matches()
    {
        var engine = CreateEngine();
        Assert.True(engine.RegexMatch("notepad.exe", "notepad"));
        Assert.False(engine.RegexMatch("explorer.exe", "^notepad$"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("([")]
    public void RegexMatch_InvalidPattern_ReturnsFalse(string pattern)
    {
        Assert.False(CreateEngine().RegexMatch("anything", pattern));
    }

    [Fact]
    public void ParseScreenOffset_ParsesPerScreenEntry()
    {
        Assert.Equal((10, 20), RuleEngine.ParseScreenOffset("1/10/20|2/-5/6", 1));
        Assert.Equal((-5, 6), RuleEngine.ParseScreenOffset("1/10/20|2/-5/6", 2));
        Assert.Equal((0, 0), RuleEngine.ParseScreenOffset("1/10/20", 3));
        Assert.Equal((0, 0), RuleEngine.ParseScreenOffset("", 1));
        Assert.Equal((0, 0), RuleEngine.ParseScreenOffset("malformed", 1));
    }
}

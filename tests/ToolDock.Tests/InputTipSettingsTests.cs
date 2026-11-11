using ToolDock.InputTip;

namespace ToolDock.Tests;

/// <summary>输入法提示设置归一化：非法值必须被钳制/补齐，避免引擎拿到越界配置。</summary>
public class InputTipSettingsTests
{
    [Fact]
    public void Normalize_ClampsInvalidValues()
    {
        var settings = new InputTipSettings
        {
            PollInterval = 0,
            MenuFontSize = 1,
            InputMethodDetectionTimeout = 99999,
            OverlayTransparent = 999,
            InputMethodBaseState = "XX",
            InputMethodDetectionMode = "weird",
        };

        settings.Normalize();

        Assert.Equal(20, settings.PollInterval);
        Assert.Equal(8, settings.MenuFontSize);
        Assert.Equal(2000, settings.InputMethodDetectionTimeout);
        Assert.Equal(255, settings.OverlayTransparent);
        Assert.Equal("EN", settings.InputMethodBaseState);
        Assert.Equal("general", settings.InputMethodDetectionMode);
    }

    [Fact]
    public void Normalize_FillsAllStateDictionaries()
    {
        var settings = new InputTipSettings();

        settings.Normalize();

        foreach (string state in InputTipStates.All)
        {
            Assert.True(settings.OverlayStyles.ContainsKey(state), $"OverlayStyles 缺少 {state}");
            Assert.True(settings.BorderStyles.ContainsKey(state), $"BorderStyles 缺少 {state}");
            Assert.True(settings.CaretSymbolStyles.ContainsKey(state), $"CaretSymbolStyles 缺少 {state}");
            Assert.True(settings.CursorSymbolStyles.ContainsKey(state), $"CursorSymbolStyles 缺少 {state}");
            Assert.False(string.IsNullOrWhiteSpace(settings.CursorPaths[state]), $"CursorPaths 缺少 {state}");
        }
    }

    [Fact]
    public void Normalize_KeepsValidValues()
    {
        var settings = new InputTipSettings
        {
            PollInterval = 50,
            MenuFontSize = 20,
            InputMethodBaseState = "CN",
        };

        settings.Normalize();

        Assert.Equal(50, settings.PollInterval);
        Assert.Equal(20, settings.MenuFontSize);
        Assert.Equal("CN", settings.InputMethodBaseState);
    }
}

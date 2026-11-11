using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolDock.InputTip;

/// <summary>输入法状态的六个取值（对齐 InputTip：CN/EN/Caps 为中文键盘下的状态，US/JP/KR 为键盘布局）。</summary>
public static class InputTipStates
{
    public static readonly string[] All = ["CN", "EN", "Caps", "US", "JP", "KR"];

    /// <summary>状态默认颜色（0xRRGGBB，与上游 stateVal 一致）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>
    {
        ["CN"] = "0xFF0000",
        ["EN"] = "0x0000FF",
        ["Caps"] = "0x008000",
        ["US"] = "0x0000FF",
        ["JP"] = "0xCCCC00",
        ["KR"] = "0x800080",
    };

    /// <summary>默认图标配色名（对应 default-triangle-*.png 与 default-* 光标目录）。</summary>
    public static readonly IReadOnlyDictionary<string, string> ColorNames = new Dictionary<string, string>
    {
        ["CN"] = "red",
        ["EN"] = "blue",
        ["Caps"] = "green",
        ["US"] = "blue",
        ["JP"] = "yellow",
        ["KR"] = "purple",
    };

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["CN"] = "中文状态（中文键盘）",
        ["EN"] = "英文状态（中文键盘）",
        ["Caps"] = "大写锁定（CapsLock）",
        ["US"] = "英文键盘",
        ["JP"] = "日文键盘",
        ["KR"] = "韩文键盘",
    };

    /// <summary>悬浮小窗默认文案（比完整标签短，适合角标）。</summary>
    public static readonly IReadOnlyDictionary<string, string> ShortLabels = new Dictionary<string, string>
    {
        ["CN"] = "中",
        ["EN"] = "英",
        ["Caps"] = "大写",
        ["US"] = "US",
        ["JP"] = "日",
        ["KR"] = "韩",
    };

    /// <summary>光标符号默认文字（上游取 i18n(state) 首字）。</summary>
    public static readonly IReadOnlyDictionary<string, string> SymbolLabels = new Dictionary<string, string>
    {
        ["CN"] = "中",
        ["EN"] = "英",
        ["Caps"] = "大",
        ["US"] = "英",
        ["JP"] = "日",
        ["KR"] = "韩",
    };

    public static bool IsState(string? value)
        => value is not null && Array.IndexOf(All, value) >= 0;
}

/// <summary>悬浮小窗的单状态样式。</summary>
public sealed class OverlayStyle
{
    public string Text { get; set; } = "";
    public string Font { get; set; } = "Microsoft YaHei UI";
    public int Size { get; set; } = 18;
    public int Weight { get; set; } = 600;
    public int Transparent { get; set; } = 235;
    public string BgColor { get; set; } = "";
    public string TextColor { get; set; } = "0xFFFFFF";
    public string BasePosition { get; set; } = "topWindow";
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
}

/// <summary>窗口边框的单状态样式。</summary>
public sealed class BorderStyle
{
    public int Width { get; set; } = 3;
    public string Color { get; set; } = "";
}

/// <summary>光标符号 / 鼠标符号的单状态样式（图片、方块、文字三种形态共用）。</summary>
public sealed class SymbolStyle
{
    public string PicturePath { get; set; } = "";
    public int PictureOffsetX { get; set; } = -25;
    public int PictureOffsetY { get; set; }
    public int PictureWidth { get; set; } = 20;
    public int PictureHeight { get; set; } = 20;

    public string ShapeColor { get; set; } = "";
    public int ShapeOffsetX { get; set; }
    public int ShapeOffsetY { get; set; } = 30;
    public int ShapeWidth { get; set; } = 12;
    public int ShapeHeight { get; set; } = 12;
    public int ShapeTransparent { get; set; } = 255;

    public string TextContent { get; set; } = "";
    public string TextBgColor { get; set; } = "";
    public string TextColor { get; set; } = "0xFFFFFF";
    public string TextFont { get; set; } = "Microsoft YaHei UI";
    public int TextWeight { get; set; } = 600;
    public int TextTransparent { get; set; } = 255;
    public int TextSize { get; set; } = 16;
    public int TextOffsetX { get; set; }
    public int TextOffsetY { get; set; } = 30;
}

/// <summary>多显示器下的符号偏移（X/Y 为逻辑像素，按屏幕缩放换算）。</summary>
public sealed class ScreenOffset
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>输入法状态判定规则（自定义模式）：状态码规则, 转换码规则, 目标状态。</summary>
public sealed class ImeRule
{
    public string OpenRule { get; set; } = "";
    public string ConversionRule { get; set; } = "";
    public string State { get; set; } = "EN";

    public override string ToString() => $"{OpenRule},{ConversionRule},{State}";
}

/// <summary>
/// 一条规则（窗口规则 / 悬浮小窗 / 边框 / 符号 / 热键通用）。
/// Trigger 决定行为；Condition + Class/Title/Process 决定匹配；其余字段按规则类型使用。
/// </summary>
public sealed class InputTipRule
{
    public string Id { get; set; } = "";
    public string Process { get; set; } = "";
    public string Condition { get; set; } = "";
    public string Class { get; set; } = "";
    public string Title { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Offset { get; set; } = "";
    public string Capture { get; set; } = "";
    public string CaptureOffset { get; set; } = "";
    public string Hotkey { get; set; } = "";
    public string IdleTimer { get; set; } = "";
    public string TextMonitor { get; set; } = "";
    public string HotkeyMonitor { get; set; } = "";

    public InputTipRule Clone() => (InputTipRule)MemberwiseClone();
}

/// <summary>输入法状态提示的完整设置（%APPDATA%\ToolDock\inputtip\settings.json）。</summary>
public sealed class InputTipSettings
{
    // ---- 常规 ----
    public bool Enabled { get; set; } = true;
    public string Language { get; set; } = "zh-CN";
    public bool LaunchAtStartup { get; set; }
    public int PollInterval { get; set; } = 20;
    public int MenuFontSize { get; set; } = 16;
    public int MenuAnimation { get; set; } = 1;
    public bool EnableCustomTrayTip { get; set; }
    public string TrayTipTemplate { get; set; } = "工具坞 · 输入法提示\n%appState%";
    public bool EnableKeyStats { get; set; }
    public string KeyStatsTemplate { get; set; } = "今日按键：%keyCount%";

    // ---- 输入法 ----
    public string InputMethodDetectionMode { get; set; } = "general";
    public string InputMethodBaseState { get; set; } = "EN";
    public int InputMethodDetectionTimeout { get; set; } = 200;
    public bool KeepCapsLockWhenStateSwitch { get; set; }
    public bool KeepCapsLockWhenKeyboardSwitch { get; set; }
    public bool ExportState { get; set; }
    public List<ImeRule> ImeRules { get; set; } = [];

    // ---- 鼠标样式 ----
    public bool CursorActive { get; set; }
    public bool LoadOnlyIBeamCursor { get; set; }
    /// <summary>状态 → 光标目录名（默认 default-red 等，或 data/cursor 下的自定义目录）。</summary>
    public Dictionary<string, string> CursorPaths { get; set; } = [];

    // ---- 悬浮小窗 ----
    public bool OverlayActive { get; set; }
    public bool OverlayOnlyFocusScreen { get; set; }
    public int OverlayCornerPreference { get; set; } = 3;
    public int OverlayAnimation { get; set; } = 1;
    public bool OverlayReshowOnProcessChange { get; set; }
    public bool OverlayReshowOnTitleChange { get; set; }
    public bool OverlayReshowOnClassChange { get; set; }
    public bool OverlayShowOnNormal { get; set; } = true;
    public bool OverlayShowOnMaximized { get; set; } = true;
    public bool OverlayShowOnFullscreen { get; set; } = true;
    public int OverlayHideDelay { get; set; } = 2000;
    public string OverlayShowMode { get; set; } = "blacklist";
    public int OverlayTransparent { get; set; } = 255;
    public int OverlayEdgeStyle { get; set; }
    public Dictionary<string, OverlayStyle> OverlayStyles { get; set; } = [];

    // ---- 窗口边框 ----
    public bool BorderActive { get; set; }
    public bool BorderReshowOnProcessChange { get; set; }
    public bool BorderReshowOnTitleChange { get; set; }
    public bool BorderReshowOnClassChange { get; set; }
    public bool BorderShowOnNormal { get; set; } = true;
    public bool BorderShowOnMaximized { get; set; } = true;
    public bool BorderShowOnFullscreen { get; set; } = true;
    public bool BorderShowOnMaximizedTop { get; set; } = true;
    public bool BorderShowOnMaximizedBottom { get; set; } = true;
    public bool BorderShowOnMaximizedLeft { get; set; } = true;
    public bool BorderShowOnMaximizedRight { get; set; } = true;
    public bool BorderShowOnFullscreenTop { get; set; } = true;
    public bool BorderShowOnFullscreenBottom { get; set; } = true;
    public bool BorderShowOnFullscreenLeft { get; set; } = true;
    public bool BorderShowOnFullscreenRight { get; set; } = true;
    public int BorderHideDelay { get; set; }
    public string BorderShowMode { get; set; } = "blacklist";
    public int BorderWidthPinned { get; set; } = 3;
    public string BorderColorPinned { get; set; } = "0x00CCCC";
    public Dictionary<string, BorderStyle> BorderStyles { get; set; } = [];

    // ---- 光标符号 ----
    public int CaretSymbolType { get; set; }
    public int CaretSymbolHideDelay { get; set; }
    public int CaretSymbolTextEdgeStyle { get; set; }
    public int CaretSymbolShapeEdgeStyle { get; set; }
    public int CaretSymbolTextCornerPreference { get; set; } = 3;
    public int CaretSymbolShapeCornerPreference { get; set; } = 3;
    /// <summary>垂直偏移参考原点：above / below。</summary>
    public string CaretSymbolOriginY { get; set; } = "below";
    public Dictionary<string, SymbolStyle> CaretSymbolStyles { get; set; } = [];

    // ---- 鼠标符号 ----
    public int CursorSymbolType { get; set; }
    public string CursorSymbolShowMode { get; set; } = "blacklist";
    public int CursorSymbolHideDelay { get; set; }
    public int CursorSymbolTextEdgeStyle { get; set; }
    public int CursorSymbolShapeEdgeStyle { get; set; }
    public int CursorSymbolTextCornerPreference { get; set; } = 3;
    public int CursorSymbolShapeCornerPreference { get; set; } = 3;
    public Dictionary<string, SymbolStyle> CursorSymbolStyles { get; set; } = [];

    // ---- 屏幕偏移 ----
    public Dictionary<string, ScreenOffset> CaretScreenOffsets { get; set; } = [];
    public Dictionary<string, ScreenOffset> CursorScreenOffsets { get; set; } = [];

    // ---- 规则 ----
    public List<InputTipRule> WindowRules { get; set; } = [];
    public List<InputTipRule> OverlayRules { get; set; } = [];
    public List<InputTipRule> BorderRules { get; set; } = [];
    public List<InputTipRule> CaretSymbolRules { get; set; } = [];
    public List<InputTipRule> CursorSymbolRules { get; set; } = [];
    public List<InputTipRule> HotkeyRules { get; set; } = [];

    /// <summary>JetBrains 桥接（需要独立 JAB 进程，工具箱内暂不启用，保留字段以兼容配置）。</summary>
    public bool SymbolJabActive { get; set; }

    [JsonIgnore]
    public string ConfigDirectory { get; set; } = "";

    [JsonIgnore]
    public string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    [JsonIgnore]
    public string AssetsDirectory => Path.Combine(ConfigDirectory, "assets");

    [JsonIgnore]
    public string CursorDirectory => Path.Combine(AssetsDirectory, "cursor");

    [JsonIgnore]
    public string SymbolDirectory => Path.Combine(AssetsDirectory, "symbol");

    [JsonIgnore]
    public string ExportStatePath => Path.Combine(ConfigDirectory, "InputTip.State");

    public static InputTipSettings CreateDefault(string configDirectory)
    {
        var settings = new InputTipSettings { ConfigDirectory = configDirectory };
        settings.Normalize();
        return settings;
    }

    /// <summary>补齐缺失的状态条目、校正非法值（对齐上游 normalizeConfig 的容错）。</summary>
    public void Normalize()
    {
        if (!InputTipStates.IsState(InputMethodBaseState))
        {
            InputMethodBaseState = "EN";
        }
        if (InputMethodDetectionMode is not ("general" or "custom"))
        {
            InputMethodDetectionMode = "general";
        }
        PollInterval = Math.Clamp(PollInterval <= 0 ? 20 : PollInterval, 5, 100);
        MenuFontSize = Math.Clamp(MenuFontSize, 8, 50);
        InputMethodDetectionTimeout = Math.Clamp(InputMethodDetectionTimeout, 20, 2000);

        OverlayTransparent = Math.Clamp(OverlayTransparent, 0, 255);
        OverlayHideDelay = Math.Max(0, OverlayHideDelay);
        BorderHideDelay = Math.Max(0, BorderHideDelay);
        CaretSymbolHideDelay = Math.Max(0, CaretSymbolHideDelay);
        CursorSymbolHideDelay = Math.Max(0, CursorSymbolHideDelay);

        foreach (string state in InputTipStates.All)
        {
            if (!OverlayStyles.ContainsKey(state))
            {
                OverlayStyles[state] = new OverlayStyle
                {
                    Text = InputTipStates.ShortLabels[state],
                    BgColor = InputTipStates.Colors[state],
                    OffsetY = 6,
                };
            }
            if (!BorderStyles.ContainsKey(state))
            {
                BorderStyles[state] = new BorderStyle
                {
                    Color = InputTipStates.Colors[state],
                };
            }
            if (!CaretSymbolStyles.ContainsKey(state))
            {
                CaretSymbolStyles[state] = CreateDefaultSymbol(state, caret: true);
            }
            if (!CursorSymbolStyles.ContainsKey(state))
            {
                CursorSymbolStyles[state] = CreateDefaultSymbol(state, caret: false);
            }
            if (!CursorPaths.ContainsKey(state) || string.IsNullOrWhiteSpace(CursorPaths[state]))
            {
                CursorPaths[state] = "default-" + InputTipStates.ColorNames[state];
            }
        }

        // 字典键统一按状态名大小写归一
        NormalizeKeys(OverlayStyles);
        NormalizeKeys(BorderStyles);
        NormalizeKeys(CaretSymbolStyles);
        NormalizeKeys(CursorSymbolStyles);
        NormalizeKeys(CursorPaths);

        foreach (var style in OverlayStyles.Values)
        {
            style.Size = Math.Clamp(style.Size, 8, 100);
            style.Weight = NormalizeWeight(style.Weight);
            style.Transparent = Math.Clamp(style.Transparent, 0, 255);
            style.TextColor = NormalizeColor(style.TextColor, "0xFFFFFF");
            style.BgColor = NormalizeColor(style.BgColor, "");
        }
        foreach (var style in BorderStyles.Values)
        {
            style.Width = Math.Clamp(style.Width, 1, 100);
            style.Color = NormalizeColor(style.Color, "");
        }
        foreach (var style in CaretSymbolStyles.Values)
        {
            NormalizeSymbol(style);
        }
        foreach (var style in CursorSymbolStyles.Values)
        {
            NormalizeSymbol(style);
        }

        WindowRules ??= [];
        OverlayRules ??= [];
        BorderRules ??= [];
        CaretSymbolRules ??= [];
        CursorSymbolRules ??= [];
        HotkeyRules ??= [];
        ImeRules ??= [];
        foreach (var rule in AllRules())
        {
            rule.Id ??= "";
            rule.Process ??= "";
            rule.Condition ??= "";
            rule.Class ??= "";
            rule.Title ??= "";
            rule.Trigger ??= "";
            rule.Offset ??= "";
            rule.Capture ??= "";
            rule.CaptureOffset ??= "";
            rule.Hotkey ??= "";
            rule.IdleTimer ??= "";
            rule.TextMonitor ??= "";
            rule.HotkeyMonitor ??= "";
        }
    }

    public IEnumerable<InputTipRule> AllRules()
    {
        foreach (var rule in WindowRules) yield return rule;
        foreach (var rule in OverlayRules) yield return rule;
        foreach (var rule in BorderRules) yield return rule;
        foreach (var rule in CaretSymbolRules) yield return rule;
        foreach (var rule in CursorSymbolRules) yield return rule;
        foreach (var rule in HotkeyRules) yield return rule;
    }

    private static void NormalizeKeys<T>(Dictionary<string, T> map)
    {
        foreach (string state in InputTipStates.All)
        {
            foreach (string key in map.Keys.Where(k => string.Equals(k, state, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (key != state)
                {
                    map[state] = map[key];
                    map.Remove(key);
                }
            }
        }
    }

    private static SymbolStyle CreateDefaultSymbol(string state, bool caret)
    {
        string colorName = InputTipStates.ColorNames[state];
        return new SymbolStyle
        {
            PicturePath = "default-triangle-" + colorName + ".png",
            PictureOffsetX = caret ? -25 : -25,
            PictureOffsetY = caret ? 0 : 0,
            PictureWidth = 20,
            PictureHeight = 20,
            ShapeColor = InputTipStates.Colors[state],
            ShapeOffsetX = 0,
            ShapeOffsetY = caret ? 30 : 30,
            ShapeWidth = 12,
            ShapeHeight = 12,
            TextContent = InputTipStates.SymbolLabels[state],
            TextBgColor = InputTipStates.Colors[state],
            TextColor = "0xFFFFFF",
            TextSize = 16,
            TextOffsetX = 0,
            TextOffsetY = caret ? 30 : 30,
        };
    }

    private static void NormalizeSymbol(SymbolStyle style)
    {
        style.PictureOffsetX = Math.Clamp(style.PictureOffsetX, -9999, 9999);
        style.PictureOffsetY = Math.Clamp(style.PictureOffsetY, -9999, 9999);
        style.PictureWidth = Math.Clamp(style.PictureWidth <= 0 ? 20 : style.PictureWidth, 1, 999);
        style.PictureHeight = Math.Clamp(style.PictureHeight <= 0 ? 20 : style.PictureHeight, 1, 999);
        style.ShapeOffsetX = Math.Clamp(style.ShapeOffsetX, -9999, 9999);
        style.ShapeOffsetY = Math.Clamp(style.ShapeOffsetY, -9999, 9999);
        style.ShapeWidth = Math.Clamp(style.ShapeWidth <= 0 ? 12 : style.ShapeWidth, 1, 999);
        style.ShapeHeight = Math.Clamp(style.ShapeHeight <= 0 ? 12 : style.ShapeHeight, 1, 999);
        style.ShapeTransparent = Math.Clamp(style.ShapeTransparent, 0, 255);
        style.TextOffsetX = Math.Clamp(style.TextOffsetX, -9999, 9999);
        style.TextOffsetY = Math.Clamp(style.TextOffsetY, -9999, 9999);
        style.TextSize = Math.Clamp(style.TextSize <= 0 ? 16 : style.TextSize, 8, 100);
        style.TextWeight = NormalizeWeight(style.TextWeight);
        style.TextTransparent = Math.Clamp(style.TextTransparent, 0, 255);
        style.TextColor = NormalizeColor(style.TextColor, "0xFFFFFF");
        style.TextBgColor = NormalizeColor(style.TextBgColor, "");
        style.ShapeColor = NormalizeColor(style.ShapeColor, "");
    }

    private static int NormalizeWeight(int weight)
    {
        int rounded = (int)Math.Round(Math.Abs(weight) / 100.0) * 100;
        return rounded is >= 100 and <= 900 ? rounded : 400;
    }

    /// <summary>颜色统一成 0xRRGGBB；空值表示透明/无。</summary>
    public static string NormalizeColor(string? value, string fallback)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return fallback;
        }
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = "0x" + text;
        }
        string hex = text[2..];
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
        {
            return fallback;
        }
        return "0x" + hex.ToUpperInvariant();
    }

    public static int ParseColor(string? value, int fallback)
    {
        string text = NormalizeColor(value, "");
        if (text.Length != 8)
        {
            return fallback;
        }
        return int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out int rgb) ? rgb : fallback;
    }

    // ---- 读写 ----

    public static InputTipSettings Load(string directory)
    {
        string path = Path.Combine(directory, "settings.json");
        InputTipSettings? settings = null;
        try
        {
            if (File.Exists(path))
            {
                settings = JsonSerializer.Deserialize(File.ReadAllText(path), InputTipJsonContext.Default.InputTipSettings);
            }
        }
        catch
        {
            settings = null;
        }
        settings ??= new InputTipSettings();
        settings.ConfigDirectory = directory;
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            string path = ConfigPath;
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, InputTipJsonContext.Default.InputTipSettings));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            // 配置目录不可写：引擎继续用内存中的设置运行
        }
    }
}

/// <summary>NativeAOT 下不能反射序列化，统一走源生成上下文。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(InputTipSettings))]
public partial class InputTipJsonContext : JsonSerializerContext
{
}

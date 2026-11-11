using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ToolDock.App.Services;
using ToolDock.InputTip;

namespace ToolDock.App.Views;

/// <summary>
/// 输入法状态提示（InputTip 原生移植）设置页。
/// 覆盖上游全部功能分类：常规 / 输入法 / 鼠标样式 / 悬浮小窗 / 光标符号 /
/// 鼠标符号 / 窗口边框 / 规则 / 更多。保存时写 settings.json 并让 Agent 重载引擎。
/// </summary>
public partial class InputTipPage : UserControl
{
    private const string ToolKey = "inputtip";

    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "inputtip");
    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "settings.json");

    private static readonly (string Value, string Label)[] TriggerOptions =
    [
        ("", "无操作"),
        ("switchStateCaps-CapsLock", "状态切换 - 大写 (CapsLock)"),
        ("switchStateCN-IME", "状态切换 - 中文 (IME)"),
        ("switchStateCN-LShift", "状态切换 - 中文 (LShift)"),
        ("switchStateCN-RShift", "状态切换 - 中文 (RShift)"),
        ("switchStateCN-CtrlSpace", "状态切换 - 中文 (Ctrl+Space)"),
        ("switchStateEN-IME", "状态切换 - 英文 (IME)"),
        ("switchStateEN-LShift", "状态切换 - 英文 (LShift)"),
        ("switchStateEN-RShift", "状态切换 - 英文 (RShift)"),
        ("switchStateEN-CtrlSpace", "状态切换 - 英文 (Ctrl+Space)"),
        ("switchStateCN/EN-IME", "状态切换 - 中/英 (IME)"),
        ("switchStateCN/EN-LShift", "状态切换 - 中/英 (LShift)"),
        ("switchStateCN/EN-RShift", "状态切换 - 中/英 (RShift)"),
        ("switchStateCN/EN-CtrlSpace", "状态切换 - 中/英 (Ctrl+Space)"),
        ("switchKeyboardCN", "键盘切换 - 中文"),
        ("switchKeyboardUS", "键盘切换 - 英文"),
        ("switchKeyboardJP", "键盘切换 - 日文"),
        ("switchKeyboardJPHiragana", "键盘切换 - 日文 (あ)"),
        ("switchKeyboardJPKatakana", "键盘切换 - 日文 (カ)"),
        ("switchKeyboardJPHalfKana", "键盘切换 - 日文 (_カ)"),
        ("switchKeyboardJPFullAlpha", "键盘切换 - 日文 (全Ａ)"),
        ("switchKeyboardJPHalfAlpha", "键盘切换 - 日文 (A)"),
        ("switchKeyboardKR", "键盘切换 - 韩文"),
        ("switchKeyboardKRHangul", "键盘切换 - 韩文 (한)"),
        ("switchKeyboardKRAlpha", "键盘切换 - 韩文 (A)"),
        ("toggle", "应用控制 - 暂停/恢复"),
        ("pause", "应用控制 - 暂停"),
        ("resume", "应用控制 - 恢复"),
        ("exit", "应用控制 - 退出（停用工具）"),
        ("restart", "应用控制 - 重启引擎"),
        ("showStateCode", "显示/隐藏状态码和转换码"),
        ("showCaptureMode", "显示/隐藏光标捕获模式"),
        ("toggleWindowTop", "窗口状态 - 切换置顶"),
        ("setWindowTop", "窗口状态 - 置顶"),
        ("cancelWindowTop", "窗口状态 - 取消置顶"),
        ("ignoreStateSwitch", "状态切换 - 忽略"),
        ("ignoreKeyboardSwitch", "键盘切换 - 忽略"),
    ];

    private static readonly (string Value, string Label)[] ConditionOptions =
    [
        ("", "无附加条件"),
        ("class", "匹配类名"),
        ("title", "匹配标题"),
        ("classAndTitle", "匹配类名和标题"),
    ];

    private static readonly (string Value, string Label)[] WindowConditionOptions =
    [
        ("", "无附加条件"),
        ("class", "匹配类名"),
        ("title", "匹配标题"),
        ("classAndTitle", "匹配类名和标题"),
        ("textMonitor", "文本监控"),
        ("hotkeyMonitor", "热键监控"),
        ("idleTimer", "空闲计时器"),
    ];

    private static readonly (string Value, string Label)[] OverlayPositions =
    [
        ("topWindow", "窗口顶部"),
        ("bottomWindow", "窗口底部"),
        ("leftWindow", "窗口左侧"),
        ("rightWindow", "窗口右侧"),
        ("centerWindow", "窗口中心"),
        ("topLeftWindow", "窗口左上"),
        ("topRightWindow", "窗口右上"),
        ("bottomLeftWindow", "窗口左下"),
        ("bottomRightWindow", "窗口右下"),
        ("top", "屏幕顶部"),
        ("bottom", "屏幕底部"),
        ("left", "屏幕左侧"),
        ("right", "屏幕右侧"),
        ("center", "屏幕中心"),
        ("topLeft", "屏幕左上"),
        ("topRight", "屏幕右上"),
        ("bottomLeft", "屏幕左下"),
        ("bottomRight", "屏幕右下"),
        ("bottomWorkArea", "工作区底部"),
        ("bottomLeftWorkArea", "工作区左下"),
        ("bottomRightWorkArea", "工作区右下"),
    ];

    private static readonly (string Value, string Label)[] ShowModes =
    [
        ("blacklist", "黑名单（列表内隐藏）"),
        ("whitelist", "白名单（仅列表内显示）"),
    ];

    private InputTipSettings _settings = new();
    private readonly List<Action> _collectors = new();
    private bool _syncing;

    public InputTipPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AgentLink.InputTipUpdated -= OnAgentConfig;
            AgentLink.InputTipUpdated += OnAgentConfig;
            LoadSettings();
            BuildPage();
            ApplyToolState(ToolEnabledToggle.IsChecked == true);
            AgentLink.Instance.RequestInputTip();
            UpdateStatus();
        };
        Unloaded += (_, _) => AgentLink.InputTipUpdated -= OnAgentConfig;
    }

    // ------------------------------------------------------------------
    // 配置读写
    // ------------------------------------------------------------------

    private void LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            if (!File.Exists(ConfigPath))
            {
                var defaults = InputTipSettings.CreateDefault(ConfigDirectory);
                defaults.Save();
            }
            _settings = InputTipSettings.Load(ConfigDirectory);
        }
        catch
        {
            _settings = InputTipSettings.CreateDefault(ConfigDirectory);
        }
        ToolEnabledToggle.IsChecked = _settings.Enabled;
        ConfigPathText.Text = ConfigPath;
        VersionText.Text = "1.0";
    }

    private void Collect()
    {
        foreach (var collector in _collectors)
        {
            try
            {
                collector();
            }
            catch
            {
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Collect();
        _settings.Enabled = ToolEnabledToggle.IsChecked == true;
        _settings.Normalize();
        try
        {
            _settings.Save();
            SaveStatus.Text = "已保存，正在重启引擎…";
            AgentLink.Instance.SendInputTipRestart();
            AgentLink.Instance.SendInputTipEnabled(_settings.Enabled);
            UpdateStatus();
        }
        catch (Exception exception)
        {
            SaveStatus.Text = "保存失败：" + exception.Message;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = InputTipSettings.CreateDefault(ConfigDirectory);
            _settings.Enabled = ToolEnabledToggle.IsChecked == true;
            _settings.Save();
            BuildPage();
            SaveStatus.Text = "已恢复默认配置";
            AgentLink.Instance.SendInputTipRestart();
        }
        catch (Exception exception)
        {
            SaveStatus.Text = "恢复默认失败：" + exception.Message;
        }
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ConfigDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            SaveStatus.Text = "打开配置目录失败：" + exception.Message;
        }
    }

    // ------------------------------------------------------------------
    // 页面构建
    // ------------------------------------------------------------------

    private void BuildPage()
    {
        _collectors.Clear();
        GeneralHost.Children.Clear();
        ImeHost.Children.Clear();
        CursorHost.Children.Clear();
        OverlayHost.Children.Clear();
        CaretHost.Children.Clear();
        CursorSymbolHost.Children.Clear();
        BorderHost.Children.Clear();
        RulesHost.Children.Clear();
        MoreHost.Children.Clear();

        BuildGeneral();
        BuildIme();
        BuildCursor();
        BuildOverlay();
        BuildSymbol(CaretHost, caret: true);
        BuildSymbol(CursorSymbolHost, caret: false);
        BuildBorder();
        BuildRules();
        BuildMore();
    }

    private void BuildGeneral()
    {
        Section(GeneralHost, "提示方案");
        Card(GeneralHost,
            ToggleRow("鼠标样式提示", "按状态切换整套系统光标", () => _settings.CursorActive, v => _settings.CursorActive = v),
            ToggleRow("悬浮小窗提示", "在窗口或屏幕边缘显示当前状态小窗", () => _settings.OverlayActive, v => _settings.OverlayActive = v),
            SymbolToggleRow(caret: true),
            SymbolToggleRow(caret: false),
            ToggleRow("窗口边框提示", "给前台窗口加彩色边框", () => _settings.BorderActive, v => _settings.BorderActive = v));

        Section(GeneralHost, "运行");
        Card(GeneralHost,
            IntRow("轮询间隔（毫秒）", "越小响应越快、CPU 占用略高；上游默认 20", () => _settings.PollInterval, v => _settings.PollInterval = v),
            IntRow("菜单字号", "动作菜单/提示的文字大小", () => _settings.MenuFontSize, v => _settings.MenuFontSize = v),
            ComboRow("菜单动画", "设置窗口/动作菜单的显示动画", [("0", "无"), ("1", "淡入")], () => _settings.MenuAnimation.ToString(), v => _settings.MenuAnimation = int.TryParse(v, out var n) ? n : 1),
            ToggleRow("自定义托盘提示", "用下面的模板替换托盘状态行", () => _settings.EnableCustomTrayTip, v => _settings.EnableCustomTrayTip = v),
            TextRow("托盘提示模板", "%appState% = 运行状态，%keyCount% = 今日按键数，%\\n% = 换行", () => _settings.TrayTipTemplate, v => _settings.TrayTipTemplate = v),
            ToggleRow("统计今日按键数", "在托盘提示里显示按键统计", () => _settings.EnableKeyStats, v => _settings.EnableKeyStats = v),
            TextRow("按键统计模板", "%keyCount% = 今日按键数", () => _settings.KeyStatsTemplate, v => _settings.KeyStatsTemplate = v));
    }

    private void BuildIme()
    {
        Section(ImeHost, "状态检测");
        Card(ImeHost,
            ComboRow("检测模式", "general = 自动识别中文键盘的 CN/EN；custom = 按下方规则匹配状态码/转换码",
                [("general", "通用（自动学习）"), ("custom", "自定义规则")],
                () => _settings.InputMethodDetectionMode, v => _settings.InputMethodDetectionMode = v),
            ComboRow("默认状态", "自定义模式下所有规则都不匹配时使用", [("EN", "英文"), ("CN", "中文")],
                () => _settings.InputMethodBaseState, v => _settings.InputMethodBaseState = v),
            IntRow("检测超时（毫秒）", "等待 IME 返回状态码/转换码的最长时间", () => _settings.InputMethodDetectionTimeout, v => _settings.InputMethodDetectionTimeout = v),
            ToggleRow("切换状态时保留大写锁定", "大写锁定开启时不自动取消", () => _settings.KeepCapsLockWhenStateSwitch, v => _settings.KeepCapsLockWhenStateSwitch = v),
            ToggleRow("切换键盘时保留大写锁定", "切换 CN/US/JP/KR 布局时不自动取消大写", () => _settings.KeepCapsLockWhenKeyboardSwitch, v => _settings.KeepCapsLockWhenKeyboardSwitch = v),
            ToggleRow("导出当前状态", "把状态写入 %APPDATA%\\ToolDock\\inputtip\\InputTip.State，供 VSCode 等外部程序读取", () => _settings.ExportState, v => _settings.ExportState = v));

        Section(ImeHost, "自定义状态规则（状态码, 转换码, 状态）");
        Card(ImeHost,
            RuleTableRow(
                ["状态码规则", "转换码规则", "状态"],
                _settings.ImeRules.Select(r => new[] { r.OpenRule, r.ConversionRule, r.State }).ToList(),
                (index) => EditImeRule(index),
                (index) => DeleteImeRule(index),
                () => EditImeRule(-1)));
    }

    private void BuildCursor()
    {
        Section(CursorHost, "鼠标样式");
        Card(CursorHost,
            ToggleRow("启用鼠标样式提示", "按状态切换系统光标", () => _settings.CursorActive, v => _settings.CursorActive = v),
            ToggleRow("只修改文本光标", "仅替换 IBeam（文本选择）光标，其余保持系统样式", () => _settings.LoadOnlyIBeamCursor, v => _settings.LoadOnlyIBeamCursor = v));

        var directories = ListCursorDirectories();
        foreach (string state in InputTipStates.All)
        {
            string key = state;
            Card(CursorHost,
                ComboRow(InputTipStates.Labels[key], "选择光标目录（default-* 为内置配色，也可把 .cur/.ani 放进 assets/cursor）",
                    directories.Select(d => (d, d)).ToArray(),
                    () => _settings.CursorPaths.TryGetValue(key, out var v) ? v : "",
                    v => _settings.CursorPaths[key] = v));
        }
    }

    private void BuildOverlay()
    {
        Section(OverlayHost, "悬浮小窗");
        Card(OverlayHost,
            ToggleRow("启用悬浮小窗", "按状态显示小窗", () => _settings.OverlayActive, v => _settings.OverlayActive = v),
            ToggleRow("只显示在当前屏幕", "多显示器时只在窗口所在屏幕显示", () => _settings.OverlayOnlyFocusScreen, v => _settings.OverlayOnlyFocusScreen = v),
            ComboRow("显示名单", "黑名单 = 列表内隐藏；白名单 = 仅列表内显示", ShowModes,
                () => _settings.OverlayShowMode, v => _settings.OverlayShowMode = v),
            IntRow("自动隐藏延迟（毫秒）", "0 表示不自动隐藏", () => _settings.OverlayHideDelay, v => _settings.OverlayHideDelay = v),
            ComboRow("显示动画", "小窗出现时的动画", [("0", "无"), ("1", "淡入"), ("2", "滑入")],
                () => _settings.OverlayAnimation.ToString(), v => _settings.OverlayAnimation = int.TryParse(v, out var n) ? n : 1),
            ComboRow("窗口圆角", "Win11 窗口边角样式", [("0", "无"), ("1", "直角"), ("2", "圆角"), ("3", "小圆角")],
                () => _settings.OverlayCornerPreference.ToString(), v => _settings.OverlayCornerPreference = int.TryParse(v, out var n) ? n : 3),
            ComboRow("窗口边缘", "小窗的边框样式", [("0", "无"), ("1", "对话框边框"), ("2", "客户区凹陷"), ("3", "静态边框")],
                () => _settings.OverlayEdgeStyle.ToString(), v => _settings.OverlayEdgeStyle = int.TryParse(v, out var n) ? n : 0),
            ToggleRow("进程变化时重新显示", "", () => _settings.OverlayReshowOnProcessChange, v => _settings.OverlayReshowOnProcessChange = v),
            ToggleRow("标题变化时重新显示", "", () => _settings.OverlayReshowOnTitleChange, v => _settings.OverlayReshowOnTitleChange = v),
            ToggleRow("类名变化时重新显示", "", () => _settings.OverlayReshowOnClassChange, v => _settings.OverlayReshowOnClassChange = v),
            ToggleRow("普通窗口显示", "", () => _settings.OverlayShowOnNormal, v => _settings.OverlayShowOnNormal = v),
            ToggleRow("最大化窗口显示", "", () => _settings.OverlayShowOnMaximized, v => _settings.OverlayShowOnMaximized = v),
            ToggleRow("全屏窗口显示", "", () => _settings.OverlayShowOnFullscreen, v => _settings.OverlayShowOnFullscreen = v));

        var fonts = ListFonts();
        foreach (string state in InputTipStates.All)
        {
            string key = state;
            if (!_settings.OverlayStyles.TryGetValue(key, out var style))
            {
                continue;
            }
            Section(OverlayHost, InputTipStates.Labels[key]);
            Card(OverlayHost,
                TextRow("显示文字", "留空则不显示该状态的小窗", () => style.Text, v => style.Text = v),
                ComboRow("位置", "小窗相对窗口/屏幕的位置", OverlayPositions,
                    () => style.BasePosition, v => style.BasePosition = v),
                IntRow("水平偏移", "逻辑像素，按屏幕缩放换算", () => style.OffsetX, v => style.OffsetX = v),
                IntRow("垂直偏移", "", () => style.OffsetY, v => style.OffsetY = v),
                ComboRow("字体", "", fonts.Select(f => (f, f)).ToArray(), () => style.Font, v => style.Font = v),
                IntRow("字号", "8-100", () => style.Size, v => style.Size = v),
                IntRow("字重", "100-900", () => style.Weight, v => style.Weight = v),
                IntRow("不透明度", "0-255", () => style.Transparent, v => style.Transparent = v),
                ColorRow("背景色", "留空 = 透明抠色", () => style.BgColor, v => style.BgColor = v),
                ColorRow("文字颜色", "0xRRGGBB", () => style.TextColor, v => style.TextColor = v));
        }

        Section(OverlayHost, "显示/隐藏规则");
        Card(OverlayHost,
            RuleTableRow(["进程", "条件", "类名", "标题", "行为"],
                _settings.OverlayRules.Select(r => new[] { r.Process, ConditionLabel(r.Condition), r.Class, r.Title, r.Trigger == "show" ? "显示" : "隐藏" }).ToList(),
                i => EditRule(_settings.OverlayRules, i, RuleKind.Display, ["show", "hide"]),
                i => DeleteRule(_settings.OverlayRules, i),
                () => EditRule(_settings.OverlayRules, -1, RuleKind.Display, ["show", "hide"])));
    }

    private void BuildSymbol(StackPanel host, bool caret)
    {
        string prefix = caret ? "光标符号" : "鼠标符号";
        int type = caret ? _settings.CaretSymbolType : _settings.CursorSymbolType;
        Section(host, prefix);
        Card(host,
            ComboRow("显示形态", "图片 / 方块 / 文字", [("0", "不显示"), ("1", "图片"), ("2", "方块"), ("3", "文字")],
                () => type.ToString(), v =>
                {
                    int parsed = int.TryParse(v, out var n) ? n : 0;
                    if (caret) _settings.CaretSymbolType = parsed; else _settings.CursorSymbolType = parsed;
                    type = parsed;
                }),
            IntRow("自动隐藏延迟（毫秒）", "0 表示不自动隐藏；按住左键时不会隐藏",
                () => caret ? _settings.CaretSymbolHideDelay : _settings.CursorSymbolHideDelay,
                v => { if (caret) _settings.CaretSymbolHideDelay = v; else _settings.CursorSymbolHideDelay = v; }),
            ComboRow("垂直参考原点", "符号贴在光标的哪一侧", [("below", "下方"), ("above", "上方")],
                () => _settings.CaretSymbolOriginY, v => _settings.CaretSymbolOriginY = v),
            caret
                ? ToggleRow("JetBrains（JAB）支持", "上游通过独立 JAB 进程接入 JetBrains 系列 IDE；工具箱内暂未启用（保留配置项）",
                    () => _settings.SymbolJabActive, v => _settings.SymbolJabActive = v)
                : ComboRow("显示名单", "黑名单 = 列表内隐藏；白名单 = 仅列表内显示", ShowModes,
                    () => _settings.CursorSymbolShowMode, v => _settings.CursorSymbolShowMode = v));

        if (type == 1)
        {
            Section(host, "图片形态");
            var pictures = ListSymbolPictures();
            foreach (string state in InputTipStates.All)
            {
                string key = state;
                var style = caret ? _settings.CaretSymbolStyles[key] : _settings.CursorSymbolStyles[key];
                Card(host,
                    ComboRow(InputTipStates.Labels[key] + " 图片", "assets/symbol 下的 PNG", pictures.Select(p => (p, p)).ToArray(),
                        () => style.PicturePath, v => style.PicturePath = v),
                    IntRow("水平偏移", "", () => style.PictureOffsetX, v => style.PictureOffsetX = v),
                    IntRow("垂直偏移", "", () => style.PictureOffsetY, v => style.PictureOffsetY = v),
                    IntRow("宽度", "", () => style.PictureWidth, v => style.PictureWidth = v),
                    IntRow("高度", "", () => style.PictureHeight, v => style.PictureHeight = v));
            }
        }
        else if (type == 2)
        {
            Section(host, "方块形态");
            Card(host,
                ComboRow("窗口圆角", "", [("0", "无"), ("1", "直角"), ("2", "圆角"), ("3", "小圆角")],
                    () => (caret ? _settings.CaretSymbolShapeCornerPreference : _settings.CursorSymbolShapeCornerPreference).ToString(),
                    v => { if (caret) _settings.CaretSymbolShapeCornerPreference = int.TryParse(v, out var n) ? n : 3; else _settings.CursorSymbolShapeCornerPreference = int.TryParse(v, out var n) ? n : 3; }),
                ComboRow("窗口边缘", "", [("0", "无"), ("1", "对话框边框"), ("2", "客户区凹陷"), ("3", "静态边框")],
                    () => (caret ? _settings.CaretSymbolShapeEdgeStyle : _settings.CursorSymbolShapeEdgeStyle).ToString(),
                    v => { if (caret) _settings.CaretSymbolShapeEdgeStyle = int.TryParse(v, out var n) ? n : 0; else _settings.CursorSymbolShapeEdgeStyle = int.TryParse(v, out var n) ? n : 0; }));
            foreach (string state in InputTipStates.All)
            {
                string key = state;
                var style = caret ? _settings.CaretSymbolStyles[key] : _settings.CursorSymbolStyles[key];
                Card(host,
                    ColorRow(InputTipStates.Labels[key] + " 颜色", "留空 = 不显示该状态", () => style.ShapeColor, v => style.ShapeColor = v),
                    IntRow("水平偏移", "", () => style.ShapeOffsetX, v => style.ShapeOffsetX = v),
                    IntRow("垂直偏移", "", () => style.ShapeOffsetY, v => style.ShapeOffsetY = v),
                    IntRow("宽度", "", () => style.ShapeWidth, v => style.ShapeWidth = v),
                    IntRow("高度", "", () => style.ShapeHeight, v => style.ShapeHeight = v),
                    IntRow("不透明度", "0-255", () => style.ShapeTransparent, v => style.ShapeTransparent = v));
            }
        }
        else if (type == 3)
        {
            Section(host, "文字形态");
            Card(host,
                ComboRow("窗口圆角", "", [("0", "无"), ("1", "直角"), ("2", "圆角"), ("3", "小圆角")],
                    () => (caret ? _settings.CaretSymbolTextCornerPreference : _settings.CursorSymbolTextCornerPreference).ToString(),
                    v => { if (caret) _settings.CaretSymbolTextCornerPreference = int.TryParse(v, out var n) ? n : 3; else _settings.CursorSymbolTextCornerPreference = int.TryParse(v, out var n) ? n : 3; }),
                ComboRow("窗口边缘", "", [("0", "无"), ("1", "对话框边框"), ("2", "客户区凹陷"), ("3", "静态边框")],
                    () => (caret ? _settings.CaretSymbolTextEdgeStyle : _settings.CursorSymbolTextEdgeStyle).ToString(),
                    v => { if (caret) _settings.CaretSymbolTextEdgeStyle = int.TryParse(v, out var n) ? n : 0; else _settings.CursorSymbolTextEdgeStyle = int.TryParse(v, out var n) ? n : 0; }));
            var fonts = ListFonts();
            foreach (string state in InputTipStates.All)
            {
                string key = state;
                var style = caret ? _settings.CaretSymbolStyles[key] : _settings.CursorSymbolStyles[key];
                Card(host,
                    TextRow(InputTipStates.Labels[key] + " 文字", "留空 = 不显示该状态", () => style.TextContent, v => style.TextContent = v),
                    ColorRow("背景色", "留空 = 透明抠色", () => style.TextBgColor, v => style.TextBgColor = v),
                    ColorRow("文字颜色", "0xRRGGBB", () => style.TextColor, v => style.TextColor = v),
                    ComboRow("字体", "", fonts.Select(f => (f, f)).ToArray(), () => style.TextFont, v => style.TextFont = v),
                    IntRow("字号", "8-100", () => style.TextSize, v => style.TextSize = v),
                    IntRow("字重", "100-900", () => style.TextWeight, v => style.TextWeight = v),
                    IntRow("不透明度", "0-255", () => style.TextTransparent, v => style.TextTransparent = v),
                    IntRow("水平偏移", "", () => style.TextOffsetX, v => style.TextOffsetX = v),
                    IntRow("垂直偏移", "", () => style.TextOffsetY, v => style.TextOffsetY = v));
            }
        }

        Section(host, "屏幕偏移");
        var offsets = caret ? _settings.CaretScreenOffsets : _settings.CursorScreenOffsets;
        for (int screen = 1; screen <= 4; screen++)
        {
            int number = screen;
            string key = number.ToString();
            if (!offsets.TryGetValue(key, out var offset))
            {
                offset = new ScreenOffset();
                offsets[key] = offset;
            }
            Card(host,
                IntRow($"屏幕 {number} · X", "逻辑像素", () => offset.X, v => offset.X = v),
                IntRow($"屏幕 {number} · Y", "逻辑像素", () => offset.Y, v => offset.Y = v));
        }

        Section(host, "规则");
        var showHide = caret ? _settings.CaretSymbolRules : _settings.CursorSymbolRules;
        var sectionKind = caret ? RuleKind.Caret : RuleKind.Display;
        Card(host,
            RuleTableRow(["进程", "条件", "类名", "标题", "行为"],
                showHide.Where(r => r.Trigger is "show" or "hide")
                    .Select(r => new[] { r.Process, ConditionLabel(r.Condition), r.Class, r.Title, r.Trigger == "show" ? "显示" : "隐藏" }).ToList(),
                i => EditRule(showHide, FindIndex(showHide, r => r.Trigger is "show" or "hide", i), sectionKind, ["show", "hide"]),
                i => DeleteRule(showHide, FindIndex(showHide, r => r.Trigger is "show" or "hide", i)),
                () => EditRule(showHide, -1, sectionKind, ["show", "hide"])));

        if (caret)
        {
            Card(host,
                RuleTableRow(["进程", "条件", "类名", "标题", "偏移量"],
                    _settings.CaretSymbolRules.Where(r => r.Trigger == "offset")
                        .Select(r => new[] { r.Process, ConditionLabel(r.Condition), r.Class, r.Title, r.Offset }).ToList(),
                    i => EditRule(_settings.CaretSymbolRules, FindIndex(_settings.CaretSymbolRules, r => r.Trigger == "offset", i), RuleKind.Offset, ["offset"]),
                    i => DeleteRule(_settings.CaretSymbolRules, FindIndex(_settings.CaretSymbolRules, r => r.Trigger == "offset", i)),
                    () => EditRule(_settings.CaretSymbolRules, -1, RuleKind.Offset, ["offset"])),
                RuleTableRow(["进程", "捕获模式", "偏移量"],
                    _settings.CaretSymbolRules.Where(r => r.Trigger == "capture")
                        .Select(r => new[] { r.Process, r.Capture, r.CaptureOffset }).ToList(),
                    i => EditRule(_settings.CaretSymbolRules, FindIndex(_settings.CaretSymbolRules, r => r.Trigger == "capture", i), RuleKind.Capture, ["capture"]),
                    i => DeleteRule(_settings.CaretSymbolRules, FindIndex(_settings.CaretSymbolRules, r => r.Trigger == "capture", i)),
                    () => EditRule(_settings.CaretSymbolRules, -1, RuleKind.Capture, ["capture"])));
        }
    }

    private void BuildBorder()
    {
        Section(BorderHost, "窗口边框");
        Card(BorderHost,
            ToggleRow("启用窗口边框", "给前台窗口加彩色边框", () => _settings.BorderActive, v => _settings.BorderActive = v),
            ComboRow("显示名单", "", ShowModes, () => _settings.BorderShowMode, v => _settings.BorderShowMode = v),
            IntRow("自动隐藏延迟（毫秒）", "0 表示常驻", () => _settings.BorderHideDelay, v => _settings.BorderHideDelay = v),
            ToggleRow("进程变化时重新显示", "", () => _settings.BorderReshowOnProcessChange, v => _settings.BorderReshowOnProcessChange = v),
            ToggleRow("标题变化时重新显示", "", () => _settings.BorderReshowOnTitleChange, v => _settings.BorderReshowOnTitleChange = v),
            ToggleRow("类名变化时重新显示", "", () => _settings.BorderReshowOnClassChange, v => _settings.BorderReshowOnClassChange = v),
            ToggleRow("普通窗口显示", "", () => _settings.BorderShowOnNormal, v => _settings.BorderShowOnNormal = v),
            ToggleRow("最大化窗口显示", "", () => _settings.BorderShowOnMaximized, v => _settings.BorderShowOnMaximized = v),
            ToggleRow("全屏窗口显示", "", () => _settings.BorderShowOnFullscreen, v => _settings.BorderShowOnFullscreen = v));

        Section(BorderHost, "最大化 / 全屏窗口的四条边");
        Card(BorderHost,
            ToggleRow("最大化 · 上", "", () => _settings.BorderShowOnMaximizedTop, v => _settings.BorderShowOnMaximizedTop = v),
            ToggleRow("最大化 · 下", "", () => _settings.BorderShowOnMaximizedBottom, v => _settings.BorderShowOnMaximizedBottom = v),
            ToggleRow("最大化 · 左", "", () => _settings.BorderShowOnMaximizedLeft, v => _settings.BorderShowOnMaximizedLeft = v),
            ToggleRow("最大化 · 右", "", () => _settings.BorderShowOnMaximizedRight, v => _settings.BorderShowOnMaximizedRight = v),
            ToggleRow("全屏 · 上", "", () => _settings.BorderShowOnFullscreenTop, v => _settings.BorderShowOnFullscreenTop = v),
            ToggleRow("全屏 · 下", "", () => _settings.BorderShowOnFullscreenBottom, v => _settings.BorderShowOnFullscreenBottom = v),
            ToggleRow("全屏 · 左", "", () => _settings.BorderShowOnFullscreenLeft, v => _settings.BorderShowOnFullscreenLeft = v),
            ToggleRow("全屏 · 右", "", () => _settings.BorderShowOnFullscreenRight, v => _settings.BorderShowOnFullscreenRight = v));

        Section(BorderHost, "置顶窗口");
        Card(BorderHost,
            IntRow("置顶边框宽度", "", () => _settings.BorderWidthPinned, v => _settings.BorderWidthPinned = v),
            ColorRow("置顶边框颜色", "0xRRGGBB", () => _settings.BorderColorPinned, v => _settings.BorderColorPinned = v));

        Section(BorderHost, "各状态颜色");
        foreach (string state in InputTipStates.All)
        {
            string key = state;
            var style = _settings.BorderStyles[key];
            Card(BorderHost,
                IntRow(InputTipStates.Labels[key] + " 宽度", "", () => style.Width, v => style.Width = v),
                ColorRow(InputTipStates.Labels[key] + " 颜色", "留空 = 不显示", () => style.Color, v => style.Color = v));
        }

        Section(BorderHost, "显示/隐藏规则");
        Card(BorderHost,
            RuleTableRow(["进程", "条件", "类名", "标题", "行为"],
                _settings.BorderRules.Select(r => new[] { r.Process, ConditionLabel(r.Condition), r.Class, r.Title, r.Trigger == "show" ? "显示" : "隐藏" }).ToList(),
                i => EditRule(_settings.BorderRules, i, RuleKind.Display, ["show", "hide"]),
                i => DeleteRule(_settings.BorderRules, i),
                () => EditRule(_settings.BorderRules, -1, RuleKind.Display, ["show", "hide"])));
    }

    private void BuildRules()
    {
        Section(RulesHost, "窗口规则");
        Note(RulesHost, "窗口切换时按进程/类名/标题匹配，触发状态切换、键盘切换、暂停、置顶等行为。规则按进程精确 > 正则、条件越长优先级越高。");
        Card(RulesHost,
            RuleTableRow(["进程", "触发行为", "条件", "类名", "标题", "监控内容"],
                _settings.WindowRules.Select(r => new[]
                {
                    r.Process, TriggerLabel(r.Trigger), ConditionLabel(r.Condition), r.Class, r.Title,
                    FirstNonEmpty(r.TextMonitor, r.HotkeyMonitor, r.IdleTimer),
                }).ToList(),
                i => EditRule(_settings.WindowRules, i, RuleKind.Window, null),
                i => DeleteRule(_settings.WindowRules, i),
                () => EditRule(_settings.WindowRules, -1, RuleKind.Window, null)));

        Section(RulesHost, "热键规则");
        Note(RulesHost, "热键写法与 AutoHotkey 一致：^ Ctrl、! Alt、+ Shift、# Win，例如 ~^/、#w、~LShift Up。");
        Card(RulesHost,
            RuleTableRow(["热键", "触发行为", "进程", "条件", "类名", "标题"],
                _settings.HotkeyRules.Select(r => new[]
                {
                    r.Hotkey, TriggerLabel(r.Trigger), r.Process, ConditionLabel(r.Condition), r.Class, r.Title,
                }).ToList(),
                i => EditRule(_settings.HotkeyRules, i, RuleKind.Hotkey, null),
                i => DeleteRule(_settings.HotkeyRules, i),
                () => EditRule(_settings.HotkeyRules, -1, RuleKind.Hotkey, null)));
    }

    private void BuildMore()
    {
        Section(MoreHost, "开机与界面");
        Card(MoreHost,
            AutostartRow(),
            ComboRow("界面语言", "内置中文；上游英文语言包仅影响原版动作菜单", [("zh-CN", "简体中文")],
                () => _settings.Language, v => _settings.Language = v));

        Section(MoreHost, "配置与数据");
        Note(MoreHost, $"配置文件：{ConfigPath}");
        Note(MoreHost, $"资源目录：{Path.Combine(ConfigDirectory, "assets")}（symbol 放 PNG，cursor 放 .cur/.ani）");
        Note(MoreHost, "说明：光标捕获支持 GUI / MSAA / ACC；UIA / WPF / HOOK 通道在拿不到时会自动回退。JetBrains JAB 桥接未随工具箱启用。");

        Section(MoreHost, "关于");
        Card(MoreHost,
            TextRow("引擎版本", "", () => "1.0", _ => { }),
            TextRow("内核来源", "", () => "InputTip 3.6.12（AGPL-3.0）", _ => { }),
            TextRow("实现方式", "", () => "原生 C# 重写，运行在 ToolDock.Agent 进程内", _ => { }));
    }

    // ------------------------------------------------------------------
    // 控件构建辅助
    // ------------------------------------------------------------------

    private static void Section(StackPanel host, string title)
    {
        host.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.FindResource("SectionTitleStyle"),
            Margin = new Thickness(2, 14, 0, 8),
        });
    }

    private static void Note(StackPanel host, string text)
    {
        host.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("RowDescStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 8),
        });
    }

    private static void Card(StackPanel host, params FrameworkElement[] rows)
    {
        var card = new Border { Style = (Style)Application.Current.FindResource("SettingsCardStyle") };
        var stack = new StackPanel();
        card.Child = stack;
        bool first = true;
        foreach (var row in rows)
        {
            if (!first)
            {
                stack.Children.Add(new Border { Style = (Style)Application.Current.FindResource("RowDividerStyle") });
            }
            first = false;
            stack.Children.Add(row);
        }
        host.Children.Add(card);
    }

    private FrameworkElement ToggleRow(string title, string desc, Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("SwitchStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = get(),
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, title);
        _collectors.Add(() => set(toggle.IsChecked == true));
        return Row(title, desc, toggle);
    }

    private FrameworkElement IntRow(string title, string desc, Func<int> get, Action<int> set)
    {
        var box = new TextBox
        {
            Width = 120,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("WinUITextBoxStyle"),
            Text = get().ToString(),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        _collectors.Add(() =>
        {
            if (int.TryParse(box.Text.Trim(), out int value))
            {
                set(value);
            }
        });
        return Row(title, desc, box);
    }

    private FrameworkElement TextRow(string title, string desc, Func<string> get, Action<string> set)
    {
        var box = new TextBox
        {
            Width = 320,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("WinUITextBoxStyle"),
            Text = get() ?? "",
        };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        _collectors.Add(() => set(box.Text));
        return Row(title, desc, box);
    }

    private FrameworkElement ComboRow(string title, string desc, (string Value, string Label)[] items,
        Func<string> get, Action<string> set)
    {
        var combo = new ComboBox
        {
            Width = 260,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxDropDownHeight = 320,
        };
        foreach (var (value, label) in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        string current = get() ?? "";
        var selected = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            combo.SelectedItem = selected;
        }
        else if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
        System.Windows.Automation.AutomationProperties.SetName(combo, title);
        _collectors.Add(() =>
        {
            if (combo.SelectedItem is ComboBoxItem item)
            {
                set(item.Tag as string ?? "");
            }
        });
        return Row(title, desc, combo);
    }

    /// <summary>符号总开关：切换后重建页面，让“光标符号/鼠标符号”页的形态选择同步。</summary>
    private FrameworkElement SymbolToggleRow(bool caret)
    {
        int current = caret ? _settings.CaretSymbolType : _settings.CursorSymbolType;
        var toggle = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("SwitchStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = current != 0,
        };
        string title = caret ? "光标符号提示" : "鼠标符号提示";
        string desc = caret ? "在文本光标附近显示状态符号" : "在鼠标附近显示状态符号";
        System.Windows.Automation.AutomationProperties.SetName(toggle, title);
        System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, caret ? "InputTip_CaretSymbol" : "InputTip_CursorSymbol");
        toggle.Click += (_, _) =>
        {
            Collect();
            int type = toggle.IsChecked == true ? 3 : 0;
            if (caret)
            {
                _settings.CaretSymbolType = type;
            }
            else
            {
                _settings.CursorSymbolType = type;
            }
            BuildPage();
        };
        return Row(title, desc, toggle);
    }

    /// <summary>开机自启开关：只在用户点击时下发，不参与“应用”时的批量收集。</summary>
    private FrameworkElement AutostartRow()
    {
        var toggle = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("SwitchStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = AgentLink.Instance.CurrentAutoStart,
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "随工具箱开机自启");
        toggle.Click += (_, _) => AgentLink.Instance.SendAutoStart(toggle.IsChecked == true);
        return Row("随工具箱开机自启", "输入法提示跟随工具箱 Agent 启动，无需单独注册启动项", toggle);
    }

    private FrameworkElement ColorRow(string title, string desc, Func<string> get, Action<string> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var swatch = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 8, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.FindResource("Card.Stroke"),
            Background = Brushes.Transparent,
        };
        var box = new TextBox
        {
            Width = 120,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("WinUITextBoxStyle"),
            Text = get() ?? "",
        };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        void RefreshSwatch()
        {
            int rgb = InputTipSettings.ParseColor(box.Text, -1);
            if (rgb < 0)
            {
                swatch.Background = Brushes.Transparent;
                return;
            }
            swatch.Background = new SolidColorBrush(Color.FromRgb(
                (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
        }
        box.TextChanged += (_, _) => RefreshSwatch();
        RefreshSwatch();
        panel.Children.Add(swatch);
        panel.Children.Add(box);
        _collectors.Add(() => set(InputTipSettings.NormalizeColor(box.Text, "")));
        return Row(title, desc, panel);
    }

    private static FrameworkElement Row(string title, string desc, FrameworkElement editor)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.FindResource("RowTitleStyle"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(desc))
        {
            left.Children.Add(new TextBlock
            {
                Text = desc,
                Style = (Style)Application.Current.FindResource("RowDescStyle"),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        grid.Children.Add(left);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    /// <summary>规则表格：表头 + 行（双击编辑）+ 添加按钮。</summary>
    private FrameworkElement RuleTableRow(string[] headers, IReadOnlyList<string[]> rows,
        Action<int> onEdit, Action<int> onDelete, Action onAdd)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        foreach (string _ in headers)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        for (int i = 0; i < headers.Length; i++)
        {
            var label = new TextBlock
            {
                Text = headers[i],
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.FindResource("Text.Secondary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, i);
            header.Children.Add(label);
        }
        panel.Children.Add(header);

        if (rows.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暂无规则",
                FontSize = 12,
                Foreground = (Brush)Application.Current.FindResource("Text.Secondary"),
                Margin = new Thickness(0, 4, 0, 8),
            });
        }
        for (int index = 0; index < rows.Count; index++)
        {
            int captured = index;
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
            foreach (string _ in headers)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = new TextBlock
                {
                    Text = i < rows[index].Length ? rows[index][i] : "",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 4, 6, 4),
                };
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var edit = new Button
            {
                Content = "编辑",
                Style = (Style)Application.Current.FindResource("AppButtonStyle"),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
            };
            edit.Click += (_, _) => onEdit(captured);
            var delete = new Button
            {
                Content = "删除",
                Style = (Style)Application.Current.FindResource("AppButtonStyle"),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
            };
            delete.Click += (_, _) => onDelete(captured);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            Grid.SetColumn(actions, headers.Length);
            grid.Children.Add(actions);
            panel.Children.Add(grid);
            panel.Children.Add(new Border { Style = (Style)Application.Current.FindResource("RowDividerStyle") });
        }

        var add = new Button
        {
            Content = "添加规则",
            Style = (Style)Application.Current.FindResource("AppButtonStyle"),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        add.Click += (_, _) => onAdd();
        panel.Children.Add(add);
        return panel;
    }

    // ------------------------------------------------------------------
    // 规则编辑
    // ------------------------------------------------------------------

    private void EditRule(List<InputTipRule> list, int index, RuleKind kind, string[]? allowedTriggers)
    {
        Collect();
        InputTipRule draft;
        bool isNew = index < 0;
        if (isNew)
        {
            draft = new InputTipRule
            {
                Id = DateTime.Now.ToString("yyyy-MM-dd") + "." + Random.Shared.Next(0, 0xFFFFF).ToString("x5"),
            };
        }
        else
        {
            if (index >= list.Count)
            {
                return;
            }
            draft = list[index].Clone();
        }

        var dialog = new InputTipRuleDialog(draft, kind, allowedTriggers, _settings)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        if (dialog.DeleteRequested)
        {
            if (!isNew)
            {
                list.RemoveAt(index);
            }
        }
        else if (isNew)
        {
            list.Add(draft);
        }
        else
        {
            list[index] = draft;
        }
        BuildPage();
        SaveStatus.Text = "规则已修改，点「应用并重启引擎」生效";
    }

    private void DeleteRule(List<InputTipRule> list, int index)
    {
        if (index < 0 || index >= list.Count)
        {
            return;
        }
        Collect();
        list.RemoveAt(index);
        BuildPage();
        SaveStatus.Text = "规则已删除，点「应用并重启引擎」生效";
    }

    private static int FindIndex(List<InputTipRule> list, Func<InputTipRule, bool> predicate, int target)
    {
        int seen = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (!predicate(list[i]))
            {
                continue;
            }
            if (seen == target)
            {
                return i;
            }
            seen++;
        }
        return -1;
    }

    private void EditImeRule(int index)
    {
        Collect();
        var rule = index >= 0 && index < _settings.ImeRules.Count ? _settings.ImeRules[index] : new ImeRule();
        var dialog = new InputTipRuleDialog(rule, _settings)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        if (dialog.DeleteRequested)
        {
            if (index >= 0)
            {
                _settings.ImeRules.RemoveAt(index);
            }
        }
        else if (index >= 0)
        {
            _settings.ImeRules[index] = rule;
        }
        else
        {
            _settings.ImeRules.Add(rule);
        }
        BuildPage();
    }

    private void DeleteImeRule(int index)
    {
        if (index < 0 || index >= _settings.ImeRules.Count)
        {
            return;
        }
        Collect();
        _settings.ImeRules.RemoveAt(index);
        BuildPage();
    }

    // ------------------------------------------------------------------
    // 列表数据
    // ------------------------------------------------------------------

    private List<string> ListCursorDirectories()
    {
        var result = new List<string>();
        void Scan(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    if (Directory.EnumerateFiles(directory, "*.*").Any(f => Path.GetExtension(f) is ".cur" or ".ani"))
                    {
                        result.Add(Path.GetFileName(directory));
                    }
                }
            }
            catch
            {
            }
        }
        Scan(Path.Combine(ConfigDirectory, "assets", "default-cursor"));
        Scan(Path.Combine(ConfigDirectory, "assets", "cursor"));
        foreach (string state in InputTipStates.All)
        {
            result.Add("default-" + InputTipStates.ColorNames[state]);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> ListSymbolPictures()
    {
        var result = new List<string>();
        try
        {
            string directory = Path.Combine(ConfigDirectory, "assets", "symbol");
            if (Directory.Exists(directory))
            {
                result.AddRange(Directory.EnumerateFiles(directory, "*.png")
                    .Select(Path.GetFileName)
                    .Where(name => name is not null)
                    .Select(name => name!));
            }
        }
        catch
        {
        }
        foreach (string state in InputTipStates.All)
        {
            result.Add("default-triangle-" + InputTipStates.ColorNames[state] + ".png");
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ListFonts()
    {
        try
        {
            return System.Windows.Media.Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Where(f => !f.StartsWith("@"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return ["Microsoft YaHei"];
        }
    }

    private static string TriggerLabel(string value)
    {
        var option = TriggerOptions.FirstOrDefault(o => o.Value == value);
        return string.IsNullOrEmpty(option.Label) ? value : option.Label;
    }

    private static string ConditionLabel(string value)
    {
        var option = WindowConditionOptions.FirstOrDefault(o => o.Value == value);
        return string.IsNullOrEmpty(option.Label) ? value : option.Label;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    // ------------------------------------------------------------------
    // 状态与导航
    // ------------------------------------------------------------------

    private void ToolEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        bool enabled = ToolEnabledToggle.IsChecked == true;
        UiSettings.Load().SetToolEnabled(ToolKey, enabled);
        _settings.Enabled = enabled;
        try
        {
            _settings.Save();
        }
        catch
        {
        }
        AgentLink.Instance.SendInputTipEnabled(enabled);
        ApplyToolState(enabled);
        SaveStatus.Text = enabled ? "已启用" : "已停用";
        UpdateStatus();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        // Agent 不会为暂停状态主动推送，这里本地先翻转，按钮文案立即跟上
        bool paused = !AgentLink.Instance.CurrentInputTipPaused;
        AgentLink.Instance.CurrentInputTipPaused = paused;
        AgentLink.Instance.SendInputTipPaused(paused);
        SaveStatus.Text = paused ? "已暂停" : "已恢复";
        UpdateStatus();
    }

    private void ApplyToolState(bool enabled)
    {
        EditorArea.IsEnabled = enabled;
        EditorArea.Opacity = enabled ? 1.0 : 0.45;
        if (SaveButton is not null)
        {
            SaveButton.IsEnabled = enabled;
        }
        if (PauseButton is not null)
        {
            PauseButton.IsEnabled = enabled;
        }
    }

    private void OnAgentConfig()
    {
        var link = AgentLink.Instance;
        _syncing = true;
        ToolEnabledToggle.IsChecked = link.CurrentInputTipEnabled;
        _syncing = false;
        ApplyToolState(link.CurrentInputTipEnabled);
        if (!string.IsNullOrEmpty(link.CurrentInputTipConfigPath))
        {
            ConfigPathText.Text = link.CurrentInputTipConfigPath;
        }
        if (!string.IsNullOrEmpty(link.CurrentInputTipEngine))
        {
            VersionText.Text = link.CurrentInputTipEngine;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var link = AgentLink.Instance;
        bool enabled = ToolEnabledToggle.IsChecked == true;
        PauseButton.Content = link.CurrentInputTipPaused ? "恢复" : "暂停";
        if (!enabled)
        {
            StatusText.Text = "已停用";
            StatusText.Foreground = (Brush)Application.Current.Resources["Text.Secondary"];
        }
        else if (!link.Connected)
        {
            StatusText.Text = "Agent 未连接（仅编辑配置）";
            StatusText.Foreground = (Brush)Application.Current.Resources["Text.Secondary"];
        }
        else if (link.CurrentInputTipRunning)
        {
            string state = string.IsNullOrEmpty(link.CurrentInputTipState) ? "" : " · " + StateLabel(link.CurrentInputTipState);
            StatusText.Text = (link.CurrentInputTipPaused ? "已暂停" : "随工具箱运行") + state;
            StatusText.Foreground = (Brush)Application.Current.Resources["Accent"];
        }
        else
        {
            StatusText.Text = "引擎启动中…";
            StatusText.Foreground = (Brush)Application.Current.Resources["Text.Secondary"];
        }
    }

    private static string StateLabel(string state)
        => InputTipStates.ShortLabels.TryGetValue(state, out var label) ? label : state;

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralPane is null)
        {
            return;
        }
        string tag = (sender as FrameworkElement)?.Tag as string ?? "General";
        GeneralPane.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        ImePane.Visibility = tag == "Ime" ? Visibility.Visible : Visibility.Collapsed;
        CursorPane.Visibility = tag == "Cursor" ? Visibility.Visible : Visibility.Collapsed;
        OverlayPane.Visibility = tag == "Overlay" ? Visibility.Visible : Visibility.Collapsed;
        CaretPane.Visibility = tag == "Caret" ? Visibility.Visible : Visibility.Collapsed;
        CursorSymbolPane.Visibility = tag == "CursorSymbol" ? Visibility.Visible : Visibility.Collapsed;
        BorderPane.Visibility = tag == "Border" ? Visibility.Visible : Visibility.Collapsed;
        RulesPane.Visibility = tag == "Rules" ? Visibility.Visible : Visibility.Collapsed;
        MorePane.Visibility = tag == "More" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("builtin");
}

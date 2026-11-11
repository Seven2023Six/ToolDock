using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>
/// 窗口拖拽：内置 AltSnap 1.68 引擎的完整配置页。
/// 页面按 AltSnap.ini 的段落生成控件；应用时写回 ini，然后让 Agent 重启进程内引擎。
/// </summary>
public partial class AltSnapPage : UserControl
{
    private const string ToolKey = "altsnap";

    private static readonly string IniDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "altsnap");
    private static readonly string IniPath = Path.Combine(IniDirectory, "AltSnap.ini");
    private static readonly string DefaultIniPath = Path.Combine(IniDirectory, "AltSnap.dni");

    /// <summary>ini 里仍是英文动作名；下拉框显示中文，保存时再映射回英文。</summary>
    private static readonly (string Value, string Label)[] ActionOptions =
    [
        ("Nothing", "无动作"),
        ("Move", "移动窗口"),
        ("Resize", "缩放窗口（拖拽）"),
        ("Restore", "还原窗口"),
        ("Menu", "窗口菜单"),
        ("Minimize", "最小化"),
        ("Maximize", "最大化"),
        ("Center", "居中"),
        ("Center2", "居中（备用）"),
        ("AlwaysOnTop", "置顶 / 取消置顶"),
        ("Close", "关闭窗口"),
        ("Lower", "置于底层"),
        ("Focus", "聚焦窗口"),
        ("Borderless", "无边框"),
        ("Kill", "结束进程"),
        ("Pause", "暂停接管"),
        ("Resume", "恢复接管"),
        ("RevealExe", "定位程序文件"),
        ("MaximizeHV", "最大化（横 / 纵）"),
        ("MinAllOther", "最小化其它窗口"),
        ("Mute", "静音"),
        ("SideSnap", "侧边吸附"),
        ("ExtendSnap", "扩展吸附"),
        ("ExtendTNEdge", "扩展任务栏边缘"),
        ("MoveTNEdge", "移动到任务栏边缘"),
        ("NStacked", "下一叠放窗口"),
        ("NStacked2", "下一叠放窗口（备用）"),
        ("PStacked", "上一叠放窗口"),
        ("StackList", "叠放窗口列表"),
        ("StackList2", "叠放窗口列表（备用）"),
        ("AltTabList", "窗口列表"),
        ("AltTabFullList", "全部窗口列表"),
        ("ASOnOff", "吸附开关"),
        ("MoveOnOff", "移动开关"),
        ("MZone", "移动到区域"),
        ("XZone", "交换区域"),
        ("Step", "步进"),
        ("NLayout", "下一布局"),
        ("PLayout", "上一布局"),
        ("Shrt", "循环切换布局"),
        ("NPLayout", "下一 / 上一布局"),
        ("Roll", "卷起窗口"),
        ("AltTab", "Alt+Tab"),
        ("Volume", "调节音量"),
        ("Transparency", "调节透明度"),
        ("HScroll", "横向滚动"),
        ("Zoom", "缩放窗口（滚轮）"),
        ("Zoom2", "缩放窗口（滚轮备用）"),
        ("NPStacked", "下一 / 上一叠放"),
        ("NPStacked2", "下一 / 上一叠放（备用）"),
    ];

    private enum FieldKind { Bool, Mask, Int, Text, LongText, Action, Choice }

    private sealed record FieldDef(
        string Section,
        string Key,
        string Title,
        string Desc,
        FieldKind Kind,
        string Default,
        string[]? Choices = null);

    private readonly Dictionary<string, ToggleButton> _toggles = new();
    private readonly Dictionary<string, TextBox> _texts = new();
    private readonly Dictionary<string, ComboBox> _combos = new();
    private readonly List<FieldDef> _schema = new();

    private TextBox? _rawIniBox;
    private bool _built;
    private bool _syncing;

    public AltSnapPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AgentLink.AltSnapUpdated -= OnAgentConfig;
            AgentLink.AltSnapUpdated += OnAgentConfig;
            BuildSchema();
            EnsureIni();
            LoadAll();
            ApplyToolState(ToolEnabledToggle.IsChecked == true);
            AgentLink.Instance.RequestAltSnap();
            UpdateStatus();
        };
        Unloaded += (_, _) => AgentLink.AltSnapUpdated -= OnAgentConfig;
    }

    // ------------------------------------------------------------------
    // 配置架构（对齐 AltSnap 1.68 默认 ini 的 General/Input/Blacklist/
    // Advanced/Performance/Zones 段落；未列出的键可从原始配置文件编辑）
    // ------------------------------------------------------------------

    private void BuildSchema()
    {
        if (_built)
        {
            return;
        }
        _built = true;

        FieldDef B(string sec, string key, string title, string desc, string def = "0")
            => new(sec, key, title, desc, FieldKind.Bool, def);
        FieldDef Mask(string sec, string key, string title, string desc, string def = "0")
            => new(sec, key, title, desc, FieldKind.Mask, def);
        FieldDef I(string sec, string key, string title, string desc, string def)
            => new(sec, key, title, desc, FieldKind.Int, def);
        FieldDef T(string sec, string key, string title, string desc, string def = "")
            => new(sec, key, title, desc, FieldKind.Text, def);
        FieldDef L(string sec, string key, string title, string desc, string def = "")
            => new(sec, key, title, desc, FieldKind.LongText, def);
        FieldDef A(string sec, string key, string title, string desc, string def = "Nothing")
            => new(sec, key, title, desc, FieldKind.Action, def);
        FieldDef C(string sec, string key, string title, string desc, string def, params string[] choices)
            => new(sec, key, title, desc, FieldKind.Choice, def, choices);

        _schema.Clear();

        // 常规
        _schema.Add(B("General", "AutoFocus", "拖动时自动聚焦", "拖动哪个窗口就把它提到最前", "0"));
        _schema.Add(B("General", "Aero", "Aero 贴边吸附", "拖到屏幕边缘/角落时半屏、1/4 屏或最大化", "1"));
        _schema.Add(B("General", "SmartAero", "智能 Aero 尺寸", "缩放贴边窗口时联动调整相邻窗口尺寸", "1"));
        _schema.Add(Mask("General", "StickyResize", "Shift 联动缩放", "按住 Shift 调整贴边窗口时同步缩放相邻窗口（第 0 位）", "0"));
        _schema.Add(Mask("General", "MMMaximize", "中键标题栏最大化", "不按 Alt，中键点击标题栏也可最大化/还原（第 0 位）", "1"));
        _schema.Add(I("General", "AeroHoffset", "Aero 水平偏移", "贴边位置的水平偏移比例（0-100）", "50"));
        _schema.Add(I("General", "AeroVoffset", "Aero 垂直偏移", "贴边位置的垂直偏移比例（0-100）", "50"));
        _schema.Add(B("General", "InactiveScroll", "滚轮作用于非活动窗口", "鼠标悬停即可滚动后台窗口，不抢焦点", "0"));
        _schema.Add(C("General", "AutoSnap", "边缘吸附对象", "移动窗口时自动贴合屏幕或其它窗口边缘",
            "2", "0|关闭", "1|屏幕边缘", "2|+ 窗口外侧", "3|+ 窗口内侧"));
        _schema.Add(B("General", "MDI", "MDI 子窗口支持", "支持拖动 MDI 子窗口（如旧版管理工具）", "1"));
        _schema.Add(C("General", "ResizeCenter", "缩放中心模式", "Alt+右键缩放时以哪一侧为基准",
            "1", "0|全方向", "1|右下角", "2|移动", "3|最近边"));
        _schema.Add(I("General", "CenterFraction", "中心缩放区比例", "中心缩放模式下的判定区域（0-100）", "24"));
        _schema.Add(I("General", "SidesFraction", "边缘缩放区比例", "边缘缩放模式的判定区域（0-100）", "24"));
        _schema.Add(I("General", "MoveTrans", "拖动透明度", "0-254 为半透明度，255 = 不透明（关闭）", "255"));

        // Input：Alt
        _schema.Add(A("Input", "LMB", "Alt + 左键", "默认按住拖动移动窗口", "Move"));
        _schema.Add(A("Input", "MMB", "Alt + 中键", "默认弹出窗口系统菜单", "Menu"));
        _schema.Add(A("Input", "RMB", "Alt + 右键", "默认按光标半区缩放窗口", "Resize"));
        _schema.Add(A("Input", "MB4", "Alt + 侧键 1", "鼠标第 4 键", "Nothing"));
        _schema.Add(A("Input", "MB5", "Alt + 侧键 2", "鼠标第 5 键", "Nothing"));
        _schema.Add(A("Input", "Scroll", "Alt + 滚轮", "滚轮动作（调音量 / 卷起窗口 / 调透明度等）", "Nothing"));
        _schema.Add(A("Input", "ScrollUp", "Alt + 滚轮向上", "只响应向上滚动；留空则沿用「滚轮」动作", "Nothing"));
        _schema.Add(A("Input", "HScroll", "Alt + 横向滚轮", "横向滚轮动作", "Nothing"));
        _schema.Add(A("Input", "HScrollUp", "Alt + 横向滚轮向上", "只响应横向向上滚动", "Nothing"));

        // Input：Ctrl
        _schema.Add(A("Input", "LMBB", "Alt + Ctrl + 左键", "", "Nothing"));
        _schema.Add(A("Input", "MMBB", "Alt + Ctrl + 中键", "上游默认「窗口列表」", "AltTabList"));
        _schema.Add(A("Input", "RMBB", "Alt + Ctrl + 右键", "", "Nothing"));
        _schema.Add(A("Input", "MB4B", "Alt + Ctrl + 侧键 1", "", "Nothing"));
        _schema.Add(A("Input", "MB5B", "Alt + Ctrl + 侧键 2", "", "Nothing"));
        _schema.Add(A("Input", "ScrollB", "Alt + Ctrl + 滚轮", "上游默认调节音量", "Volume"));
        _schema.Add(A("Input", "HScrollB", "Alt + Ctrl + 横向滚轮", "", "Nothing"));

        // Input：Shift
        _schema.Add(A("Input", "LMBT", "Alt + Shift + 左键", "", "Nothing"));
        _schema.Add(A("Input", "MMBT", "Alt + Shift + 中键", "上游默认置底", "Lower"));
        _schema.Add(A("Input", "RMBT", "Alt + Shift + 右键", "", "Nothing"));
        _schema.Add(A("Input", "MB4T", "Alt + Shift + 侧键 1", "", "Nothing"));
        _schema.Add(A("Input", "MB5T", "Alt + Shift + 侧键 2", "", "Nothing"));
        _schema.Add(A("Input", "ScrollT", "Alt + Shift + 滚轮", "上游默认卷起窗口", "Roll"));
        _schema.Add(A("Input", "HScrollT", "Alt + Shift + 横向滚轮", "", "Nothing"));

        // Input：Ctrl+Shift
        _schema.Add(A("Input", "LMBTB", "Alt + Ctrl + Shift + 左键", "", "Nothing"));
        _schema.Add(A("Input", "MMBTB", "Alt + Ctrl + Shift + 中键", "", "Nothing"));
        _schema.Add(A("Input", "RMBTB", "Alt + Ctrl + Shift + 右键", "", "Nothing"));
        _schema.Add(A("Input", "MB4TB", "Alt + Ctrl + Shift + 侧键 1", "", "Nothing"));
        _schema.Add(A("Input", "MB5TB", "Alt + Ctrl + Shift + 侧键 2", "", "Nothing"));
        _schema.Add(A("Input", "ScrollTB", "Alt + Ctrl + Shift + 滚轮", "", "Nothing"));
        _schema.Add(A("Input", "HScrollTB", "Alt + Ctrl + Shift + 横向滚轮", "", "Nothing"));

        // Input：Win
        _schema.Add(A("Input", "LMBM", "Alt + Win + 左键", "", "Nothing"));
        _schema.Add(A("Input", "MMBM", "Alt + Win + 中键", "", "Nothing"));
        _schema.Add(A("Input", "RMBM", "Alt + Win + 右键", "", "Nothing"));
        _schema.Add(A("Input", "MB4M", "Alt + Win + 侧键 1", "", "Nothing"));
        _schema.Add(A("Input", "MB5M", "Alt + Win + 侧键 2", "", "Nothing"));
        _schema.Add(A("Input", "ScrollM", "Alt + Win + 滚轮", "上游默认「下一叠放窗口（备用）」", "NPStacked2"));
        _schema.Add(A("Input", "HScrollM", "Alt + Win + 横向滚轮", "", "Nothing"));

        // Input：Win+Shift
        _schema.Add(A("Input", "LMBR", "Alt + Win + Shift + 左键", "", "Nothing"));
        _schema.Add(A("Input", "MMBR", "Alt + Win + Shift + 中键", "", "Nothing"));
        _schema.Add(A("Input", "RMBR", "Alt + Win + Shift + 右键", "", "Nothing"));
        _schema.Add(A("Input", "MB4R", "Alt + Win + Shift + 侧键 1", "", "Nothing"));
        _schema.Add(A("Input", "MB5R", "Alt + Win + Shift + 侧键 2", "", "Nothing"));
        _schema.Add(A("Input", "ScrollR", "Alt + Win + Shift + 滚轮", "", "Nothing"));
        _schema.Add(A("Input", "HScrollR", "Alt + Win + Shift + 横向滚轮", "", "Nothing"));

        // 抓取/热键动作
        _schema.Add(T("Input", "GrabWithAlt", "抓取键（Alt）", "AltSnap 的“按住抓取”键序列，十六进制键码；留空禁用", ""));
        _schema.Add(T("Input", "GrabWithAltB", "抓取键（Alt+Ctrl）", "十六进制键码序列", ""));
        _schema.Add(T("Input", "MoveUp", "移动上键", "键盘直接移动窗口的键（上游高级项）", ""));
        _schema.Add(T("Input", "MoveUpT", "移动上键（Shift）", "", ""));
        _schema.Add(T("Input", "MoveUpB", "移动上键（Ctrl）", "", ""));
        _schema.Add(T("Input", "ResizeUp", "缩放上键", "", ""));
        _schema.Add(T("Input", "ResizeUpT", "缩放上键（Shift）", "", ""));
        _schema.Add(T("Input", "ResizeUpB", "缩放上键（Ctrl）", "", ""));

        // 黑名单
        _schema.Add(L("Blacklist", "Processes", "排除进程", "英文逗号分隔；这些程序不会被接管", ""));
        _schema.Add(L("Blacklist", "Windows", "排除窗口", "格式：类名|标题，逗号分隔；支持 * 通配", ""));
        _schema.Add(L("Blacklist", "Pause", "暂停接管进程", "英文逗号分隔；拖动时不接管这些程序的窗口", ""));
        _schema.Add(L("Blacklist", "Snaplist", "特殊吸附窗口", "需要特殊处理的窗口类名列表", ""));

        // 高级
        _schema.Add(I("Advanced", "DragThreshold", "拖动阈值", "移动多少像素后才开始拖动", "1"));
        _schema.Add(I("Advanced", "DragOutThresholdPx", "拖出阈值", "拖出窗口边界的判定像素", "20"));
        _schema.Add(I("Advanced", "SnapThreshold", "窗口吸附阈值", "与其它窗口边缘的吸附距离", "20"));
        _schema.Add(I("Advanced", "AeroThreshold", "Aero 触发阈值", "距离屏幕边缘多少像素触发分屏", "5"));
        _schema.Add(I("Advanced", "SnapGap", "吸附间隙", "贴边时留出的像素（-128 到 127）", "0"));
        _schema.Add(I("Advanced", "MinAlpha", "最小透明度", "滚轮调透明度时的下限", "32"));
        _schema.Add(I("Advanced", "AlphaDelta", "透明度步进", "每次滚轮调整的透明度", "64"));
        _schema.Add(I("Advanced", "ZoomFrac", "缩放步进", "滚轮缩放窗口的步进", "16"));
        _schema.Add(B("Advanced", "AutoRemaximize", "自动重新最大化", "最大化窗口被移动后自动还原最大化", "0"));
        _schema.Add(B("Advanced", "FullScreen", "接管全屏窗口", "允许对全屏程序使用 AltSnap", "0"));
        _schema.Add(B("Advanced", "BLMaximized", "排除最大化窗口", "不接管已最大化的窗口", "0"));
        _schema.Add(Mask("Advanced", "AeroTopMaximizes", "顶部触发最大化", "拖到屏幕顶部最大化（第 0 位）；第 1 位为双击 Shift 触发", "1"));
        _schema.Add(B("Advanced", "UseCursor", "使用自定义光标", "拖动时显示 AltSnap 的抓取光标", "1"));
        _schema.Add(B("Advanced", "ShiftSnaps", "Shift 强制吸附", "按住 Shift 时强制触发贴边吸附", "1"));
        _schema.Add(B("Advanced", "TopmostIndicator", "置顶提示", "切换置顶时显示提示", "1"));
        _schema.Add(B("Advanced", "ResizeAll", "缩放所有选中窗口", "多选窗口时一起缩放", "1"));
        _schema.Add(B("Advanced", "PiercingClick", "穿透 Alt+单击", "不拦截 Alt+单击（会禁用 AltSnap 双击）", "0"));
        _schema.Add(B("Advanced", "MultipleInstances", "允许多实例", "仅影响独立 AltSnap；内置引擎始终单实例", "0"));
        _schema.Add(B("Advanced", "AlwaysElevate", "总是以管理员运行", "内置引擎不自动提权；需要时请以管理员启动工具箱", "0"));

        // 性能
        _schema.Add(I("Performance", "FullWin", "拖动完整窗口", "0=禁用 1=启用 2=自动", "2"));
        _schema.Add(I("Performance", "TransWinOpacity", "透明窗口不透明度", "0-255，0 表示不处理", "0"));
        _schema.Add(I("Performance", "PinRate", "置顶刷新率", "置顶窗口的刷新间隔", "32"));
        _schema.Add(I("Performance", "RezTimer", "缩放定时器", "缩放刷新间隔", "2"));
        _schema.Add(I("Performance", "RefreshRate", "刷新率", "0 表示跟随系统", "0"));
        _schema.Add(I("Performance", "MoveRate", "移动刷新率", "移动时的刷新间隔", "2"));
        _schema.Add(I("Performance", "ResizeRate", "缩放刷新率", "缩放时的刷新间隔", "4"));

        // 区域
        _schema.Add(B("Zones", "UseZones", "启用布局区域", "按住 Shift 拖动时按布局吸附（布局在原始配置的 [Zone*] 段）", "0"));
        _schema.Add(I("Zones", "ZSnapMode", "区域吸附模式", "0 / 1 / 2，对应上游的区域吸附模式", "1"));
        _schema.Add(I("Zones", "InterZone", "区域间隔", "相邻区域之间保留的像素", "32"));
        _schema.Add(B("Zones", "ShowZonesPrevw", "显示区域预览", "拖动时显示布局预览", "1"));
        _schema.Add(I("Zones", "ZonesPrevwOpacity", "预览不透明度", "0-255", "128"));
        _schema.Add(I("Zones", "GridNx", "网格列数", "默认布局的列数（1-6）", "3"));
        _schema.Add(I("Zones", "GridNy", "网格行数", "默认布局的行数（1-6）", "2"));
        _schema.Add(I("Zones", "MaxLayouts", "布局数量", "可切换的布局数量（1-4）", "4"));
        _schema.Add(I("Zones", "LayoutNumber", "当前布局", "0 开始的布局序号", "0"));

        BuildControls();
    }

    private void BuildControls()
    {
        GeneralHost.Children.Clear();
        MouseHost.Children.Clear();
        KeyboardHost.Children.Clear();
        BlacklistHost.Children.Clear();
        AdvancedHost.Children.Clear();
        _toggles.Clear();
        _texts.Clear();
        _combos.Clear();

        // 原版对话框的五个分类页
        BuildCards(GeneralHost, _schema.Where(f => f.Section == "General"), _ => "常规");

        var mouse = _schema
            .Where(f => f.Section == "Input" && !IsKeyboardField(f))
            .ToList();
        foreach (string group in new[] { "Alt", "Alt + Ctrl", "Alt + Shift", "Alt + Ctrl + Shift", "Alt + Win", "Alt + Win + Shift" })
        {
            BuildCards(MouseHost, mouse.Where(f => MouseGroup(f.Key) == group), _ => group);
        }

        BuildCards(KeyboardHost, _schema.Where(IsKeyboardField), _ => "键盘与热键");
        BuildCards(BlacklistHost, _schema.Where(f => f.Section == "Blacklist"), _ => "黑名单与排除");
        BuildCards(AdvancedHost, _schema.Where(f => f.Section == "Advanced"), _ => "高级");
        BuildCards(AdvancedHost, _schema.Where(f => f.Section == "Performance"), _ => "性能");
        BuildCards(AdvancedHost, _schema.Where(f => f.Section == "Zones"), _ => "布局区域");
        BuildRawEditor(AdvancedHost);
    }

    private static bool IsKeyboardField(FieldDef field)
        => field.Section == "Input" &&
           (field.Key.StartsWith("Grab", StringComparison.Ordinal) ||
            field.Key.StartsWith("MoveUp", StringComparison.Ordinal) ||
            field.Key.StartsWith("ResizeUp", StringComparison.Ordinal));

    private static string MouseGroup(string key)
    {
        if (key is "LMB" or "MMB" or "RMB" or "MB4" or "MB5"
            or "Scroll" or "ScrollUp" or "HScroll" or "HScrollUp")
        {
            return "Alt";
        }
        if (key.EndsWith("TB", StringComparison.Ordinal))
        {
            return "Alt + Ctrl + Shift";
        }
        if (key.EndsWith("B", StringComparison.Ordinal))
        {
            return "Alt + Ctrl";
        }
        if (key.EndsWith("T", StringComparison.Ordinal))
        {
            return "Alt + Shift";
        }
        if (key.EndsWith("R", StringComparison.Ordinal))
        {
            return "Alt + Win + Shift";
        }
        if (key.EndsWith("M", StringComparison.Ordinal))
        {
            return "Alt + Win";
        }
        return "Alt";
    }

    private void BuildCards(StackPanel host, IEnumerable<FieldDef> fields, Func<FieldDef, string> groupOf)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<FieldDef>>();
        foreach (var field in fields)
        {
            string group = groupOf(field);
            if (!groups.TryGetValue(group, out var list))
            {
                list = new List<FieldDef>();
                groups[group] = list;
                order.Add(group);
            }
            list.Add(field);
        }
        foreach (string group in order)
        {
            host.Children.Add(new TextBlock
            {
                Text = group,
                Style = (Style)FindResource("SectionTitleStyle"),
                Margin = new Thickness(2, 14, 0, 8),
            });
            var card = new Border { Style = (Style)FindResource("SettingsCardStyle") };
            var stack = new StackPanel();
            card.Child = stack;
            bool first = true;
            foreach (var field in groups[group])
            {
                if (!first)
                {
                    stack.Children.Add(new Border { Style = (Style)FindResource("RowDividerStyle") });
                }
                first = false;
                stack.Children.Add(BuildRow(field));
            }
            host.Children.Add(card);
        }
    }

    private void BuildRawEditor(StackPanel host)
    {
        host.Children.Add(new TextBlock
        {
            Text = "原始配置文件",
            Style = (Style)FindResource("SectionTitleStyle"),
            Margin = new Thickness(2, 18, 0, 8),
        });
        var card = new Border { Style = (Style)FindResource("SettingsCardStyle") };
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "完整的 AltSnap.ini；未在界面上列出的上游键（自定义区域、每动作黑名单等）可在这里修改。保存后重启引擎。",
            Style = (Style)FindResource("RowDescStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        _rawIniBox = new TextBox
        {
            Height = 300,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextAlignment = TextAlignment.Left,
            Style = (Style)FindResource("WinUITextBoxStyle"),
        };
        panel.Children.Add(_rawIniBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button
        {
            Content = "保存原始内容并重启引擎",
            Style = (Style)FindResource("AppButtonStyle"),
        };
        save.Click += SaveRaw_Click;
        var reload = new Button
        {
            Content = "从磁盘重新读取",
            Style = (Style)FindResource("AppButtonStyle"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        reload.Click += ReloadRaw_Click;
        buttons.Children.Add(save);
        buttons.Children.Add(reload);
        panel.Children.Add(buttons);
        card.Child = panel;
        host.Children.Add(card);
    }

    private FrameworkElement BuildRow(FieldDef field)
    {
        if (field.Kind is FieldKind.LongText)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
            panel.Children.Add(new TextBlock
            {
                Text = field.Title,
                Style = (Style)FindResource("RowTitleStyle"),
            });
            if (!string.IsNullOrEmpty(field.Desc))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = field.Desc,
                    Style = (Style)FindResource("RowDescStyle"),
                    Margin = new Thickness(0, 2, 0, 8),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            var box = new TextBox
            {
                Height = 62,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Style = (Style)FindResource("WinUITextBoxStyle"),
                FontSize = 12,
                TextAlignment = TextAlignment.Left,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, "AltSnap_" + field.Key);
            System.Windows.Automation.AutomationProperties.SetName(box, field.Title);
            panel.Children.Add(box);
            _texts[field.Key] = box;
            return panel;
        }

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = field.Title,
            Style = (Style)FindResource("RowTitleStyle"),
        });
        if (!string.IsNullOrEmpty(field.Desc))
        {
            left.Children.Add(new TextBlock
            {
                Text = field.Desc,
                Style = (Style)FindResource("RowDescStyle"),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        grid.Children.Add(left);

        FrameworkElement editor = field.Kind switch
        {
            FieldKind.Bool or FieldKind.Mask => BuildToggle(field),
            FieldKind.Int => BuildInt(field),
            FieldKind.Action => BuildAction(field),
            FieldKind.Choice => BuildChoice(field),
            _ => BuildText(field),
        };
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private ToggleButton BuildToggle(FieldDef field)
    {
        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("SwitchStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, "AltSnap_" + field.Key);
        System.Windows.Automation.AutomationProperties.SetName(toggle, field.Title);
        _toggles[field.Key] = toggle;
        return toggle;
    }

    private TextBox BuildInt(FieldDef field)
    {
        var box = new TextBox
        {
            Width = 120,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("WinUITextBoxStyle"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "AltSnap_" + field.Key);
        System.Windows.Automation.AutomationProperties.SetName(box, field.Title);
        _texts[field.Key] = box;
        return box;
    }

    private TextBox BuildText(FieldDef field)
    {
        var box = new TextBox
        {
            Width = 320,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("WinUITextBoxStyle"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "AltSnap_" + field.Key);
        System.Windows.Automation.AutomationProperties.SetName(box, field.Title);
        _texts[field.Key] = box;
        return box;
    }

    private static void SelectAction(ComboBox box, string value)
    {
        string target = value?.Trim() ?? "";
        foreach (var item in box.Items.OfType<string>())
        {
            if (string.Equals(ActionValue(item), target, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        // ini 里可能有列表外的自定义动作：动态加入并选中，避免空白
        string fallback = string.IsNullOrWhiteSpace(target) ? "无动作" : target;
        if (!box.Items.Contains(fallback))
        {
            box.Items.Add(fallback);
        }
        box.SelectedItem = fallback;
    }

    private static string ActionLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "无动作";
        }
        foreach (var option in ActionOptions)
        {
            if (string.Equals(option.Value, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return option.Label;
            }
        }
        return value; // 未知的自定义动作按原样显示
    }

    private static string ActionValue(string text)
    {
        string value = text.Trim();
        if (value.Length == 0)
        {
            return "Nothing";
        }
        foreach (var option in ActionOptions)
        {
            if (string.Equals(option.Label, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                return option.Value;
            }
        }
        return value;
    }

    private ComboBox BuildAction(FieldDef field)
    {
        var combo = new ComboBox
        {
            Width = 240,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxDropDownHeight = 320,
        };
        foreach (var option in ActionOptions)
        {
            combo.Items.Add(option.Label);
        }
        System.Windows.Automation.AutomationProperties.SetAutomationId(combo, "AltSnap_" + field.Key);
        System.Windows.Automation.AutomationProperties.SetName(combo, field.Title);
        _combos[field.Key] = combo;
        return combo;
    }

    private ComboBox BuildChoice(FieldDef field)
    {
        var combo = new ComboBox
        {
            Width = 220,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        foreach (var choice in field.Choices ?? [])
        {
            var parts = choice.Split('|', 2);
            combo.Items.Add(new ComboBoxItem
            {
                Content = parts.Length > 1 ? parts[1] : parts[0],
                Tag = parts[0],
            });
        }
        System.Windows.Automation.AutomationProperties.SetAutomationId(combo, "AltSnap_" + field.Key);
        System.Windows.Automation.AutomationProperties.SetName(combo, field.Title);
        _combos[field.Key] = combo;
        return combo;
    }

    // ------------------------------------------------------------------
    // 读写 ini
    // ------------------------------------------------------------------

    private static void EnsureIni()
    {
        try
        {
            Directory.CreateDirectory(IniDirectory);
            if (!File.Exists(IniPath) && File.Exists(DefaultIniPath))
            {
                File.Copy(DefaultIniPath, IniPath);
            }
        }
        catch
        {
            // 页面仍会提示配置文件不可用
        }
    }

    private static string ReadIni(string section, string key, string fallback)
    {
        var buffer = new StringBuilder(8192);
        GetPrivateProfileStringW(section, key, fallback, buffer, (uint)buffer.Capacity, IniPath);
        return buffer.ToString();
    }

    private static void WriteIni(string section, string key, string value)
        => WritePrivateProfileStringW(section, key, value, IniPath);

    private static int ReadInt(string section, string key, int fallback)
        => int.TryParse(ReadIni(section, key, fallback.ToString()), out int value) ? value : fallback;

    private void LoadAll()
    {
        foreach (var field in _schema)
        {
            string value = ReadIni(field.Section, field.Key, field.Default);
            switch (field.Kind)
            {
                case FieldKind.Bool:
                case FieldKind.Mask:
                    if (_toggles.TryGetValue(field.Key, out var toggle))
                    {
                        toggle.IsChecked = value.Trim() is "1" or "true" or "yes" or "on";
                    }
                    break;
                case FieldKind.Int:
                    if (_texts.TryGetValue(field.Key, out var intBox))
                    {
                        intBox.Text = value;
                    }
                    break;
                case FieldKind.Text:
                case FieldKind.LongText:
                    if (_texts.TryGetValue(field.Key, out var textBox))
                    {
                        textBox.Text = value;
                    }
                    break;
                case FieldKind.Action:
                    if (_combos.TryGetValue(field.Key, out var actionBox))
                    {
                        SelectAction(actionBox, value);
                    }
                    break;
                case FieldKind.Choice:
                    if (_combos.TryGetValue(field.Key, out var choiceBox))
                    {
                        SelectChoice(choiceBox, value);
                    }
                    break;
            }
        }

        try
        {
            if (_rawIniBox is not null)
            {
                _rawIniBox.Text = File.Exists(IniPath)
                    ? File.ReadAllText(IniPath, Encoding.Unicode)
                    : "";
                _rawIniBox.CaretIndex = 0;
                _rawIniBox.ScrollToHome();
            }
        }
        catch
        {
            if (_rawIniBox is not null)
            {
                _rawIniBox.Text = "";
            }
        }

        IniPathText.Text = IniPath;
    }

    private static void SelectChoice(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        bool ok = true;
        foreach (var field in _schema)
        {
            string? value = ReadControlValue(field, ref ok);
            if (value is not null)
            {
                WriteIni(field.Section, field.Key, value);
            }
        }
        SaveStatus.Text = ok ? "已保存，正在重启引擎…" : "部分数字无效，已跳过；其余已保存";
        AgentLink.Instance.SendAltSnapRestart();
        LoadAll();
        UpdateStatus();
    }

    private string? ReadControlValue(FieldDef field, ref bool ok)
    {
        switch (field.Kind)
        {
            case FieldKind.Bool:
            case FieldKind.Mask:
                if (!_toggles.TryGetValue(field.Key, out var toggle))
                {
                    return null;
                }
                bool on = toggle.IsChecked == true;
                if (field.Kind == FieldKind.Bool)
                {
                    return on ? "1" : "0";
                }
                int current = ReadInt(field.Section, field.Key, 0);
                int updated = on ? current | 1 : current & ~1;
                return updated.ToString();
            case FieldKind.Int:
                if (!_texts.TryGetValue(field.Key, out var intBox))
                {
                    return null;
                }
                if (!int.TryParse(intBox.Text.Trim(), out int number))
                {
                    ok = false;
                    return null;
                }
                return number.ToString();
            case FieldKind.Text:
            case FieldKind.LongText:
                return _texts.TryGetValue(field.Key, out var textBox) ? textBox.Text : null;
            case FieldKind.Action:
                if (!_combos.TryGetValue(field.Key, out var actionBox))
                {
                    return null;
                }
                if (actionBox.SelectedItem is string label)
                {
                    return ActionValue(label);
                }
                return ActionValue(actionBox.Text);
            case FieldKind.Choice:
                if (!_combos.TryGetValue(field.Key, out var choiceBox))
                {
                    return null;
                }
                return (choiceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? field.Default;
            default:
                return null;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(DefaultIniPath))
            {
                SaveStatus.Text = "找不到默认配置模板";
                return;
            }
            File.Copy(DefaultIniPath, IniPath, overwrite: true);
            LoadAll();
            SaveStatus.Text = "已恢复默认配置";
            AgentLink.Instance.SendAltSnapRestart();
        }
        catch (Exception exception)
        {
            SaveStatus.Text = "恢复默认失败：" + exception.Message;
        }
    }

    private void SaveRaw_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(IniDirectory);
            File.WriteAllText(IniPath, _rawIniBox?.Text ?? "", Encoding.Unicode);
            LoadAll();
            SaveStatus.Text = "原始配置已保存，正在重启引擎…";
            AgentLink.Instance.SendAltSnapRestart();
        }
        catch (Exception exception)
        {
            SaveStatus.Text = "保存原始配置失败：" + exception.Message;
        }
    }

    private void ReloadRaw_Click(object sender, RoutedEventArgs e)
    {
        LoadAll();
        SaveStatus.Text = "已从磁盘重新读取";
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureIni();
            Process.Start(new ProcessStartInfo(IniPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SaveStatus.Text = "打开配置文件失败：" + exception.Message;
        }
    }

    // ------------------------------------------------------------------
    // 工具开关/状态
    // ------------------------------------------------------------------

    private void ToolEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        bool enabled = ToolEnabledToggle.IsChecked == true;
        UiSettings.Load().SetToolEnabled(ToolKey, enabled);
        AgentLink.Instance.SendAltSnapEnabled(enabled);
        ApplyToolState(enabled);
        SaveStatus.Text = enabled ? "已启用" : "已停用";
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
    }

    private void OnAgentConfig()
    {
        var link = AgentLink.Instance;
        _syncing = true;
        ToolEnabledToggle.IsChecked = link.CurrentAltSnapEnabled;
        _syncing = false;
        ApplyToolState(link.CurrentAltSnapEnabled);
        if (!string.IsNullOrEmpty(link.CurrentAltSnapIniPath))
        {
            IniPathText.Text = link.CurrentAltSnapIniPath;
        }
        if (!string.IsNullOrEmpty(link.CurrentAltSnapEngine))
        {
            VersionText.Text = link.CurrentAltSnapEngine;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var link = AgentLink.Instance;
        bool enabled = ToolEnabledToggle.IsChecked == true;
        if (enabled && link.CurrentAltSnapRunning)
        {
            StatusText.Text = "随工具箱运行 · 引擎 " + (string.IsNullOrEmpty(link.CurrentAltSnapEngine) ? "1.0" : link.CurrentAltSnapEngine);
            StatusText.Foreground = (Brush)Application.Current.Resources["Accent"];
        }
        else if (enabled)
        {
            StatusText.Text = "引擎启动中…";
            StatusText.Foreground = (Brush)Application.Current.Resources["Text.Secondary"];
        }
        else
        {
            StatusText.Text = "已停用";
            StatusText.Foreground = (Brush)Application.Current.Resources["Text.Secondary"];
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralPane is null)
        {
            return; // InitializeComponent 期间提前触发
        }
        string tag = (sender as FrameworkElement)?.Tag as string ?? "General";
        GeneralPane.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        MousePane.Visibility = tag == "Mouse" ? Visibility.Visible : Visibility.Collapsed;
        KeyboardPane.Visibility = tag == "Keyboard" ? Visibility.Visible : Visibility.Collapsed;
        BlacklistPane.Visibility = tag == "Blacklist" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPane.Visibility = tag == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("builtin");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileStringW(
        string section, string key, string defaultValue, StringBuilder returned,
        uint size, string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileStringW(
        string section, string key, string value, string filePath);
}

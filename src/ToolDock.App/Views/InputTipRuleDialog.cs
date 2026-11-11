using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToolDock.InputTip;

namespace ToolDock.App.Views;

internal enum RuleKind
{
    Window,
    Hotkey,
    Display,
    Caret,
    Capture,
    Offset,
}

/// <summary>规则编辑对话框：按规则类型显示不同字段，确定后把值写回传入的规则对象。</summary>
internal sealed class InputTipRuleDialog : Window
{
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
        ("textMonitor", "文本监控"),
        ("hotkeyMonitor", "热键监控"),
        ("idleTimer", "空闲计时器"),
    ];

    private readonly InputTipRule? _rule;
    private readonly ImeRule? _imeRule;
    private readonly RuleKind _kind;
    private readonly StackPanel _fields = new();
    private readonly List<Action> _readers = new();

    public bool DeleteRequested { get; private set; }

    public InputTipRuleDialog(InputTipRule rule, RuleKind kind, string[]? allowedTriggers, InputTipSettings settings)
    {
        _rule = rule;
        _kind = kind;
        Title = kind switch
        {
            RuleKind.Window => "窗口规则",
            RuleKind.Hotkey => "热键规则",
            RuleKind.Capture => "光标捕获模式",
            RuleKind.Offset => "窗口偏移",
            _ => "显示/隐藏规则",
        };
        BuildWindow();
        BuildRuleFields(allowedTriggers);
    }

    public InputTipRuleDialog(ImeRule rule, InputTipSettings settings)
    {
        _imeRule = rule;
        _kind = RuleKind.Window;
        Title = "输入法状态规则";
        BuildWindow();
        BuildImeFields();
    }

    private void BuildWindow()
    {
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        if (Application.Current.TryFindResource("Bg.Solid") is Brush background)
        {
            Background = background;
        }
        if (Application.Current.TryFindResource("Text.Primary") is Brush foreground)
        {
            Foreground = foreground;
        }
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 500,
        };
        scroll.Content = _fields;
        root.Children.Add(scroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button
        {
            Content = "确定",
            MinWidth = 84,
            Height = 32,
            Style = Application.Current.TryFindResource("PrimaryButtonStyle") as Style,
        };
        ok.Click += (_, _) =>
        {
            foreach (var reader in _readers)
            {
                reader();
            }
            DialogResult = true;
        };
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 84,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Style = Application.Current.TryFindResource("AppButtonStyle") as Style,
        };
        cancel.Click += (_, _) => DialogResult = false;
        var delete = new Button
        {
            Content = "删除",
            MinWidth = 84,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Style = Application.Current.TryFindResource("AppButtonStyle") as Style,
        };
        delete.Click += (_, _) =>
        {
            DeleteRequested = true;
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        if (_rule is not null || _imeRule is not null)
        {
            buttons.Children.Add(delete);
        }
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        Content = root;
    }

    private void BuildRuleFields(string[]? allowedTriggers)
    {
        if (_rule is null)
        {
            return;
        }

        AddText("进程", _rule.Process, v => _rule.Process = v,
            "进程名（可正则），留空 = 所有进程。例如 Code.exe、msedge\\.exe|chrome\\.exe");
        if (_kind is not (RuleKind.Capture or RuleKind.Offset))
        {
            AddCombo("附加条件", ConditionOptions, _rule.Condition, v => _rule.Condition = v,
                _kind == RuleKind.Window ? "匹配窗口类名/标题，或文本/热键/空闲监控" : "匹配窗口类名/标题");
            AddText("窗口/控件类名", _rule.Class, v => _rule.Class = v, "正则，例如 Chrome_WidgetWin_1");
            AddText("窗口标题", _rule.Title, v => _rule.Title = v, "正则，例如 .*- Visual Studio Code");
        }

        if (_kind == RuleKind.Hotkey)
        {
            AddText("热键", _rule.Hotkey, v => _rule.Hotkey = v,
                "AutoHotkey 写法：^ Ctrl、! Alt、+ Shift、# Win；可写 ~LShift Up 表示单独松开左 Shift");
        }

        if (_kind is RuleKind.Window or RuleKind.Hotkey or RuleKind.Display or RuleKind.Caret)
        {
            var options = allowedTriggers is { Length: > 0 }
                ? TriggerOptions.Where(t => allowedTriggers.Contains(t.Value)).ToArray()
                : TriggerOptions;
            AddCombo("触发行为", options, _rule.Trigger, v => _rule.Trigger = v, "");
        }

        if (_kind == RuleKind.Window)
        {
            AddText("空闲计时器（毫秒）", _rule.IdleTimer, v => _rule.IdleTimer = v,
                "附加条件选「空闲计时器」时使用，例如 60000");
            AddText("文本监控", _rule.TextMonitor, v => _rule.TextMonitor = v,
                "附加条件选「文本监控」时使用：对最近输入的文本做正则匹配，例如 //\\s|\\n{2}");
            AddText("热键监控", _rule.HotkeyMonitor, v => _rule.HotkeyMonitor = v,
                "附加条件选「热键监控」时使用：按键序列用 > 连接，多个序列用 | 分隔，例如 ^a>^s|^a>^f");
        }

        if (_kind == RuleKind.Offset)
        {
            AddText("偏移量", _rule.Offset, v => _rule.Offset = v,
                "屏幕号/x/y，多屏用 | 分隔，例如 1/-2/0|2/0/0（逻辑像素）");
        }

        if (_kind == RuleKind.Capture)
        {
            AddText("捕获模式", _rule.Capture, v => _rule.Capture = v,
                "按顺序尝试，用 > 分隔；支持 GUI、UIA、MSAA、ACC、WPF、HOOK、HOOK_DLL，例如 GUI>UIA>MSAA");
            AddText("捕获偏移", _rule.CaptureOffset, v => _rule.CaptureOffset = v,
                "与捕获模式用 > 对齐的 x/y，例如 0/0>1/0；留空表示不偏移");
        }
    }

    private void BuildImeFields()
    {
        if (_imeRule is null)
        {
            return;
        }
        AddText("状态码规则", _imeRule.OpenRule, v => _imeRule.OpenRule = v,
            "留空 = 任意；数字/用 / 分隔的候选值，或 oddNum / evenNum");
        AddText("转换码规则", _imeRule.ConversionRule, v => _imeRule.ConversionRule = v,
            "留空 = 任意；例如 1、1025、1/1025，或 oddNum / evenNum");
        AddCombo("输入法状态", [("EN", "英文"), ("CN", "中文")], _imeRule.State, v => _imeRule.State = v, "");
    }

    private void AddText(string title, string value, Action<string> setter, string hint)
    {
        var label = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4),
        };
        _fields.Children.Add(label);
        var box = new TextBox
        {
            Text = value ?? "",
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = Application.Current.TryFindResource("WinUITextBoxStyle") as Style,
        };
        _fields.Children.Add(box);
        if (!string.IsNullOrEmpty(hint))
        {
            _fields.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        _readers.Add(() => setter(box.Text.Trim()));
    }

    private void AddCombo(string title, (string Value, string Label)[] items, string value,
        Action<string> setter, string hint)
    {
        var label = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4),
        };
        _fields.Children.Add(label);
        var combo = new ComboBox
        {
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        foreach (var (itemValue, itemLabel) in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = itemLabel, Tag = itemValue });
        }
        var selected = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            combo.SelectedItem = selected;
        }
        else if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
        _fields.Children.Add(combo);
        if (!string.IsNullOrEmpty(hint))
        {
            _fields.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        _readers.Add(() =>
        {
            if (combo.SelectedItem is ComboBoxItem item)
            {
                setter(item.Tag as string ?? "");
            }
        });
    }
}

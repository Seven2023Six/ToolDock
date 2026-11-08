using System.Windows;
using System.Windows.Controls;
using ToolDock.App.Services;
using ToolDock.App.Views;

namespace ToolDock.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, Control> _pageCache = new();

    public MainWindow()
    {
        InitializeComponent();

        NativeBackdrop.EnableDarkMode(this, ThemeService.Current == ThemeService.Dark);
        ApplyBackdropToWindow();

        ThemeService.ThemeChanged += OnThemeChanged;
        BackdropService.BackdropChanged += OnBackdropChanged;

        // XAML 中 IsChecked="True" 触发的 Checked 在 InitializeComponent 期间被守卫跳过，这里补上首屏
        ShowPage("home");
    }

    private void OnThemeChanged(string theme)
    {
        NativeBackdrop.EnableDarkMode(this, theme == ThemeService.Dark);
    }

    private void OnBackdropChanged(string mode)
    {
        ApplyBackdropToWindow();
    }

    /// <summary>按当前材质重涂窗口：Mica/亚克力失败自动回退纯色（对齐 VigilWatt ApplyBackdrop 策略）。</summary>
    private void ApplyBackdropToWindow()
    {
        var mode = BackdropService.Current;
        if (mode != BackdropService.Solid)
        {
            bool applied = mode == BackdropService.Acrylic
                ? NativeBackdrop.TryApplyAcrylic(this)
                : NativeBackdrop.TryApplyMica(this);
            if (applied)
            {
                Root.Background = null; // 材质透出
                return;
            }
        }
        NativeBackdrop.SetBackdropNone(this);
        Root.Background = (System.Windows.Media.Brush)FindResource("Bg.Solid");
    }

    /// <summary>Agent 通知"界面被再次请求"（托盘又点了一次打开）：置前已有窗口。</summary>
    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    /// <summary>
    /// 关窗不退出：隐藏到托盘，电量监听与任务栏贴片继续工作。
    /// 真正退出由 App.ExitApp() 触发（Agent 托盘「退出」先通知界面）。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        BackdropService.BackdropChanged -= OnBackdropChanged;
        base.OnClosed(e);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 里 IsChecked="True" 会在 InitializeComponent 期间触发本事件，此时页面表还没建好
        if (_pageCache.Count == 0)
        {
            return;
        }
        if (sender is RadioButton { CommandParameter: string key })
        {
            ShowPage(key);
        }
    }

    /// <summary>页面切换（工具页内的返回按钮也走这里）。</summary>
    public void ShowPage(string key)
    {
        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "home" => new HomePage(),
                "battery" => new BatteryPage(),
                "builtin" => new BuiltinToolsPage(),
                "utility" => new UtilityToolsPage(),
                "memclean" => new MemCleanPage(),
                "custom" => new CustomToolsPage(),
                "settings" => new SettingsPage(),
                "about" => new AboutPage(),
                _ => null,
            };
            if (page is not null)
            {
                _pageCache[key] = page;
            }
        }
        if (page is not null)
        {
            PageHost.Content = page;
        }
    }
}

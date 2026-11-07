using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using ToolDock.App.Views;

namespace ToolDock.App;

/// <summary>
/// 主窗外壳（视觉结构对齐 WPFGallery MainWindow）：
/// 44px 自绘标题栏 + 左侧树形导航 + 底部设置按钮 + 圆角内容卡片。
/// 主题由 App.OnStartup 的 ThemeMode 驱动（WPF Fluent 内置）。
/// </summary>
public partial class MainWindow : Window
{
    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new("首页", "\uE80F", "home"),
        new("设备电量", "\uE850", "battery"),
        new("内存清理", "\uE74F", "memclean"),
    };

    private bool _navLocking;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 50,
            CornerRadius = new CornerRadius(12),
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(4),
            UseAeroCaptionButtons = true,
            NonClientFrameEdges = NonClientFrameEdges.Right | NonClientFrameEdges.Bottom | NonClientFrameEdges.Left,
        });

        StateChanged += (_, _) => UpdateMainWindowVisuals();
        SelectNav("home");
        ShowPage("home");
    }

    private void UpdateMainWindowVisuals()
    {
        MainGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : default;
    }

    /// <summary>按 PageKey 选中导航项（TreeView.SelectedItem 只读，需经容器设置）。</summary>
    private void SelectNav(string pageKey)
    {
        _navLocking = true;
        for (int i = 0; i < NavItems.Count; i++)
        {
            if (NavList.ItemContainerGenerator.ContainerFromIndex(i) is TreeViewItem tvi)
            {
                tvi.IsSelected = NavItems[i].PageKey == pageKey;
            }
        }
        _navLocking = false;
    }

    /// <summary>托盘"打开"时置前（Agent 经管道推 activate 消息）。</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Focus();
    }

    private void ShowPage(string key)
    {
        PageHost.Content = key switch
        {
            "battery" => new BatteryPage(),
            "memclean" => new MemCleanPage(),
            "settings" => new SettingsPage(),
            _ => new HomePage(),
        };
    }

    private void NavList_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_navLocking || e.NewValue is not NavItem item)
        {
            return;
        }
        ShowPage(item.PageKey);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // 设置固定在导航底部（Gallery 模式）；清除列表选中态避免与列表项冲突
        _navLocking = true;
        for (int i = 0; i < NavItems.Count; i++)
        {
            if (NavList.ItemContainerGenerator.ContainerFromIndex(i) is TreeViewItem tvi)
            {
                tvi.IsSelected = false;
            }
        }
        _navLocking = false;
        ShowPage("settings");
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeIcon.Text = "\uE922";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeIcon.Text = "\uE923";
        }
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}

/// <summary>导航项（图标字形来自 Segoe Fluent Icons）。</summary>
public record NavItem(string Title, string IconGlyph, string PageKey)
{
    public Collection<NavItem> Items { get; } = new();
}

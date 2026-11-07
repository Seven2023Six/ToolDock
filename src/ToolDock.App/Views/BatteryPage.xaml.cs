using System.Windows;
using System.Windows.Controls;

namespace ToolDock.App.Views;

/// <summary>外设电量显示外壳：设备 / 统计 / 历史 / 设置 四个子页（对应 VigilWatt 的四个主页面）。</summary>
public partial class BatteryPage : UserControl
{
    private readonly Dictionary<string, UserControl> _cache = new();

    public BatteryPage()
    {
        InitializeComponent();
        // 初始化期间的 Checked 事件不可靠（TabHost 未就绪/分组重入），首屏放到 Loaded 后显式指定
        Loaded += (_, _) => ShowTab("devices");
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" 会在 InitializeComponent 期间触发，此时 TabHost 还没建好
        if (sender is RadioButton { IsChecked: true, Tag: string key } && TabHost is not null && IsLoaded)
        {
            ShowTab(key);
        }
    }

    private void ShowTab(string key)
    {
        if (TabHost is null)
        {
            return;
        }
        if (!_cache.TryGetValue(key, out var view))
        {
            view = key switch
            {
                "stats" => new BatteryStatsView(),
                "history" => new BatteryHistoryView(),
                "capture" => new BatteryCaptureView(),
                "settings" => new BatterySettingsView(),
                _ => new BatteryDevicesView(),
            };
            _cache[key] = view;
        }
        TabHost.Content = view;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("builtin");
}

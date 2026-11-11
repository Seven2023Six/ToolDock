using System.Windows;
using System.Windows.Controls;

namespace ToolDock.App.Views;

/// <summary>
/// 内置工具页（第二栏）：图吧工具箱式的图标网格。
/// 卡片为 Button：单击打开对应工具页；是否启用由**工具页面内部**的开关决定，
/// 这里只反映状态（停用的卡片变灰并显示「已停用」徽标，仍可点进去重新启用）。
/// </summary>
public partial class BuiltinToolsPage : UserControl
{
    public BuiltinToolsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyToolStates();
    }

    private void ApplyToolStates()
    {
        var settings = Services.UiSettings.Load();
        ApplyCardState(MemCleanCard, MemCleanBadge, settings.IsToolEnabled("memclean"));
        ApplyCardState(BatteryCard, BatteryBadge, settings.IsToolEnabled("battery"));
        ApplyCardState(AltSnapCard, AltSnapBadge, settings.IsToolEnabled("altsnap"));
        ApplyCardState(InputTipCard, InputTipBadge, settings.IsToolEnabled("inputtip"));
        // MAS 激活工具无常驻状态：没有启用开关，卡片始终可用
    }

    private static void ApplyCardState(Button card, Border badge, bool enabled)
    {
        card.Opacity = enabled ? 1.0 : 0.5;
        badge.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MemClean_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("memclean");

    private void Battery_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("battery");

    private void AltSnap_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("altsnap");

    private void InputTip_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("inputtip");

    private void Mas_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage(Services.MasService.ToolKey);
}

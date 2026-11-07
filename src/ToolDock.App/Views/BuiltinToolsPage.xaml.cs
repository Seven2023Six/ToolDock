using System.Windows;
using System.Windows.Controls;

namespace ToolDock.App.Views;

/// <summary>
/// 内置工具页（第二栏）：图吧工具箱式的图标网格。
/// 卡片为 Button（键盘可达、UIA 可触发）：单击 → 主界面切换为对应工具页面。
/// </summary>
public partial class BuiltinToolsPage : UserControl
{
    public BuiltinToolsPage()
    {
        InitializeComponent();
    }

    private void MemClean_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("memclean");

    private void Battery_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("battery");
}

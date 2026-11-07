using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace ToolDock.App.Views;

/// <summary>
/// 内置工具页（第二栏）：图吧工具箱式的图标网格。
/// 单击工具卡片 → 主界面切换为对应工具页面；工具页内「‹ 返回」回到本网格。
/// </summary>
public partial class BuiltinToolsPage : UserControl
{
    public BuiltinToolsPage()
    {
        InitializeComponent();
    }

    private void ToolCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string key })
        {
            (Window.GetWindow(this) as MainWindow)?.ShowPage(key);
        }
    }
}

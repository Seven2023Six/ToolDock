using System.Windows.Controls;

namespace ToolDock.App.Views;

/// <summary>
/// 自定义工具页：图吧工具箱式的分类工具网格。
/// 工具以「图标 + 名称」呈现，规划交互：单击选中查看说明，双击启动。
/// 当前为空态，工具清单由后续版本注入（内置工具 / 用户自定义）。
/// </summary>
public partial class CustomToolsPage : UserControl
{
    public CustomToolsPage()
    {
        InitializeComponent();
    }
}

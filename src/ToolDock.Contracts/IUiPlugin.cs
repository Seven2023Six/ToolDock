using System.Windows;
using ToolDock.Abstractions;

namespace ToolDock.Contracts;

/// <summary>
/// 界面插件契约：编译为独立 DLL 放进 plugins/&lt;id&gt;/，由 WPF App 运行时（AssemblyLoadContext）加载。
/// 页面懒创建——CreatePage() 只在用户首次打开该页时被调用。
/// 只需要后台能力的插件请实现 ToolDock.Abstractions.IBackgroundPlugin（编译进 Agent）。
/// </summary>
public interface IUiPlugin
{
    PluginMetadata Metadata { get; }

    /// <summary>App 启动后调用（后台线程）。抛异常 = 本插件标记故障，App 与其他插件不受影响。</summary>
    Task InitializeAsync(IUiContext context, CancellationToken cancellationToken);

    /// <summary>主窗/快捷面板共用的页面。首次访问才调用。</summary>
    FrameworkElement CreatePage();

    /// <summary>App 退出时调用。尽力清理，失败被外壳吞掉并记日志。</summary>
    Task ShutdownAsync();
}

/// <summary>界面插件的上下文：Agent 能力 + App 特有能力。</summary>
public interface IUiContext : IAgentContext
{
    /// <summary>请求 App 把主窗口导航到本插件的页面（如托盘菜单点击时）。</summary>
    void ShowPage(string pluginId);
}

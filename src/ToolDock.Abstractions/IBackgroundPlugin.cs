namespace ToolDock.Abstractions;

/// <summary>
/// 后台插件契约：运行在常驻 Agent 进程内（B2 架构下为源码编译集成，非 DLL 热加载）。
/// 只做后台逻辑：轮询、监控、通知、托盘贡献；不得引用任何 UI 类型。
/// </summary>
public interface IBackgroundPlugin
{
    PluginMetadata Metadata { get; }

    /// <summary>Agent 启动时调用（后台线程）。抛异常 = 本插件标记故障，Agent 与其他插件不受影响。</summary>
    Task InitializeAsync(IAgentContext context, CancellationToken cancellationToken);

    /// <summary>Agent 退出时调用（逆序）。尽力清理，失败被吞掉并记日志。</summary>
    Task ShutdownAsync();
}

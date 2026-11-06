using ToolDock.Abstractions;

namespace ToolDock.Agent;

/// <summary>
/// 架构验证用示例插件：往托盘菜单贡献一项、往 tooltip 写一行状态。
/// 它证明了"后台插件 → 托盘贡献 → 气泡通知"整条链路。
/// </summary>
internal sealed class SamplePlugin : IBackgroundPlugin
{
    public PluginMetadata Metadata { get; } = new("sample", "示例插件", "0.2.0");

    public Task InitializeAsync(IAgentContext context, CancellationToken cancellationToken)
    {
        context.Log.Info("示例插件已启动");
        context.Tray.AddMenu("示例插件 · 弹个气泡",
            () => context.Notify.Show("示例插件", $"后台插件在工作：{DateTime.Now:HH:mm:ss}"));
        context.Tray.SetStatusLine("示例插件在线");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}

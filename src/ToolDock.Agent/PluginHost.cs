using ToolDock.Abstractions;

namespace ToolDock.Agent;

/// <summary>
/// 后台插件宿主（B2：插件源码编译进 Agent，此处登记实例）。
/// 加新插件 = 写一个 IBackgroundPlugin 类 + 在这里加一行。任何插件故障都不拖垮 Agent。
/// </summary>
internal sealed class PluginHost
{
    private readonly List<IBackgroundPlugin> _plugins = new()
    {
        new SamplePlugin(),
    };

    public void StartAll(TrayIconController tray)
    {
        foreach (var plugin in _plugins)
        {
            try
            {
                plugin.InitializeAsync(new PluginContext(plugin.Metadata.Id, tray), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                LogFile.Write($"[host] 插件 {plugin.Metadata.Id} 初始化失败：{exception.Message}");
            }
        }
    }

    public void ShutdownAll()
    {
        for (int i = _plugins.Count - 1; i >= 0; i--)
        {
            try
            {
                _plugins[i].ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                LogFile.Write($"[host] 插件 {_plugins[i].Metadata.Id} 收尾失败：{exception.Message}");
            }
        }
    }
}

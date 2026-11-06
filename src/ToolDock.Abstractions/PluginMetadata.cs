namespace ToolDock.Abstractions;

/// <summary>插件元数据。后台插件编译进 Agent；界面插件为独立 DLL 由 App 加载。</summary>
public sealed record PluginMetadata(string Id, string Name, string Version, string Category = "通用");

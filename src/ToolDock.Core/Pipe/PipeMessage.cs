using System.Text.Json.Serialization;

namespace ToolDock.Core;

/// <summary>Agent ⇄ App 管道消息（单类承载全部消息类型，字段随用随加）。</summary>
public sealed class PipeMessage
{
    /// <summary>消息类型：hello / welcome / getSettings / settings / setTheme / setBackdrop / setAutoStart / activate</summary>
    public string? T { get; set; }

    /// <summary>协议版本（hello）。</summary>
    public int Ver { get; set; }

    /// <summary>主题名：Light / Dark（welcome / settings / setTheme）。</summary>
    public string? Theme { get; set; }

    /// <summary>窗口材质：Mica / Acrylic / Solid（welcome / settings / setBackdrop）。</summary>
    public string? Backdrop { get; set; }

    /// <summary>开机自启开关状态（welcome / settings / setAutoStart）。</summary>
    public bool AutoStart { get; set; }

    /// <summary>开关目标状态（setAutoStart）。</summary>
    public bool Enable { get; set; }

    /// <summary>true = 已有界面窗口在跑，本实例应立即退出（welcome）。</summary>
    public bool Duplicate { get; set; }
}

/// <summary>
/// JSON 源生成上下文：Agent 走 NativeAOT，不能用反射序列化，
/// 所以管道协议与设置文件统一走这里（App 侧共用，保证两边字节级一致）。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PipeMessage))]
[JsonSerializable(typeof(AppSettings))]
public partial class ToolDockJsonContext : JsonSerializerContext
{
}

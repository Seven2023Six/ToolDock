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

    /// <summary>清理区域掩码（welcome / settings / setCleanCfg）。</summary>
    public ulong Mask { get; set; }

    /// <summary>自动清理开关（welcome / settings / setCleanCfg）。</summary>
    public bool AutoClean { get; set; }

    /// <summary>自动清理阈值 %（welcome / settings / setCleanCfg）。</summary>
    public uint Threshold { get; set; }

    /// <summary>定时清理间隔分钟（welcome / settings / setCleanCfg）。</summary>
    public uint IntervalM { get; set; }

    /// <summary>免 UAC 计划任务开关（welcome / settings / setCleanCfg）。</summary>
    public bool SkipUac { get; set; }

    /// <summary>自动清理时包含冻结区（备用/已修改列表）。对齐 Mem Reduct IsAllowStandbyListCleanup，默认 false（welcome / setCleanCfg）。</summary>
    public bool AllowFreezes { get; set; }

    /// <summary>手动清理前弹出确认（welcome / setCleanCfg）。对齐 Mem Reduct IsShowReductConfirmation，默认 true。</summary>
    public bool ConfirmClean { get; set; }

    /// <summary>cleanNow：发起方是否已确认。true 跳过 Agent 端确认框（App 已确认）。</summary>
    public bool Confirmed { get; set; }

    /// <summary>开关目标状态（setAutoStart）。</summary>
    public bool Enable { get; set; }

    /// <summary>true = 已有界面窗口在跑，本实例应立即退出（welcome）。</summary>
    public bool Duplicate { get; set; }
}

/// <summary>内存清理插件的持久化设置（plugindata/memclean.json，键为 PascalCase）。供免 UAC 助手运行时读取掩码，任务因此只需安装一次。</summary>
public sealed class MemCleanPluginData
{
    public ulong Mask { get; set; } = 0xE7;
    public bool AllowFreezes { get; set; }
}

/// <summary>插件设置文件（PascalCase 键）专用上下文：无命名策略，与落盘键名精确匹配。</summary>
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(MemCleanPluginData))]
public partial class MemCleanDataContext : JsonSerializerContext
{
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

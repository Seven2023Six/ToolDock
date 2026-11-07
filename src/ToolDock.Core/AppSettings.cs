using System.IO;
using System.Text.Json;

namespace ToolDock.Core;

/// <summary>
/// 全局设置（%APPDATA%\ToolDock\settings.json）。
/// 便携模式（exe 旁 ini）留待 P2 #10 定案后扩展，接口不变。
/// </summary>
public sealed class AppSettings
{
    public bool AutoStart { get; set; }

    /// <summary>界面主题：Light（默认）/ Dark。</summary>
    public string Theme { get; set; } = "Light";

    /// <summary>窗口材质：Mica / Acrylic / Solid。M0 先固定 Mica，设置页 M3 接线。</summary>
    public string Backdrop { get; set; } = "Mica";

    /// <summary>内存清理：区域掩码（默认与 Mem Reduct REDUCT_MASK_DEFAULT 一致：0xE7）。</summary>
    public ulong AutoCleanMask { get; set; } = 0xE7;

    /// <summary>内存清理：自动清理开关。</summary>
    public bool AutoCleanEnabled { get; set; }

    /// <summary>内存清理：自动清理阈值 %。</summary>
    public uint AutoCleanThreshold { get; set; } = 90;

    /// <summary>内存清理：定时清理间隔分钟。</summary>
    public uint AutoCleanIntervalMinutes { get; set; } = 30;

    /// <summary>内存清理：免 UAC 计划任务开关。</summary>
    public bool AutoCleanSkipUac { get; set; }

    /// <summary>内存清理：自动清理时包含冻结区（对齐 Mem Reduct IsAllowStandbyListCleanup，默认 false）。</summary>
    public bool AutoCleanAllowFreezes { get; set; }

    /// <summary>内存清理：手动清理前确认（对齐 Mem Reduct IsShowReductConfirmation，默认 true）。</summary>
    public bool AutoCleanConfirm { get; set; } = true;

    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // 源生成序列化：Agent 是 NativeAOT，不能用反射版 JsonSerializer
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), ToolDockJsonContext.Default.AppSettings)
                       ?? new AppSettings();
            }
        }
        catch
        {
            // 设置损坏不该挡住启动，回退默认值即可
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, ToolDockJsonContext.Default.AppSettings));
    }
}

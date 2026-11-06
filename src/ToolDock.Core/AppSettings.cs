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

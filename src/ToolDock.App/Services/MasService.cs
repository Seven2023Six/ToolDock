using System.IO;
using System.Text;
using ToolDock.App.Services.Mas;

namespace ToolDock.App.Services;

/// <summary>
/// MAS（Microsoft Activation Scripts）激活工具门面：脚本部署、启动、检查更新。
/// 具体实现拆分在 Mas/ 目录：MasPatcher（汉化补丁）、MasUpdater（更新下载与校验）、MasLauncher（启动与监视）。
/// </summary>
internal static class MasService
{
    public const string ToolKey = "mas";

    private const string ScriptName = "MAS_AIO.cmd";

    private static string LocalDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "mas");

    internal static string LocalScript => Path.Combine(LocalDir, ScriptName);

    /// <summary>本地脚本版本（解析 @set masver=），无脚本时为 null。</summary>
    public static string? LocalVersion { get; private set; }

    public static bool HasLocalScript => File.Exists(LocalScript);

    /// <summary>本地脚本的最近修改时间（用于界面展示）。</summary>
    public static DateTime? LocalModified => HasLocalScript ? File.GetLastWriteTime(LocalScript) : null;

    /// <summary>
    /// 汉化运行脚本的落盘候选目录（按优先级）：
    /// 1) 安装目录 tools/mas（用户信任区内，杀软不删）；
    /// 2) %APPDATA%\ToolDock\mas（可写）；
    /// 3) %LOCALAPPDATA%\ToolDock\mas-cn（安装目录只读时的兜底）。
    /// </summary>
    private static IReadOnlyList<string> PatchedDirectories()
    {
        var directories = new List<string>();
        string? bundled = FindBundledScript();
        if (bundled is not null)
        {
            string? bundledDir = Path.GetDirectoryName(bundled);
            if (!string.IsNullOrEmpty(bundledDir))
            {
                directories.Add(bundledDir);
            }
        }
        directories.Add(LocalDir);
        directories.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolDock", "mas-cn"));
        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static MasService()
    {
        // GBK（cp936）编码提供程序：控制台默认代码页为 936，汉化文案以 GBK 写入即可原生显示。
        // 不用 UTF-8 + chcp 65001——cmd 在 65001 下按块解析批处理会错位吞行（实测 [6]/[E] 行被执行成命令）。
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 注册失败时回退 UTF-8，中文可能乱码但脚本功能不受影响
        }
        EnsureLocalScript();
    }

    /// <summary>运行时副本缺失时从随包分发的脚本补齐（更新只写运行时副本，不动安装目录）。</summary>
    public static void EnsureLocalScript()
    {
        try
        {
            if (File.Exists(LocalScript))
            {
                LocalVersion = MasPatcher.ParseVersion(File.ReadAllText(LocalScript));
                return;
            }
            string? bundled = FindBundledScript();
            if (bundled is null)
            {
                return;
            }
            Directory.CreateDirectory(LocalDir);
            File.Copy(bundled, LocalScript, overwrite: true);
            LocalVersion = MasPatcher.ParseVersion(File.ReadAllText(LocalScript));
        }
        catch
        {
            // 部署失败不致命：界面会显示脚本缺失，检查更新可恢复
        }
    }

    private static string? FindBundledScript()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            // 发布布局：App csproj 把 assets/tools/mas 分发到 <输出>/tools/mas
            Path.Combine(baseDir, "tools", "mas", ScriptName),
            // 开发布局：App 在 src/ToolDock.App/bin/...，回溯到 src 找 Agent 的输出
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "ToolDock.Agent", "bin", "Release", "net8.0-windows", "tools", "mas", ScriptName),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "ToolDock.Agent", "bin", "Debug", "net8.0-windows", "tools", "mas", ScriptName),
            // 兜底：仓库资产目录
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "assets", "tools", "mas", ScriptName),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    // ---- 启动 ----

    /// <summary>
    /// 打开 MAS 控制台菜单（请求一次管理员授权，与官方引导行为一致）。
    /// 立即返回；外层 cmd 可能因脚本自带的自提权重启而秒退，真正的菜单在重启后的控制台里（约 5-10 秒后出现）。
    /// </summary>
    public static (bool Ok, string Error) Launch()
    {
        EnsureLocalScript();
        if (!File.Exists(LocalScript))
        {
            Log($"启动失败：本地脚本不存在 {LocalScript}");
            return (false, "找不到内置脚本 MAS_AIO.cmd，请先执行「检查更新」获取");
        }
        string payload = File.ReadAllText(LocalScript);
        if (!payload.Contains("@set masver=", StringComparison.Ordinal))
        {
            Log("启动失败：本地脚本内容校验失败（缺 @set masver=）");
            return (false, "本地脚本内容校验失败，请执行「检查更新」重新获取");
        }

        if (!MasLauncher.EnsurePatchedFile(PatchedDirectories(), payload, Log, out string patchedFile, out string error))
        {
            Log("启动失败：" + error);
            return (false, error);
        }

        return MasLauncher.Launch(patchedFile, Log, message => LaunchDiagnostics?.Invoke(message));
    }

    /// <summary>启动诊断回调（后台线程触发）：秒退且菜单未出现时通知界面提示用户处理杀软拦截。</summary>
    public static event Action<string>? LaunchDiagnostics;

    // ---- 检查更新 ----

    /// <summary>
    /// 从官方引导脚本解析最新发布（下载源 + SHA-256 清单），拉取校验后替换本地副本。
    /// 返回 (是否成功, 展示给用户的消息)。
    /// </summary>
    public static async Task<(bool Ok, string Message)> CheckUpdateAsync()
    {
        var (ok, message, version) = await MasUpdater.CheckUpdateAsync(LocalScript, LocalDir, Log);
        if (version is not null)
        {
            LocalVersion = version;
        }
        return (ok, message);
    }

    /// <summary>日志（与界面日志同文件，便于与用户操作对照排障）。</summary>
    internal static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            Core.LogRotation.Prepare(path);
            File.AppendAllText(path,
                $"{DateTime.Now:HH:mm:ss.fff} [mas] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响启动
        }
    }
}

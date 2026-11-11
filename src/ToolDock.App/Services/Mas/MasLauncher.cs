using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ToolDock.App.Services.Mas;

/// <summary>
/// MAS 启动器：准备汉化脚本 → 以管理员身份拉起控制台 → 监视菜单窗口。
/// 启动契约（重要）：MAS 脚本会因 QuickEdit 重置而**自提权重启**，
/// 外层 cmd 常在 1-3 秒内退出（退出码 0）属正常设计，真正的菜单出现在重启后的控制台里。
/// </summary>
internal static class MasLauncher
{
    internal const string PatchedFileName = "MAS_AIO_CN.cmd";

    /// <summary>
    /// 准备汉化脚本并按候选目录顺序落盘（安装目录 → %APPDATA% → %LOCALAPPDATA%）。
    /// 返回 false 时 message 可直接展示给用户。
    /// </summary>
    internal static bool EnsurePatchedFile(
        IReadOnlyList<string> candidateDirectories,
        string payload,
        Action<string> log,
        out string patchedFile,
        out string message)
    {
        patchedFile = "";
        message = "";

        string content = MasPatcher.BuildContent(payload, out int applied);
        log($"汉化补丁：替换 {applied}/{MasPatcher.MenuPatch.Length} 处");

        string lastError = "";
        foreach (string directory in candidateDirectories)
        {
            try
            {
                string path = Path.Combine(directory, PatchedFileName);

                // 已存在且内容一致：直接复用（避免每次启动都新写文件引来杀软扫描）
                try
                {
                    if (File.Exists(path) && File.ReadAllText(path, MasPatcher.GbkEncoding()) == content)
                    {
                        patchedFile = path;
                        return true;
                    }
                }
                catch
                {
                    // 读取失败按不存在处理，继续重写
                }

                Directory.CreateDirectory(directory);
                File.WriteAllText(path, content, MasPatcher.GbkEncoding());
                Thread.Sleep(300); // 留给杀软实时扫描一小段时间，立刻检查是否被删
                if (!File.Exists(path))
                {
                    lastError = "写入后立即被删除（杀毒软件拦截）";
                    log($"汉化脚本被杀软删除：{path}");
                    continue;
                }

                patchedFile = path;
                log($"汉化脚本就绪：{path}");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                lastError = "目录无写入权限";
            }
            catch (Exception exception)
            {
                lastError = exception.Message;
            }
        }

        message = "无法准备汉化脚本：" + lastError +
                  "\r\n请将 ToolDock 安装目录或 %APPDATA%\\ToolDock\\mas\\ 加入杀毒软件信任区后重试。";
        return false;
    }

    /// <summary>
    /// 启动 MAS 控制台（一次 UAC）。立即返回；后台任务监视菜单窗口并做秒退诊断。
    /// </summary>
    internal static (bool Ok, string Error) Launch(
        string patchedFile,
        Action<string> log,
        Action<string>? diagnostics)
    {
        try
        {
            // -el = 官方自提权参数；外层再以 runas 启动，全程只有一次 UAC
            var startInfo = new ProcessStartInfo("cmd.exe", $"/c \"\"{patchedFile}\" -el\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            var process = Process.Start(startInfo);
            log($"cmd 已启动 pid={process?.Id}");
            DateTime startedAt = DateTime.UtcNow;
            if (process is null)
            {
                return (false, "启动失败");
            }

            _ = Task.Run(() =>
            {
                bool menuSeen = WaitForMenuWindow(TimeSpan.FromSeconds(30));
                bool exited = false;
                int exitCode = -1;
                try
                {
                    if (process.WaitForExit(30000))
                    {
                        exited = true;
                        exitCode = process.ExitCode;
                    }
                }
                catch
                {
                    // 句柄失效即可忽略
                }
                log($"监视结果：菜单窗口={(menuSeen ? "已出现" : "未出现")} 外层进程退出={exited} 退出码={(exited ? exitCode.ToString() : "—")}");

                // 外层秒退且始终没有菜单窗口：最常见原因是杀毒软件删除/拦截了脚本
                if (exited && !menuSeen && DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(15))
                {
                    log("疑似被杀毒软件拦截（控制台秒退且菜单未出现）");
                    diagnostics?.Invoke("控制台立即退出且未显示菜单——脚本很可能被杀毒软件删除或拦截。\r\n请将 ToolDock 安装目录与 %APPDATA%\\ToolDock\\mas\\ 加入杀毒软件信任区后重试。");
                }
            });
            return (true, "");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            log("启动中止：用户取消了管理员授权（UAC 1223）");
            return (false, "管理员授权被取消，未启动");
        }
        catch (Exception exception)
        {
            log($"启动异常：{exception.GetType().Name} {exception.Message}");
            return (false, exception.Message);
        }
    }

    /// <summary>后台监视：MAS 菜单窗口是否出现（标题含 MAS 激活工具 / Microsoft_Activation_Scripts）。</summary>
    internal static bool WaitForMenuWindow(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var window = Process.GetProcesses()
                    .FirstOrDefault(p => p.MainWindowTitle.Contains("Microsoft_Activation_Scripts", StringComparison.OrdinalIgnoreCase)
                                         || p.MainWindowTitle.Contains("MAS 激活工具", StringComparison.Ordinal));
                if (window is not null)
                {
                    return true;
                }
            }
            catch
            {
                // 枚举失败下一轮重试
            }
            Thread.Sleep(500);
        }
        return false;
    }
}

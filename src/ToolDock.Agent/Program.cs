using System.Diagnostics;
using ToolDock.Agent;
using ToolDock.Core;

internal static class Program
{
    private static readonly CancellationTokenSource LifetimeCts = new();
    private static TrayIconController? _tray;

    [STAThread]
    private static int Main(string[] args)
    {
        LogFile.Write("[agent] 进程启动");
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            // AOT + WinExe 没有控制台，启动期异常只能落日志
            LogFile.Write($"[agent] 致命错误：{exception}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        // 命令行清理（对齐 Mem Reduct /clean）：/clean [掩码] —— 需要管理员权限，不启动托盘
        int cleanIndex = Array.FindIndex(args, a =>
            a.Equals("/clean", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--clean", StringComparison.OrdinalIgnoreCase));
        if (cleanIndex >= 0)
        {
            if (!CleanupCore.IsElevated())
            {
                LogFile.Write("[agent] /clean 需要管理员权限");
                return 2;
            }
            ulong mask = CleanupCore.MaskDefault;
            if (cleanIndex + 1 < args.Length && ulong.TryParse(args[cleanIndex + 1], out var parsed))
            {
                mask = parsed;
            }
            var result = CleanupCore.Clean(mask);
            LogFile.Write($"[agent] 命令行清理完成 mask=0x{mask:X} 释放 {result.FreedBytes / 1048576.0:0} MB");
            return 0;
        }

        using var guard = new SingleInstanceGuard("ToolDock.Agent.SingleInstance");
        if (!guard.Acquired)
        {
            return 0; // 已有 Agent 在跑
        }

        // settings.json 的唯一写者是 Agent
        var settings = AppSettings.Load();

        // 开机自启注册表与设置对齐（用户手动删了启动项也会在下一次启动时补回）
        if (Environment.ProcessPath is { } exePath)
        {
            AutoStartManager.Apply(settings.AutoStart, exePath);
        }

        _tray = new TrayIconController();
        _tray.OpenRequested += LaunchApp;
        _tray.ExitRequested += () => _tray.RequestExit();
        _tray.Initialize();

        var plugins = new PluginHost();
        var memclean = new MemoryCleanPlugin();
        plugins.Register(memclean);
        plugins.StartAll(_tray);

        var pipeServer = new PipeServer(settings, Environment.ProcessPath ?? AppContext.BaseDirectory, _tray, memclean);
        var pipeTask = pipeServer.RunAsync(LifetimeCts.Token);

        LogFile.Write("[agent] 启动完成");
        MemoryTrim.TrimCurrent(); // 启动即回收一次工作集
        _tray.RunMessageLoop(); // 阻塞主线程，直到托盘菜单"退出"

        pipeServer.NotifyShutdown();
        LifetimeCts.Cancel();
        try
        {
            pipeTask.Wait(2000);
        }
        catch
        {
            // 取消或超时即收尾
        }
        plugins.ShutdownAll();
        _tray.Dispose();
        LogFile.Write("[agent] 已退出");
        return 0;
    }

    /// <summary>按需拉起 WPF 界面（App 关窗即退，所以每次点托盘都可能是冷启动）。</summary>
    private static void LaunchApp()
    {
        try
        {
            // 发布布局：publish/ToolDock.Agent.exe + publish/app/ToolDock.exe
            // 开发布局：从 Agent 的 bin 目录回溯到 src 找 App 的编译产物
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "app", "ToolDock.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                    "ToolDock.App", "bin", "Release", "net8.0-windows10.0.19041.0", "ToolDock.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                    "ToolDock.App", "bin", "Debug", "net8.0-windows10.0.19041.0", "ToolDock.exe"),
            ];
            var exePath = candidates.FirstOrDefault(File.Exists);
            if (exePath is null)
            {
                _tray?.ShowBalloon("工具坞", "找不到界面程序 ToolDock.exe");
                LogFile.Write("[agent] 启动界面失败：未找到 ToolDock.exe");
                return;
            }
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            LogFile.Write($"[agent] 启动界面失败：{exception.Message}");
        }
    }
}

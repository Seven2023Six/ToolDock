using System.IO;
using System.Windows;
using ToolDock.App.Services;
using ToolDock.App.Views;

namespace ToolDock.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                // 外壳要命比插件要紧：任何未处理异常先保住进程
                StartupLog("app: DispatcherUnhandledException " + args.Exception);
                MessageBox.Show($"工具坞遇到未处理的错误：\n{args.Exception.Message}", "工具坞 ToolDock",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            StartupLog("app: 启动开始");

            // 全局状态（主题等）由常驻 Agent 拥有；Agent 不在（开发场景）则以独立模式跑
            var link = AgentLink.Instance;
            link.Connect(600);
            StartupLog($"app: 管道握手完成 Connected={link.Connected} Duplicate={link.DuplicateRequested} Theme={link.WelcomeTheme}");
            if (link.DuplicateRequested)
            {
                Shutdown(); // 已有界面窗口，Agent 已让它置前
                return;
            }
            var theme = link.Connected && link.WelcomeTheme is not null
                ? link.WelcomeTheme
                : Core.AppSettings.Load().Theme;

            // WPF Fluent 主题（net10 内置）：Light / Dark / System 三态，切换与系统联动都由框架完成
            ApplyThemeMode(theme);
            StartupLog($"app: 主题 {theme} 就绪");

            var mainWindow = new MainWindow();
            StartupLog("app: 主窗构造完成");
            MainWindow = mainWindow;
            mainWindow.Show();
            StartupLog("app: 主窗已显示");
        }
        catch (Exception exception)
        {
            StartupLog("app: 启动失败 " + exception);
            throw;
        }
    }

    /// <summary>应用 ThemeMode（容忍未知值，回退 Light）。</summary>
    public static void ApplyThemeMode(string theme)
    {
        Current.ThemeMode = theme switch
        {
            "Dark" => ThemeMode.Dark,
            "System" => ThemeMode.System,
            _ => ThemeMode.Light,
        };
    }

    private static void StartupLog(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ToolDock", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响运行
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AgentLink.Instance.Dispose();
        base.OnExit(e);
    }
}

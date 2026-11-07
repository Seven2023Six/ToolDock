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
                // 外壳要命比插件要紧：任何未处理异常先保住进程，M3 接日志后细化
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
            ThemeService.Initialize(theme);
            var backdrop = link.Connected
                ? AgentLink.Instance.CurrentBackdrop
                : Core.AppSettings.Load().Backdrop;
            BackdropService.Initialize(backdrop);
            StartupLog($"app: 主题 {theme} / 材质 {backdrop} 就绪");

            // 电量监听与任务栏贴片随界面进程启动（App 关窗即退，不常驻）；失败不影响主界面
            try
            {
                Services.BatteryMonitor.Instance.Start();
            }
            catch (Exception exception)
            {
                StartupLog("app: 电量监听启动失败 " + exception.Message);
            }

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
        // 电量监听（BLE/经典蓝牙/HID 轮询）随即关闭，不留在后台占资源
        Services.BatteryMonitor.Instance.Dispose();
        AgentLink.Instance.Dispose();
        base.OnExit(e);
    }
}

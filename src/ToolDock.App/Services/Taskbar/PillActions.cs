using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ToolDock.App.Services;

/// <summary>贴片交互适配：双击打开工具坞、右键弹出菜单（移植 VigilWatt 的 TrayService/AppPopupMenu 调用点）。</summary>
internal static class PillActions
{
    public static void ShowMainWindow()
    {
        var window = Application.Current?.MainWindow;
        if (window is null)
        {
            return;
        }
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Show();
        window.Activate();
    }

    public static void ShowMenu(nint owner, int x, int y)
    {
        var menu = new ContextMenu { Placement = PlacementMode.AbsolutePoint, PlacementRectangle = new Rect(x, y, 0, 0) };

        var open = new MenuItem { Header = "打开工具坞" };
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);

        var battery = new MenuItem { Header = "外设电量显示" };
        battery.Click += (_, _) =>
        {
            ShowMainWindow();
            (Application.Current?.MainWindow as MainWindow)?.ShowPage("battery");
        };
        menu.Items.Add(battery);

        menu.Items.Add(new Separator());

        var close = new MenuItem { Header = "关闭任务栏贴片" };
        close.Click += (_, _) =>
        {
            var monitor = BatteryMonitor.Instance;
            if (monitor.Settings is { } settings)
            {
                settings.TaskbarBatteryEnabled = false;
                monitor.SaveSettings();
                monitor.Pill.Refresh();
            }
        };
        menu.Items.Add(close);

        menu.IsOpen = true;
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ToolDock.App.Views;

/// <summary>
/// 实用工具页：随包内置的第三方程序。点击卡片直接以所在目录为工作目录启动，
/// 程序自身的 manifest 决定是否需要 UAC（卸载工具为 requireAdministrator）。
/// </summary>
public partial class UtilityToolsPage : UserControl
{
    public UtilityToolsPage()
    {
        InitializeComponent();
    }

    private void UninstallTool_Click(object sender, RoutedEventArgs e) => LaunchUninstallTool();

    private static void LaunchUninstallTool()
    {
        string? exe = FindTool("tools/uninstalltool/UninstallTool.exe",
            "assets/tools/uninstalltool/UninstallTool.exe");
        if (exe is null)
        {
            MessageBox.Show("找不到内置的卸载工具，请重新安装或重新发布工具箱。", "工具坞 ToolDock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show($"启动卸载工具失败：{exception.Message}", "工具坞 ToolDock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>发布布局 tools\...；开发布局回退到仓库 assets\tools\...。</summary>
    private static string? FindTool(params string[] relativeCandidates)
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (string relative in relativeCandidates)
        {
            string path = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }
}

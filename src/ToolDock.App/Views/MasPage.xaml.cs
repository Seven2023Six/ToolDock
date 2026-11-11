using System.Windows;
using System.Windows.Controls;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>
/// MAS 激活工具页：内置脚本开箱即用，平时不驻留任何进程；
/// 「打开激活菜单」是唯一入口，点击后弹原版控制台（一次 UAC），关掉控制台即结束。
/// 检查更新从官方源拉取最新脚本并校验哈希后替换本地副本。
/// </summary>
public partial class MasPage : UserControl
{
    public MasPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshScriptStatus();
            MasService.LaunchDiagnostics += OnLaunchDiagnostics;
        };
        Unloaded += (_, _) => MasService.LaunchDiagnostics -= OnLaunchDiagnostics;
    }

    /// <summary>秒退且菜单未出现（疑似杀软拦截）：后台线程触发，转发到 UI 线程提示。</summary>
    private void OnLaunchDiagnostics(string message)
    {
        Dispatcher.Invoke(() =>
        {
            MessageBox.Show(message, "工具坞 · MAS 激活工具", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void RefreshScriptStatus()
    {
        if (MasService.HasLocalScript)
        {
            string version = MasService.LocalVersion ?? "未知版本";
            ScriptVersionText.Text = $"MAS {version}（内置脚本）";
            string modified = MasService.LocalModified?.ToString("yyyy-MM-dd HH:mm") ?? "—";
            ScriptStatusText.Text = $"脚本就绪 · 更新于 {modified} · 点击「检查更新」可获取官方最新版";
            SummaryText.Text = $"内置 Microsoft Activation Scripts {version} · 原版控制台菜单";
        }
        else
        {
            ScriptVersionText.Text = "脚本缺失";
            ScriptStatusText.Text = "未找到内置脚本，请点击「检查更新」从官方源获取";
            SummaryText.Text = "内置脚本缺失，请先检查更新";
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var (ok, error) = MasService.Launch();
        if (!ok)
        {
            MessageBox.Show(error, "工具坞 · MAS 激活工具", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateButtonText.Text = "检查中…";
        try
        {
            var (ok, message) = await MasService.CheckUpdateAsync();
            MessageBox.Show(message, "工具坞 · MAS 激活工具",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            UpdateButtonText.Text = "检查更新";
            RefreshScriptStatus();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("builtin");
}

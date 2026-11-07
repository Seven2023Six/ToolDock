using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>设置页：主题切换、窗口材质三选、开机自启（真注册表）、日志工具。</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();

        // 初始状态来自 Agent 快照；独立模式读本地设置
        var local = Core.AppSettings.Load();
        AutostartToggle.IsChecked = AgentLink.Instance.Connected
            ? AgentLink.Instance.CurrentAutoStart
            : local.AutoStart;

        UpdateThemeToggleText();
        UpdateBackdropButtons();
        ThemeService.ThemeChanged += OnThemeChanged;
        BackdropService.BackdropChanged += OnBackdropChanged;
    }

    private void OnThemeChanged(string theme)
    {
        // 页面被 MainWindow 缓存常驻，直接在 UI 线程更新文案即可
        Dispatcher.Invoke(UpdateThemeToggleText);
    }

    private void OnBackdropChanged(string mode)
    {
        Dispatcher.Invoke(UpdateBackdropButtons);
    }

    private void UpdateThemeToggleText()
    {
        ThemeToggleText.Text = ThemeService.Current == ThemeService.Dark
            ? "当前：深色，点击切换浅色"
            : "当前：浅色，点击切换深色";
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Toggle();
    }

    private void UpdateBackdropButtons()
    {
        SetBackdropStyle(BackdropMicaBtn, BackdropService.Current == BackdropService.Mica);
        SetBackdropStyle(BackdropAcrylicBtn, BackdropService.Current == BackdropService.Acrylic);
        SetBackdropStyle(BackdropSolidBtn, BackdropService.Current == BackdropService.Solid);
    }

    private static void SetBackdropStyle(Button button, bool selected)
    {
        button.Style = (Style)FindNameResource(selected ? "PrimaryButtonStyle" : "AppButtonStyle");
    }

    private static object FindNameResource(string key)
        => Application.Current.Resources[key]
           ?? throw new InvalidOperationException($"缺少资源 {key}");

    private void BackdropButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            BackdropService.Apply(mode);
        }
    }

    private void AutostartToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = AutostartToggle.IsChecked == true;
        AgentLink.Instance.CurrentAutoStart = enabled; // 乐观更新，UI 不回跳
        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendAutoStart(enabled); // Agent 写/删注册表并落盘
        }
        else
        {
            // 独立模式（开发场景）：只记录设置，Agent 下次启动时同步注册表
            var settings = Core.AppSettings.Load();
            settings.AutoStart = enabled;
            settings.Save();
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ToolDock", "logs");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show($"打开日志目录失败：{exception.Message}", "工具坞 ToolDock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ToolDock", "logs");
            if (!Directory.Exists(dir))
            {
                return;
            }
            foreach (var file in Directory.GetFiles(dir, "*.log"))
            {
                try { File.Delete(file); } catch (IOException) { /* 被占用的日志跳过 */ }
            }
        }
        catch
        {
            // 清理失败不影响功能
        }
    }
}

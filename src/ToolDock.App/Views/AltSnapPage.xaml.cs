using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>
/// AltSnap（窗口 Alt+拖动）的内置工具页：中文界面 + 常用选项直接写 AltSnap.ini。
/// 保存后重启 AltSnap 生效；工具开关跟随工具箱启动/退出。
/// </summary>
public partial class AltSnapPage : UserControl
{
    private readonly DispatcherTimer _statusTimer;
    private bool _syncing;

    public AltSnapPage()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        Loaded += (_, _) =>
        {
            LoadState();
            _statusTimer.Start();
        };
        Unloaded += (_, _) => _statusTimer.Stop();
    }

    private void LoadState()
    {
        AltSnapService.EnsureIni();
        VersionText.Text = string.IsNullOrEmpty(AltSnapService.Version)
            ? "—"
            : "v" + AltSnapService.Version;

        _syncing = true;
        ToolEnabledToggle.IsChecked = UiSettings.Load().IsToolEnabled(AltSnapService.ToolKey);
        _syncing = false;

        SelectAction(LmbBox, AltSnapService.ReadIni("Input", "LMB", "Move"));
        SelectAction(RmbBox, AltSnapService.ReadIni("Input", "RMB", "Resize"));
        SelectAction(MmbBox, AltSnapService.ReadIni("Input", "MMB", "Menu"));

        AeroToggle.IsChecked = AltSnapService.ReadIniInt("General", "Aero", 1) != 0;
        SmartAeroToggle.IsChecked = AltSnapService.ReadIniInt("General", "SmartAero", 1) != 0;
        AutoFocusToggle.IsChecked = AltSnapService.ReadIniInt("General", "AutoFocus", 0) != 0;
        int trans = AltSnapService.ReadIniInt("General", "MoveTrans", 255);
        TransToggle.IsChecked = trans is > 0 and < 255;
        PauseBox.Text = AltSnapService.ReadIni("General", "Pause", "");
        SaveStatus.Text = "";

        bool enabled = ToolEnabledToggle.IsChecked == true;
        if (enabled && !AltSnapService.IsRunning)
        {
            AltSnapService.Start();
        }
        ApplyToolState(enabled);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        bool running = AltSnapService.IsRunning;
        StatusText.Text = running
            ? "运行中" + (string.IsNullOrEmpty(AltSnapService.Version) ? "" : " · v" + AltSnapService.Version)
            : "已停止";
        StatusText.Foreground = running
            ? (System.Windows.Media.Brush)Application.Current.Resources["Accent"]
            : (System.Windows.Media.Brush)Application.Current.Resources["Text.Secondary"];
    }

    private void ApplyToolState(bool enabled)
    {
        if (ToolContent is not null)
        {
            ToolContent.IsEnabled = enabled;
            ToolContent.Opacity = enabled ? 1.0 : 0.45;
        }
        if (SaveButton is not null)
        {
            SaveButton.IsEnabled = enabled;
        }
    }

    private void ToolEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        bool enabled = ToolEnabledToggle.IsChecked == true;
        UiSettings.Load().SetToolEnabled(AltSnapService.ToolKey, enabled);
        if (enabled)
        {
            AltSnapService.Start();
        }
        else
        {
            AltSnapService.Stop();
        }
        ApplyToolState(enabled);
        UpdateStatus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new Dictionary<string, string>
        {
            ["Input/LMB"] = SelectedAction(LmbBox),
            ["Input/RMB"] = SelectedAction(RmbBox),
            ["Input/MMB"] = SelectedAction(MmbBox),
            ["General/Aero"] = AeroToggle.IsChecked == true ? "1" : "0",
            ["General/SmartAero"] = SmartAeroToggle.IsChecked == true ? "1" : "0",
            ["General/AutoFocus"] = AutoFocusToggle.IsChecked == true ? "1" : "0",
            ["General/MoveTrans"] = TransToggle.IsChecked == true ? "200" : "255",
            ["General/Pause"] = PauseBox.Text.Trim(),
            ["General/Language"] = "zh-CN",
        };
        foreach (var (path, value) in defaults)
        {
            var parts = path.Split('/');
            AltSnapService.WriteIni(parts[0], parts[1], value);
        }

        if (AltSnapService.IsRunning)
        {
            AltSnapService.Stop();
            AltSnapService.Start();
        }
        SaveStatus.Text = "已保存并重启 AltSnap";
        UpdateStatus();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var path in new[]
                 {
                     "Input/LMB", "Input/RMB", "Input/MMB",
                     "General/Aero", "General/SmartAero", "General/AutoFocus", "General/MoveTrans",
                     "General/Pause",
                 })
        {
            var parts = path.Split('/');
            string value = AltSnapService.ReadDefault(parts[0], parts[1], "");
            if (value.Length > 0)
            {
                AltSnapService.WriteIni(parts[0], parts[1], value);
            }
        }
        AltSnapService.WriteIni("General", "Language", "zh-CN");
        LoadState();
        if (AltSnapService.IsRunning)
        {
            AltSnapService.Stop();
            AltSnapService.Start();
        }
        SaveStatus.Text = "已恢复默认";
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        if (AltSnapService.IniPath is { } ini && File.Exists(ini))
        {
            Process.Start(new ProcessStartInfo(ini) { UseShellExecute = true });
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("builtin");

    private static void SelectAction(ComboBox box, string action)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, action, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string SelectedAction(ComboBox box)
        => box.SelectedItem is ComboBoxItem { Tag: string action } ? action : "Nothing";
}

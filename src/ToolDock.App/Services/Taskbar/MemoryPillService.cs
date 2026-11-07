using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ToolDock.Core;

namespace ToolDock.App.Services;

/// <summary>界面层自己的小设置（不写 settings.json，那份由常驻 Agent 独占）。</summary>
internal sealed class UiSettings
{
    /// <summary>任务栏内存贴片开关。</summary>
    public bool MemoryPillEnabled { get; set; } = true;

    /// <summary>内置工具逐个开关（键 = 工具页 key；缺省视为开启）。</summary>
    public Dictionary<string, bool> ToolEnabled { get; set; } = new();

    public bool IsToolEnabled(string key) => !ToolEnabled.TryGetValue(key, out var on) || on;

    public void SetToolEnabled(string key, bool enabled)
    {
        ToolEnabled[key] = enabled;
        Save();
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "ui-settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
            }
        }
        catch
        {
        }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}

/// <summary>
/// 任务栏内存贴片的生命周期：启用时把贴片挂到任务栏，点击即触发清理（经 Agent 走既有提权链路）。
/// 与电量贴片并排但留间隔（电量贴片在右、内存贴片在左）。
/// </summary>
internal sealed class MemoryPillService
{
    /// <summary>进程内单例（App 启动时 Start，退出时 Stop）。</summary>
    public static MemoryPillService Instance { get; } = new();

    private MemoryPillWindow? _window;

    /// <summary>由界面注册：打开内存清理页（右键菜单用）。</summary>
    public static Action? OpenMemCleanPage { get; set; }

    public bool IsRunning => _window is not null;

    public void Start()
    {
        if (_window is not null)
        {
            return;
        }
        var settings = UiSettings.Load();
        if (!settings.MemoryPillEnabled || !settings.IsToolEnabled("memclean"))
        {
            return;
        }
        Attach();
    }

    public void Refresh()
    {
        var settings = UiSettings.Load();
        if (!settings.MemoryPillEnabled || !settings.IsToolEnabled("memclean"))
        {
            Stop();
            return;
        }
        if (_window is null)
        {
            Attach();
        }
    }

    /// <summary>工具总开关变化时调用：停用即下线贴片，重新启用则按贴片开关恢复。</summary>
    public void ApplyToolState(bool toolEnabled)
    {
        if (toolEnabled)
        {
            Refresh();
        }
        else
        {
            Stop();
        }
    }

    public void SetEnabled(bool enabled)
    {
        var settings = UiSettings.Load();
        settings.MemoryPillEnabled = enabled;
        settings.Save();
        Refresh();
    }

    public bool IsEnabled => UiSettings.Load().MemoryPillEnabled;

    private void Attach()
    {
        nint taskbar = Native.FindWindowW(Native.TaskbarClassName, null);
        if (taskbar == nint.Zero)
        {
            return;
        }
        try
        {
            _window = new MemoryPillWindow(taskbar, CleanNow, HideFromPill);
        }
        catch
        {
            _window = null;
        }
    }

    public void Stop()
    {
        try { _window?.Dispose(); } catch { }
        _window = null;
    }

    /// <summary>点击贴片：交给 Agent 走既有清理链路（是否弹确认/UAC 由内存清理设置决定）。</summary>
    private static void CleanNow()
    {
        try
        {
            var link = AgentLink.Instance;
            if (link.Connected)
            {
                link.SendCleanNow(confirmed: false);
            }
            else if (CleanupCore.IsElevated())
            {
                CleanupCore.Clean(CleanupCore.MaskDefault);
            }
        }
        catch
        {
        }
    }

    private static void HideFromPill()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Instance.SetEnabled(false);
            MemoryPillEnabledChanged?.Invoke(false);
        });
    }

    /// <summary>贴片被右键「隐藏」时通知界面同步开关状态。</summary>
    public static event Action<bool>? MemoryPillEnabledChanged;

    /// <summary>贴片配色跟随系统主题（浅色/深色）。</summary>
    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value != 0;
            }
        }
        catch
        {
        }
        return true;
    }
}

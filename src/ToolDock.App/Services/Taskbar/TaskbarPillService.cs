namespace ToolDock.App.Services;

using System.IO;

/// <summary>
/// 任务栏贴片的生命周期：只在「启用」时创建协调器（TaskbarPillManager），关闭即释放，不空转定时器。
/// 贴片会真嵌进任务栏（SetParent 到 Shell_TrayWnd），随任务栏移动/改 DPI 自动跟随（由协调器重排）。
/// </summary>
public sealed class TaskbarPillService
{
    private TaskbarPillManager? _manager;

    public bool IsRunning => _manager is not null;

    public void Start()
    {
        if (_manager is not null)
        {
            return;
        }
        if (!PillHost.Ready)
        {
            return;
        }
        if (PillHost.Settings?.TaskbarBatteryEnabled != true || !UiSettings.Load().IsToolEnabled("battery"))
        {
            return;
        }
        try
        {
            _manager = new TaskbarPillManager();
        }
        catch (Exception exception)
        {
            _manager = null;
            Diag("pill start failed: " + exception);
        }
    }

    /// <summary>设置改动后立即重排（启用/关闭/换模式/换屏）。</summary>
    public void Refresh()
    {
        if (PillHost.Settings?.TaskbarBatteryEnabled == true && UiSettings.Load().IsToolEnabled("battery"))
        {
            if (_manager is null)
            {
                Start();
            }
            else
            {
                _manager.RefreshFromSettings();
            }
        }
        else
        {
            Stop();
        }
    }

    /// <summary>工具总开关变化时调用：停用即下线贴片，重新启用则恢复。</summary>
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

    public void ResetPosition() => _manager?.ResetPosition();

    public void Stop()
    {
        try { _manager?.Dispose(); } catch { }
        _manager = null;
    }

    private static void Diag(string line)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"pill-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

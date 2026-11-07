using System.IO;
using System.Text.Json;
using VigilWatt.Core.Devices;
using VigilWatt.Core.Models;
using VigilWatt.Core.Settings;
using VigilWatt.Core.Storage;

namespace ToolDock.App.Services;

/// <summary>
/// 外设电量数据源：复用 VigilWatt.Core（MIT）的 DeviceManager——
/// BLE GATT 电池服务、经典蓝牙 HFP、以及 2.4G HID 多品牌插件都由它负责。
/// 界面进程短命（关窗即退），所以监听只在 App 存活期间运行，退出时 Dispose。
/// 历史库落在 ToolDock 自己的目录，不污染 VigilWatt 的数据。
/// </summary>
public sealed class BatteryMonitor : IDisposable
{
    public static BatteryMonitor Instance { get; } = new();

    private readonly object _gate = new();
    private DeviceManager? _manager;
    private AppSettings? _settings;
    private HistoryStore? _history;
    private string _lastError = "";

    /// <summary>启动失败时的原因（界面用于提示），成功为空。</summary>
    public string LastError
    {
        get { lock (_gate) { return _lastError; } }
    }

    public bool Started
    {
        get { lock (_gate) { return _manager is not null; } }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_manager is not null)
            {
                return;
            }
            try
            {
                // 设置存在 ToolDock 自己的目录，不写 VigilWatt 的配置文件
                var settings = LoadSettingsFile() ?? new AppSettings
                {
                    // 「外设电量显示」只管外设：本机电池默认不监控（VigilWatt 里同样是可选开关）
                    MonitorSystemBattery = false,
                };

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock");
                Directory.CreateDirectory(dir);
                _history = new HistoryStore(Path.Combine(dir, "battery-history.db"));

                _settings = settings;
                _manager = new DeviceManager(settings, _history);
                _manager.LowBatteryDetected += OnLowBattery;
                _manager.DevicesChanged += PushBatteryStatus;
                _manager.DeviceUpdated += _ => PushBatteryStatus();
                _manager.Start();
                _ = _manager.RequestPollAsync();
                Pill.Start();
                _lastError = "";
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                try { _manager?.Dispose(); } catch { }
                try { _history?.Dispose(); } catch { }
                _manager = null;
                _history = null;
            }
        }
    }

    /// <summary>当前设备快照（已按在线/名称排序、显示去重）。</summary>
    public IReadOnlyCollection<DeviceStatus> Devices
    {
        get
        {
            lock (_gate)
            {
                return _manager?.Devices ?? Array.Empty<DeviceStatus>();
            }
        }
    }

    /// <summary>底层 DeviceManager（贴片等消费方直接读设备/剩余时间）。</summary>
    public DeviceManager? Manager
    {
        get { lock (_gate) { return _manager; } }
    }

    /// <summary>任务栏贴片（启用时才有实例）。</summary>
    public TaskbarPillService Pill { get; } = new();

    /// <summary>底层设置对象（设置页读写低电量阈值/采样间隔/插件开关等）。</summary>
    public AppSettings? Settings
    {
        get { lock (_gate) { return _settings; } }
    }

    /// <summary>已发现的 2.4G 品牌插件（设置页动态生成开关）。</summary>
    public IReadOnlyList<VigilWatt.Core.Plugins.IBatteryPlugin> Plugins
    {
        get { lock (_gate) { return _manager?.Plugins ?? Array.Empty<VigilWatt.Core.Plugins.IBatteryPlugin>(); } }
    }

    /// <summary>设置改动后让 DeviceManager 立即生效（插件开关/本机电量/定时器）。</summary>
    public void ApplySettings()
    {
        DeviceManager? manager;
        lock (_gate) { manager = _manager; }
        manager?.ApplySettings();
        Pill.Refresh();
    }

    /// <summary>把设置写回磁盘（%APPDATA%\ToolDockattery-settings.json）。</summary>
    public bool SaveSettings()
    {
        AppSettings? settings;
        lock (_gate) { settings = _settings; }
        if (settings is null)
        {
            return false;
        }
        try
        {
            string path = SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, SerializerOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "battery-settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static AppSettings? LoadSettingsFile()
    {
        try
        {
            string path = SettingsFilePath;
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    // ---- 历史（统计页 / 历史页共用）----

    public List<DeviceHistoryStat> GetDeviceHistoryStats()
    {
        HistoryStore? history;
        lock (_gate) { history = _history; }
        try { return history?.GetDeviceStats() ?? new List<DeviceHistoryStat>(); }
        catch { return new List<DeviceHistoryStat>(); }
    }

    public HistoryStats? GetHistoryStats()
    {
        HistoryStore? history;
        lock (_gate) { history = _history; }
        try { return history?.GetStats(); }
        catch { return null; }
    }

    public List<HistorySample> QueryHistory(string deviceId, DateTimeOffset from, DateTimeOffset to)
    {
        HistoryStore? history;
        lock (_gate) { history = _history; }
        try { return history?.Query(deviceId, from, to) ?? new List<HistorySample>(); }
        catch { return new List<HistorySample>(); }
    }

    /// <summary>清理早于 cutoff 的历史记录，返回删除条数。</summary>
    public int CleanupHistory(DateTimeOffset cutoff)
    {
        HistoryStore? history;
        lock (_gate) { history = _history; }
        try { return history?.CleanupOlderThan(cutoff) ?? 0; }
        catch { return 0; }
    }

    /// <summary>删除若干设备的历史记录，返回删除条数。</summary>
    public int DeleteHistory(IReadOnlyCollection<string> deviceIds)
    {
        HistoryStore? history;
        lock (_gate) { history = _history; }
        try { return history?.DeleteDevices(deviceIds) ?? 0; }
        catch { return 0; }
    }

    /// <summary>是否把本机电池也列进来（VigilWatt 里本机电池是可选监控项）。</summary>
    public bool SystemBatteryEnabled
    {
        get { lock (_gate) { return _settings?.MonitorSystemBattery == true; } }
    }

    /// <summary>切换本机电量监控：改设置后 ApplySettings 会立刻增删该条目。</summary>
    public void SetSystemBatteryEnabled(bool enabled)
    {
        DeviceManager? manager;
        lock (_gate)
        {
            manager = _manager;
            if (_settings is not null)
            {
                _settings.MonitorSystemBattery = enabled;
            }
        }
        manager?.ApplySettings();
    }

    private string? _lastStatusText;

    /// <summary>低电量提醒：经 Agent 弹托盘气泡（阈值由 DeviceManager 按设置判定）。</summary>
    private void OnLowBattery(DeviceStatus device)
    {
        try
        {
            AgentLink.Instance.SendNotify("电量提醒", $"{device.DisplayName} 电量 {device.BatteryPercent}%，请及时充电");
        }
        catch
        {
            // 提醒失败不影响监测
        }
    }

    /// <summary>把在线设备里最低的电量同步到托盘提示（文本变化才发，避免刷管道）。</summary>
    private void PushBatteryStatus()
    {
        DeviceManager? manager;
        lock (_gate) { manager = _manager; }
        if (manager is null)
        {
            return;
        }

        DeviceStatus? lowest = null;
        foreach (var device in manager.Devices)
        {
            if (!device.IsOnline || device.BatteryPercent is null)
            {
                continue;
            }
            if (lowest is null || device.BatteryPercent < lowest.BatteryPercent)
            {
                lowest = device;
            }
        }

        string? text = lowest is null ? null : $"电量 {lowest.BatteryPercent}%（{lowest.DisplayName}）";
        if (text == _lastStatusText)
        {
            return;
        }
        _lastStatusText = text;
        try
        {
            AgentLink.Instance.SendBatteryStatus(text);
        }
        catch
        {
        }
    }

    /// <summary>剩余可用时间估算（供列表细节行显示；算不出时 IsAvailable=false）。</summary>
    public RemainingTimeEstimateResult? GetRemainingTime(DeviceStatus device)
    {
        DeviceManager? manager;
        lock (_gate) { manager = _manager; }
        try
        {
            return manager?.GetRemainingTimeEstimate(device);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>手动触发一轮立即轮询（刷新按钮）。</summary>
    public Task RefreshAsync()
    {
        DeviceManager? manager;
        lock (_gate) { manager = _manager; }
        return manager?.RequestPollAsync() ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        try { Pill.Stop(); } catch { }
        try
        {
            if (_manager is not null)
            {
                _manager.LowBatteryDetected -= OnLowBattery;
                _manager.DevicesChanged -= PushBatteryStatus;
            }
        }
        catch
        {
        }
        lock (_gate)
        {
            try { _manager?.Dispose(); } catch { }
            try { _history?.Dispose(); } catch { }
            _manager = null;
            _history = null;
        }
    }
}

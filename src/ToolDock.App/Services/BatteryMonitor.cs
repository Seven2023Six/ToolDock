using System.IO;
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
                var settings = new AppSettings
                {
                    // 「外设电量显示」只管外设：本机电池不加进来（VigilWatt 里同样是可选开关）
                    MonitorSystemBattery = false,
                };

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock");
                Directory.CreateDirectory(dir);
                _history = new HistoryStore(Path.Combine(dir, "battery-history.db"));

                _settings = settings;
                _manager = new DeviceManager(settings, _history);
                _manager.Start();
                _ = _manager.RequestPollAsync();
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
        lock (_gate)
        {
            try { _manager?.Dispose(); } catch { }
            try { _history?.Dispose(); } catch { }
            _manager = null;
            _history = null;
        }
    }
}

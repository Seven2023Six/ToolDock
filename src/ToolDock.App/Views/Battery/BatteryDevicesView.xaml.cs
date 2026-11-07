using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ToolDock.App.Services;
using VigilWatt.Core.Devices;
using VigilWatt.Core.Models;

namespace ToolDock.App.Views;

/// <summary>设备子页：VigilWatt 设备列表（图标 / 名称 / 来源徽标 / 电量条 / 大号百分比）。</summary>
public partial class BatteryDevicesView : UserControl
{
    private readonly ObservableCollection<BatteryRow> _rows = new();
    private readonly DispatcherTimer _timer;
    private string _signature = "";
    private bool _syncing;

    public BatteryDevicesView()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _rows;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Update();

        Loaded += (_, _) =>
        {
            BatteryMonitor.Instance.Start();
            _syncing = true;
            SystemBatteryToggle.IsChecked = BatteryMonitor.Instance.SystemBatteryEnabled;
            _syncing = false;
            Update();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Update()
    {
        var monitor = BatteryMonitor.Instance;
        var devices = monitor.Devices;

        var rows = new List<BatteryRow>(devices.Count);
        foreach (var device in devices)
        {
            rows.Add(BatteryRow.From(device, monitor));
        }

        string signature = string.Join("|", rows.Select(r => r.Signature));
        if (signature != _signature)
        {
            _signature = signature;
            _rows.Clear();
            foreach (var row in rows)
            {
                _rows.Add(row);
            }
        }

        bool empty = rows.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrEmpty(monitor.LastError))
        {
            SummaryText.Text = "电量读取不可用";
            EmptyHint.Text = "初始化失败：" + monitor.LastError;
        }
        else if (empty)
        {
            SummaryText.Text = "未发现设备";
            EmptyHint.Text = "连接蓝牙设备或插入 2.4G 接收器，然后点击刷新";
        }
        else
        {
            var percents = rows.Where(r => r.HasPercent).Select(r => r.Percent).ToList();
            SummaryText.Text = percents.Count == 0
                ? $"{rows.Count} 台设备"
                : $"{rows.Count} 台设备 · 最低电量 {percents.Min()}%";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshText.Text = "刷新中…";
        try
        {
            await BatteryMonitor.Instance.RefreshAsync();
            Update();
        }
        catch
        {
        }
        finally
        {
            RefreshText.Text = "刷新";
            RefreshButton.IsEnabled = true;
        }
    }

    private void SystemBattery_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        BatteryMonitor.Instance.SetSystemBatteryEnabled(SystemBatteryToggle.IsChecked == true);
        BatteryMonitor.Instance.SaveSettings();
        _ = Task.Run(async () =>
        {
            try
            {
                await BatteryMonitor.Instance.RefreshAsync();
            }
            catch
            {
            }
            Dispatcher.Invoke(Update);
        });
    }
}

/// <summary>一行设备（一次性绑定，刷新时整行重建）。</summary>
public sealed class BatteryRow
{
    private static readonly Color Charging = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Color Low = Color.FromRgb(0xE2, 0x44, 0x5C);
    private static readonly Color Mid = Color.FromRgb(0xFA, 0xAA, 0x17);
    private static readonly Color Good = Color.FromRgb(0x34, 0xC7, 0x59);
    private static readonly Color Offline = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly Color SourceBluetooth = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Color Source24G = Color.FromRgb(0xAF, 0x52, 0xDE);
    private static readonly Color SourceSystem = Color.FromRgb(0x30, 0xB0, 0xC7);

    public string Signature { get; init; } = "";
    public string IconGlyph { get; init; } = "\uE7C3";
    public string Name { get; init; } = "";
    public string SourceText { get; init; } = "";
    public string PercentText { get; init; } = "----";
    public int Percent { get; init; }
    public bool HasPercent { get; init; }
    public double Progress { get; init; }
    public string SubText { get; init; } = "";
    public Visibility ChargingVisibility { get; init; } = Visibility.Collapsed;
    public Brush SourceBrush { get; init; } = Frozen(Offline);
    public Brush SourceBackgroundBrush { get; init; } = Frozen(Tint(Offline));
    public Brush ProgressBrush { get; init; } = Frozen(Offline);
    public Brush PercentBrush { get; init; } = Frozen(Offline);

    public static BatteryRow From(DeviceStatus device, BatteryMonitor monitor)
    {
        bool charging = DeviceStatusFormatter.IsCharging(device.Charging);
        string state = DeviceStatusFormatter.ChargeSuffix(device.Charging);
        if (!device.IsOnline)
        {
            state = " · 离线";
        }

        string percentText = DeviceStatusFormatter.PercentText(device.BatteryPercent, "----");
        string sourceText = DeviceSourceInfo.ShortLabel(device.Source);

        string remaining = "";
        try
        {
            if (monitor.GetRemainingTime(device) is { IsAvailable: true, Remaining: { } span })
            {
                remaining = $" · 约{RemainingTimeEstimator.Format(span)}剩余";
            }
        }
        catch
        {
        }

        string subText = $"{device.Protocol}{state}{remaining} · 更新于 {device.LastUpdated:HH:mm:ss}";

        Color progressColor = !device.IsOnline
            ? Offline
            : charging ? Charging
            : device.BatteryPercent is <= 20 ? Low
            : device.BatteryPercent is <= 50 ? Mid
            : Good;

        Color sourceColor = device.Source switch
        {
            DeviceSource.Ble or DeviceSource.BluetoothClassic => SourceBluetooth,
            DeviceSource.System => SourceSystem,
            _ => Source24G,
        };

        Brush percentBrush = !device.IsOnline ? Frozen(Offline)
            : charging ? Frozen(Charging)
            : device.BatteryPercent is <= 20 ? Frozen(Low)
            : (Brush)Application.Current.Resources["Text.Primary"];

        string signature = string.Join('|',
            device.Id, device.DisplayName, (int)device.Kind, percentText,
            subText, sourceText, (int)device.Source, charging, device.IsOnline);

        return new BatteryRow
        {
            Signature = signature,
            IconGlyph = Glyph(device.Kind),
            Name = device.DisplayName,
            SourceText = sourceText,
            PercentText = percentText,
            Percent = device.BatteryPercent ?? 0,
            HasPercent = device.BatteryPercent is not null,
            Progress = device.BatteryPercent ?? 0,
            SubText = subText,
            ChargingVisibility = charging ? Visibility.Visible : Visibility.Collapsed,
            SourceBrush = Frozen(sourceColor),
            SourceBackgroundBrush = Frozen(Tint(sourceColor)),
            ProgressBrush = Frozen(progressColor),
            PercentBrush = percentBrush,
        };
    }

    private static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "\uE962",
        DeviceKind.Keyboard => "\uE765",
        DeviceKind.Headset => "\uE7F5",
        DeviceKind.Speaker => "\uE7F6",
        DeviceKind.Gamepad => "\uE7FC",
        DeviceKind.Computer => "\uE7F4",
        DeviceKind.Phone => "\uE8EA",
        DeviceKind.Tablet => "\uE70A",
        DeviceKind.Watch => "\uE916",
        _ => "\uE7C3",
    };

    private static Color Tint(Color color, byte alpha = 0x28)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

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

        // 隐藏设备入口：有忽略/屏蔽记录时才显示
        var managerForHidden = monitor.Manager;
        int ignored = managerForHidden?.IgnoredDevices.Count ?? 0;
        int blocked = managerForHidden?.BlockedDevices.Count ?? 0;
        HiddenBar.Visibility = ignored + blocked > 0 ? Visibility.Visible : Visibility.Collapsed;
        IgnoredButton.Visibility = ignored > 0 ? Visibility.Visible : Visibility.Collapsed;
        BlockedButton.Visibility = blocked > 0 ? Visibility.Visible : Visibility.Collapsed;
        IgnoredButton.Content = $"已忽略 {ignored} 台";
        BlockedButton.Content = $"已屏蔽 {blocked} 台";

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

    /// <summary>设备卡右侧的「外观」按钮（右键菜单之外的显式入口）。</summary>
    private void AppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string deviceId })
        {
            OpenAppearance(deviceId);
        }
    }

    /// <summary>设备外观：重命名 + 自定义图标（移植 VigilWatt 同名对话框）。</summary>
    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: string deviceId })
        {
            OpenAppearance(deviceId);
        }
    }

    private void OpenAppearance(string deviceId)
    {
        var monitor = BatteryMonitor.Instance;
        var settings = monitor.Settings;
        if (settings is null)
        {
            return;
        }
        var device = monitor.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device is null)
        {
            return;
        }

        var applied = DevicePresentation.Apply(device, settings);
        var automaticKind = device.Kind == DeviceKind.Unknown
            ? DeviceKindGuesser.Guess(device.DisplayName)
            : device.Kind;
        var currentKind = settings.DeviceCustomizations.TryGetValue(deviceId, out var existing) && existing.IconKind.HasValue
            ? existing.IconKind.Value
            : automaticKind;

        var dialog = new DeviceAppearanceDialog(deviceId, applied.DisplayName, currentKind, automaticKind)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (dialog.ResetRequested)
        {
            settings.DeviceCustomizations.Remove(deviceId);
        }
        else if (dialog.Result is { } custom)
        {
            settings.DeviceCustomizations[deviceId] = custom;
        }

        monitor.SaveSettings();
        monitor.ApplySettings();
        _signature = "";   // 强制重建行，立刻反映新名字/新图标
        Update();
    }

    /// <summary>忽略设备：从列表隐藏，可在底部「已忽略」里恢复。</summary>
    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: string deviceId })
        {
            return;
        }
        BatteryMonitor.Instance.Manager?.IgnoreDevice(deviceId);
        BatteryMonitor.Instance.SaveSettings();
        _signature = "";
        Update();
    }

    /// <summary>移除离线设备（在线设备会立刻被插件重新报回，故只对离线生效）。</summary>
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: string deviceId })
        {
            return;
        }
        BatteryMonitor.Instance.Manager?.RemoveOfflineDevice(deviceId, block: false);
        BatteryMonitor.Instance.SaveSettings();
        _signature = "";
        Update();
    }

    private void Ignored_Click(object sender, RoutedEventArgs e)
    {
        var manager = BatteryMonitor.Instance.Manager;
        if (manager is null)
        {
            return;
        }
        var menu = new ContextMenu { PlacementTarget = IgnoredButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var (id, info) in manager.IgnoredDevices)
        {
            var name = string.IsNullOrEmpty(info.Name) ? id : info.Name;
            var item = new MenuItem { Header = $"恢复「{name}」" };
            item.Click += (_, _) =>
            {
                manager.UnignoreDevice(id);
                BatteryMonitor.Instance.SaveSettings();
                _signature = "";
                Update();
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            return;
        }
        menu.IsOpen = true;
    }

    private void Blocked_Click(object sender, RoutedEventArgs e)
    {
        var manager = BatteryMonitor.Instance.Manager;
        if (manager is null)
        {
            return;
        }
        var menu = new ContextMenu { PlacementTarget = BlockedButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var (id, info) in manager.BlockedDevices)
        {
            var name = string.IsNullOrEmpty(info.Name) ? id : info.Name;
            var item = new MenuItem { Header = $"解除屏蔽「{name}」" };
            item.Click += (_, _) =>
            {
                manager.UnblockDevice(id);
                BatteryMonitor.Instance.SaveSettings();
                _signature = "";
                Update();
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            return;
        }
        menu.IsOpen = true;
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

    public string DeviceId { get; init; } = "";
    public DeviceKind Kind { get; init; } = DeviceKind.Unknown;
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

    public static BatteryRow From(DeviceStatus raw, BatteryMonitor monitor)
    {
        // 套用用户自定义（重命名 / 自定义图标）；没有设置时原样展示
        var settings = monitor.Settings;
        var device = settings is null
            ? raw
            : DevicePresentation.WithIconKind(DevicePresentation.Apply(raw, settings), settings);

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
            DeviceId = device.Id,
            Kind = device.Kind,
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

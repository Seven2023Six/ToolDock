using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ToolDock.App.Services;
using VigilWatt.Core.Models;

namespace ToolDock.App.Views;

/// <summary>
/// 首页 · Widget 墙：内存与设备电量都实时刷新。
/// 电量卡片把「最低电量设备」做成主数字，其余在线设备一行一台，整卡点击进入电量页。
/// </summary>
public partial class HomePage : UserControl
{
    /// <summary>除主数字外最多列出的设备数（卡片高度保持与内存卡接近）。</summary>
    private const int MaxRows = 3;

    private static readonly Brush ChargingBrush = Frozen(0x0A, 0x84, 0xFF);
    private static readonly Brush LowBrush = Frozen(0xE2, 0x44, 0x5C);
    private static readonly Brush MidBrush = Frozen(0xFA, 0xAA, 0x17);

    private readonly DispatcherTimer _timer;
    private readonly List<HomeDeviceRow> _deviceRows = new();
    private string _batterySignature = "";

    public HomePage()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        RefreshMemory();
        RefreshBattery();
    }

    private void RefreshMemory()
    {
        if (SystemMemory.Read() is not { } memory)
        {
            return;
        }
        MemUsageText.Text = memory.LoadPercent.ToString();
        // 两列都用占比星号：used : free = percent : (100 - percent)，比例才正确
        MemUsedColumn.Width = new GridLength(memory.LoadPercent, GridUnitType.Star);
        MemFreeColumn.Width = new GridLength(100 - memory.LoadPercent, GridUnitType.Star);
        MemFootnoteText.Text = $"已用 {memory.UsedGiB:0.0} / {memory.TotalGiB:0.0} GB";
    }

    private void RefreshBattery()
    {
        var monitor = BatteryMonitor.Instance;
        if (!monitor.Started)
        {
            monitor.Start();
        }

        var settings = monitor.Settings;
        var online = new List<DeviceStatus>();
        foreach (var raw in monitor.Devices)
        {
            var device = settings is null
                ? raw
                : DevicePresentation.WithIconKind(DevicePresentation.Apply(raw, settings), settings);
            if (device.IsOnline)
            {
                online.Add(device);
            }
        }

        // 主数字取电量最低的设备——最需要关注的那台
        var hero = online
            .Where(d => d.BatteryPercent.HasValue)
            .OrderBy(d => d.BatteryPercent)
            .FirstOrDefault();

        string signature = string.Join("|", online.Select(d =>
            $"{d.Id}:{d.BatteryPercent}:{(int)d.Charging}:{(int)d.Kind}:{d.DisplayName}"));
        if (signature != _batterySignature)
        {
            _batterySignature = signature;
            _deviceRows.Clear();
            foreach (var device in online)
            {
                if (hero is not null && device.Id == hero.Id)
                {
                    continue;
                }
                if (_deviceRows.Count >= MaxRows)
                {
                    break;
                }
                _deviceRows.Add(HomeDeviceRow.From(device, BrushFor(device)));
            }
            BatteryDeviceList.ItemsSource = null;
            BatteryDeviceList.ItemsSource = _deviceRows;
        }

        if (hero is null)
        {
            BatteryLowText.Text = "—";
            BatteryLowSuffix.Visibility = Visibility.Collapsed;
            BatteryLowText.Foreground = (Brush)Application.Current.Resources["Text.Primary"];
            BatteryLowName.Text = online.Count == 0 ? "暂无在线设备" : "电量未知";
        }
        else
        {
            BatteryLowText.Text = hero.BatteryPercent!.Value.ToString();
            BatteryLowSuffix.Visibility = Visibility.Visible;
            var brush = BrushFor(hero);
            BatteryLowText.Foreground = brush;
            BatteryLowSuffix.Foreground = brush;
            BatteryLowName.Text = hero.DisplayName + DeviceStatusFormatter.ChargeSuffix(hero.Charging);
        }

        BatteryHint.Visibility = online.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BatteryCard_Click(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowPage("battery");

    private void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        var link = AgentLink.Instance;
        if (link.Connected)
        {
            // confirmed=false：由 Agent 按「清理前确认」设置决定是否弹确认框
            link.SendCleanNow(confirmed: false);
        }
        else
        {
            if (Core.CleanupCore.IsElevated())
            {
                var result = Core.CleanupCore.Clean(Core.CleanupCore.MaskDefault);
                MemFootnoteText.Text = $"已释放 {result.FreedBytes / 1073741824.0:0.00} GB";
            }
            else
            {
                MemFootnoteText.Text = "独立模式无管理员权限，无法清理";
            }
        }
    }

    private static Brush BrushFor(DeviceStatus device)
    {
        if (DeviceStatusFormatter.IsCharging(device.Charging))
        {
            return ChargingBrush;
        }
        return device.BatteryPercent switch
        {
            <= 20 => LowBrush,
            <= 50 => MidBrush,
            _ => (Brush)Application.Current.Resources["Text.Primary"],
        };
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>首页电量卡片里的一行设备。</summary>
public sealed class HomeDeviceRow
{
    public string Glyph { get; init; } = "\uE7C3";
    public string Name { get; init; } = "";
    public string PercentText { get; init; } = "--";
    public Brush Brush { get; init; } = Brushes.Gray;

    public static HomeDeviceRow From(DeviceStatus device, Brush brush) => new()
    {
        Glyph = GlyphFor(device.Kind),
        Name = device.DisplayName,
        PercentText = device.BatteryPercent is { } percent
            ? $"{percent}%{DeviceStatusFormatter.ChargeSuffix(device.Charging)}"
            : "--",
        Brush = brush,
    };

    private static string GlyphFor(DeviceKind kind) => kind switch
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
}

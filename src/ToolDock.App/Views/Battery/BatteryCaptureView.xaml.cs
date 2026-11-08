using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VigilWatt.Core.Capture;
using VigilWatt.Core.Capture.Hid;

namespace ToolDock.App.Views;

/// <summary>
/// 设备诊断/采证（移植 VigilWatt 同名页面）：
/// 选任务 → 选设备接口 → 探测能力 → 按步骤操作设备并采集 → 停止导出离线包。
/// 采集引擎来自 VigilWatt.Core（只读句柄，不向设备写报文）。
/// </summary>
public partial class BatteryCaptureView : UserControl
{
    private readonly ObservableCollection<CaptureDeviceRow> _devices = new();
    private readonly ObservableCollection<CapabilityRow> _capabilities = new();
    private readonly ObservableCollection<GuideStepRow> _steps = new();
    private readonly DispatcherTimer _timer;

    private CaptureDeviceInfo? _selected;
    private CaptureSession? _session;
    private CaptureGuide _guide = CaptureGuide.Default();
    private string? _lastPackageDirectory;

    public BatteryCaptureView()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        CapabilityList.ItemsSource = _capabilities;
        StepList.ItemsSource = _steps;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateCaptureStatus();

        Loaded += (_, _) =>
        {
            if (_devices.Count == 0)
            {
                LoadDevices();
            }
            RenderSteps();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            // 离开页面时结束采集：会话线程/句柄不能留在后台空转
            if (_session is not null)
            {
                try { _session.Dispose(); } catch { }
                _session = null;
                StartButton.IsEnabled = true;
                NextStepButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                DeviceList.IsEnabled = true;
                ProbeButton.IsEnabled = _selected is not null;
                CaptureStatus.Text = "采集已停止（离开页面）";
            }
        };
    }

    // ---- 设备与接口 ----

    private void LoadDevices()
    {
        _devices.Clear();
        _capabilities.Clear();
        ProbeButton.IsEnabled = false;

        List<CaptureDeviceInfo> devices;
        try
        {
            devices = HidCaptureEnumerator.Enumerate();
        }
        catch (Exception exception)
        {
            TaskHint.Text = "枚举 HID 设备失败：" + exception.Message;
            return;
        }

        // 未知设备调查：未适配的排前面；已支持诊断按原顺序
        foreach (var device in devices)
        {
            _devices.Add(new CaptureDeviceRow
            {
                Device = device,
                Title = string.IsNullOrEmpty(device.Product) ? device.InterfaceKey : device.Product,
                Subtitle = $"{device.InterfaceKey}  ·  {device.VendorId:X4}:{device.ProductId:X4}",
            });
        }

        CaptureStatus.Text = _devices.Count == 0
            ? "没有找到 HID 设备；插好接收器后点「刷新设备」"
            : $"找到 {_devices.Count} 个接口，选择一项后点「探测接口」";
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void Task_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TaskHint is null)
        {
            return;
        }
        TaskHint.Text = TaskBox.SelectedIndex == 1
            ? "已在用的设备读数异常：用于排查插件与接口"
            : "未适配的新设备：列出全部接口，未适配的排前面";
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not CaptureDeviceRow row)
        {
            ProbeButton.IsEnabled = false;
            return;
        }
        _selected = row.Device;
        ProbeButton.IsEnabled = true;
        _capabilities.Clear();
    }

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }
        ProbeButton.IsEnabled = false;
        CaptureStatus.Text = "正在探测接口…";
        try
        {
            var device = _selected;
            var capabilities = await Task.Run(() => HidCapabilityProbe.Probe(device));
            _capabilities.Clear();
            foreach (var item in capabilities)
            {
                _capabilities.Add(CapabilityRow.From(item));
            }
            CaptureStatus.Text = _capabilities.Count == 0
                ? "没有可用的采集能力"
                : $"探测完成：{_capabilities.Count} 项能力，可开始采集";
        }
        catch (Exception exception)
        {
            CaptureStatus.Text = "探测失败：" + exception.Message;
        }
        finally
        {
            ProbeButton.IsEnabled = _selected is not null;
        }
    }

    // ---- 采集 ----

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            CaptureStatus.Text = "先选择一个设备接口";
            return;
        }

        try
        {
            var device = _selected;
            var capabilities = _capabilities.Select(c => c.Source).ToList();
            var options = new CaptureOptions
            {
                Task = TaskBox.SelectedIndex == 1
                    ? CaptureTaskKind.SupportedDeviceDiagnosis
                    : CaptureTaskKind.UnknownDeviceInvestigation,
                AppVersion = typeof(BatteryCaptureView).Assembly.GetName().Version?.ToString() ?? "",
                OsVersion = Environment.OSVersion.VersionString,
                Backend = "tool-dock",
                AllowSend = false,
            };

            // 采集包放在 ToolDock 自己的目录，不写 VigilWatt 的数据目录
            string captureRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolDock", "captures");
            Directory.CreateDirectory(captureRoot);

            _session = CaptureSession.Create(device, capabilities, options,
                () => new UsbHidCaptureSource(device, Array.Empty<byte>(), options.ReadTimeout, options.FeatureInterval),
                new CaptureStore(captureRoot));

            if (!_session.Start())
            {
                CaptureStatus.Text = "无法启动采集（设备可能被占用）";
                _session.Dispose();
                _session = null;
                return;
            }

            _guide = CaptureGuide.Default();
            RenderSteps();

            StartButton.IsEnabled = false;
            NextStepButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            DeviceList.IsEnabled = false;
            ProbeButton.IsEnabled = false;
            _timer.Start();
            UpdateCaptureStatus();
        }
        catch (Exception exception)
        {
            CaptureStatus.Text = "启动采集失败：" + exception.Message;
        }
    }

    private void NextStep_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _guide.IsComplete)
        {
            return;
        }
        var current = _guide.Current;
        if (current is not null)
        {
            _session.Mark(current.Title, current.Id);
        }
        if (!_guide.Advance())
        {
            NextStepButton.IsEnabled = false;
        }
        RenderSteps();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }
        StopButton.IsEnabled = false;
        NextStepButton.IsEnabled = false;
        _timer.Stop();
        CaptureStatus.Text = "正在整理采集包…";

        try
        {
            var session = _session;
            var bundle = await session.StopAsync();
            _lastPackageDirectory = session.Directory;
            CaptureStatus.Text = session.PackagePath is null
                ? $"采集结束：{bundle.Records.Count} 条记录（未生成压缩包）"
                : $"已导出：{bundle.Records.Count} 条记录";
            ExportText.Text = session.PackagePath ?? session.Directory;
            OpenFolderButton.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            CaptureStatus.Text = "导出失败：" + exception.Message;
        }
        finally
        {
            _session?.Dispose();
            _session = null;
            StartButton.IsEnabled = true;
            DeviceList.IsEnabled = true;
            ProbeButton.IsEnabled = _selected is not null;
        }
    }

    private void UpdateCaptureStatus()
    {
        if (_session is null)
        {
            return;
        }
        string step = _guide.Current is { } current ? $"当前步骤：{current.Title}" : "所有步骤已完成";
        CaptureStatus.Text = $"采集中 · {_session.RecordCount} 条记录 · {_session.ElapsedMs / 1000.0:0.0}s · {step}";
    }

    private void RenderSteps()
    {
        _steps.Clear();
        for (int i = 0; i < _guide.Steps.Count; i++)
        {
            var step = _guide.Steps[i];
            bool done = i < _guide.CurrentIndex;
            bool current = i == _guide.CurrentIndex;
            _steps.Add(new GuideStepRow
            {
                Title = step.Title,
                Hint = step.Hint,
                Marker = done ? "\uE73E" : current ? "\uE76C" : "\uE738",
                MarkerBrush = done ? DoneBrush : current ? ActiveBrush : IdleBrush,
                TitleBrush = current ? ActiveBrush : (Brush)Application.Current.Resources["Text.Primary"],
            });
        }
    }

    private static readonly Brush IdleBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Brush ActiveBrush = Frozen(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly Brush DoneBrush = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPackageDirectory is null || !Directory.Exists(_lastPackageDirectory))
        {
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastPackageDirectory}\"") { UseShellExecute = true });
    }
}

/// <summary>设备接口行。</summary>
public sealed class CaptureDeviceRow
{
    public CaptureDeviceInfo Device { get; init; } = null!;
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
}

/// <summary>接口能力行。</summary>
public sealed class CapabilityRow
{
    public CapabilityItem Source { get; init; } = null!;
    public string Title { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string Evidence { get; init; } = "";
    public Brush StatusBrush { get; init; } = Brushes.Gray;

    public static CapabilityRow From(CapabilityItem item)
    {
        (string text, Color color) = item.Status switch
        {
            CapabilityStatus.Available => ("可采集", Color.FromRgb(0x34, 0xC7, 0x59)),
            CapabilityStatus.Unavailable => ("不可用", Color.FromRgb(0xE2, 0x44, 0x5C)),
            CapabilityStatus.NeedsPermission => ("需授权", Color.FromRgb(0xFA, 0xAA, 0x17)),
            CapabilityStatus.NeedsExtraTool => ("需外部工具", Color.FromRgb(0xFA, 0xAA, 0x17)),
            _ => ("未验证", Color.FromRgb(0x8A, 0x8A, 0x8A)),
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return new CapabilityRow
        {
            Source = item,
            Title = item.Title,
            StatusText = text,
            Evidence = string.IsNullOrEmpty(item.Evidence) ? item.Reason ?? "" : item.Evidence!,
            StatusBrush = brush,
        };
    }
}

/// <summary>采集步骤行。</summary>
public sealed class GuideStepRow
{
    public string Title { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Marker { get; init; } = "";
    public Brush MarkerBrush { get; init; } = Brushes.Gray;
    public Brush TitleBrush { get; init; } = Brushes.Gray;
}

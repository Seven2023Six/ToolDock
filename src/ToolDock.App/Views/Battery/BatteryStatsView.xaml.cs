using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ToolDock.App.Services;
using VigilWatt.Core.Models;
using VigilWatt.Core.Storage;

namespace ToolDock.App.Views;

/// <summary>
/// 统计子页：按设备 + 时间窗展示电量走势（对应 VigilWatt 统计页）。
/// 图表用 Canvas 直绘：折线 + 百分比网格 + 首末时间刻度。
/// </summary>
public partial class BatteryStatsView : UserControl
{
    private readonly List<DeviceHistoryStat> _devices = new();
    private int _days = 1;
    private bool _loading;

    public BatteryStatsView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadDevices();
    }

    private void LoadDevices()
    {
        BatteryMonitor.Instance.Start();
        _loading = true;
        try
        {
            _devices.Clear();
            _devices.AddRange(BatteryMonitor.Instance.GetDeviceHistoryStats()
                .OrderByDescending(d => d.Last));

            DeviceBox.Items.Clear();
            foreach (var device in _devices)
            {
                DeviceBox.Items.Add(string.IsNullOrEmpty(device.Name) ? device.DeviceId : device.Name);
            }
            if (DeviceBox.Items.Count > 0)
            {
                DeviceBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _loading = false;
        }
        Redraw();
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            Redraw();
        }
    }

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var days))
        {
            _days = days;
            if (IsLoaded)
            {
                Redraw();
            }
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        ChartCanvas.Children.Clear();
        if (ChartCanvas.ActualWidth < 10 || ChartCanvas.ActualHeight < 10)
        {
            return;
        }

        int index = DeviceBox.SelectedIndex;
        if (index < 0 || index >= _devices.Count)
        {
            SummaryText.Text = "—";
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "还没有设备的电量记录";
            return;
        }

        var device = _devices[index];
        var to = DateTimeOffset.Now;
        var from = to.AddDays(-_days);
        var samples = BatteryMonitor.Instance.QueryHistory(device.DeviceId, from, to)
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (samples.Count < 2)
        {
            SummaryText.Text = $"{device.Name} · {samples.Count} 条记录";
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "该时间范围内没有足够记录";
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;

        int min = samples.Min(s => s.Percent);
        int max = samples.Max(s => s.Percent);
        double avg = samples.Average(s => s.Percent);
        SummaryText.Text = $"{device.Name} · {samples.Count} 条记录 · 最低 {min}% / 平均 {avg:0}% / 最高 {max}%";

        double width = ChartCanvas.ActualWidth;
        double height = ChartCanvas.ActualHeight;
        double padLeft = 34, padRight = 8, padTop = 8, padBottom = 20;
        double plotW = Math.Max(1, width - padLeft - padRight);
        double plotH = Math.Max(1, height - padTop - padBottom);

        var gridBrush = (Brush)Application.Current.Resources["Divider"];
        var textBrush = (Brush)Application.Current.Resources["Text.Tertiary"];
        var lineBrush = (Brush)Application.Current.Resources["Accent"];

        // 0/50/100 网格线
        foreach (var level in new[] { 0, 50, 100 })
        {
            double y = padTop + plotH * (1 - level / 100.0);
            ChartCanvas.Children.Add(new Line
            {
                X1 = padLeft,
                X2 = padLeft + plotW,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1,
            });
            var label = new TextBlock
            {
                Text = level + "%",
                FontSize = 10,
                Foreground = textBrush,
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 8);
            ChartCanvas.Children.Add(label);
        }

        // 折线
        var first = samples[0].Timestamp;
        var span = (samples[^1].Timestamp - first).TotalSeconds;
        if (span <= 0)
        {
            span = 1;
        }

        var points = new PointCollection();
        foreach (var sample in samples)
        {
            double x = padLeft + plotW * ((sample.Timestamp - first).TotalSeconds / span);
            double y = padTop + plotH * (1 - sample.Percent / 100.0);
            points.Add(new Point(x, y));
        }

        ChartCanvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = lineBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });

        // 首末时间刻度
        AddTimeLabel(first.ToLocalTime().ToString(_days == 1 ? "HH:mm" : "M/d"), padLeft, padTop + plotH + 4);
        AddTimeLabel(samples[^1].Timestamp.ToLocalTime().ToString(_days == 1 ? "HH:mm" : "M/d"),
            padLeft + plotW - 42, padTop + plotH + 4);
    }

    private void AddTimeLabel(string text, double left, double top)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["Text.Tertiary"],
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        ChartCanvas.Children.Add(label);
    }
}

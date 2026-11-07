using System.Windows.Controls;
using System.Windows.Threading;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>首页 · Widget 墙：时钟与内存实时刷新；电量/清理/开关随里程碑接入。</summary>
public partial class HomePage : UserControl
{
    private readonly DispatcherTimer _timer;

    public HomePage()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = $"{now.Month}月{now.Day}日 {ToChineseWeekDay(now.DayOfWeek)}";

        if (SystemMemory.Read() is not { } memory)
        {
            return;
        }
        MemUsageText.Text = memory.LoadPercent.ToString();
        MemUsedColumn.Width = new System.Windows.GridLength(memory.LoadPercent, System.Windows.GridUnitType.Star);
        MemFootnoteText.Text = $"已用 {memory.UsedGiB:0.0} / {memory.TotalGiB:0.0} GB";
    }

    private static string ToChineseWeekDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日",
    };
}

using System.Windows.Controls;
using System.Windows.Threading;
using ToolDock.App.Services;

namespace ToolDock.App.Views;

/// <summary>
/// 内存清理页：数据部分（使用率/构成）当前进程可直接读取，实时刷新；
/// 清理动作与历史依赖 M2 插件与提权方案，占位禁用。
/// </summary>
public partial class MemCleanPage : UserControl
{
    private readonly DispatcherTimer _timer;

    public MemCleanPage()
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
        if (SystemMemory.Read() is not { } memory)
        {
            return;
        }

        SummaryText.Text = $"物理内存 {memory.TotalGiB:0.0} GB · 实时刷新";
        UsageText.Text = memory.LoadPercent.ToString();

        // 使用率写入第一列宽度，剩余空间自动归"可用"
        UsedColumn.Width = new System.Windows.GridLength(memory.LoadPercent, System.Windows.GridUnitType.Star);
        UsedLegendText.Text = $"已用 {memory.UsedGiB:0.0} GB";
        FreeLegendText.Text = $"可用 {memory.FreeGiB:0.0} GB";
    }
}

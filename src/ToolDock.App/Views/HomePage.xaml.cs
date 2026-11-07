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
        MemUsageText.Text = memory.LoadPercent.ToString();
        // 两列都用占比星号：used : free = percent : (100 - percent)，比例才正确
        MemUsedColumn.Width = new System.Windows.GridLength(memory.LoadPercent, System.Windows.GridUnitType.Star);
        MemFreeColumn.Width = new System.Windows.GridLength(100 - memory.LoadPercent, System.Windows.GridUnitType.Star);
        MemFootnoteText.Text = $"已用 {memory.UsedGiB:0.0} / {memory.TotalGiB:0.0} GB";
    }

    private void CleanNow_Click(object sender, System.Windows.RoutedEventArgs e)
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

}

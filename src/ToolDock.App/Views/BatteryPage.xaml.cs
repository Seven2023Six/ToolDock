using System.Windows.Controls;

namespace ToolDock.App.Views;

public partial class BatteryPage : UserControl
{
    public BatteryPage()
    {
        InitializeComponent();
    }

    private void Back_Click(object sender, System.Windows.RoutedEventArgs e)
        => (System.Windows.Window.GetWindow(this) as MainWindow)?.ShowPage("builtin");
}

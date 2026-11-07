using System.Windows;

namespace ToolDock.App.Services;

/// <summary>
/// 主题切换：浅色（默认）/ 深色。
/// 实现方式是整体替换 App.xaml 合并字典里的第 0 项（主题字典），
/// 界面上的颜色全部走 DynamicResource，替换后立即生效。
/// </summary>
internal static class ThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Current { get; private set; } = Light;

    /// <summary>主题切换完成（参数为新主题名）。用于窗口同步标题栏/Mica 明暗。</summary>
    public static event Action<string>? ThemeChanged;

    /// <summary>启动时按设置装载主题（不触发事件、不回写设置）。</summary>
    public static void Initialize(string theme)
    {
        Current = theme == Dark ? Dark : Light;
        LoadDictionary(Current);
    }

    public static void Apply(string theme)
    {
        var target = theme == Dark ? Dark : Light;
        if (target == Current)
        {
            return;
        }
        Current = target;
        LoadDictionary(target);
        ThemeChanged?.Invoke(target);

        // Agent 在跑 → 回传给唯一写者落盘（后台发射，不阻塞 UI）；独立模式 → 直接写本地设置
        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendTheme(target);
        }
        else
        {
            var settings = Core.AppSettings.Load();
            settings.Theme = target;
            settings.Save();
        }
    }

    public static void Toggle() => Apply(Current == Dark ? Light : Dark);

    private static void LoadDictionary(string theme)
    {
        // pack URI 用程序集名（ToolDock）而非命名空间；动态取值避免改名后悄悄失效
        var assemblyName = typeof(ThemeService).Assembly.GetName().Name;
        var source = new Uri($"/{assemblyName};component/Themes/{theme}.xaml", UriKind.Relative);
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = source };
    }
}

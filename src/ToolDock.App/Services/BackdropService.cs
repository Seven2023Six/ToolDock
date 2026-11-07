using ToolDock.App.Services;

namespace ToolDock.App;

/// <summary>
/// 窗口材质（Mica / Acrylic / 纯色）的当前状态与切换。
/// 与 ThemeService 同构：Agent 在跑经管道落盘，独立模式写本地设置。
/// </summary>
internal static class BackdropService
{
    public const string Mica = "Mica";
    public const string Acrylic = "Acrylic";
    public const string Solid = "Solid";

    public static string Current { get; private set; } = Mica;

    /// <summary>材质切换完成（参数为新值）。MainWindow 据此重涂窗口背景。</summary>
    public static event Action<string>? BackdropChanged;

    public static void Initialize(string mode)
    {
        Current = Normalize(mode);
    }

    public static void Apply(string mode)
    {
        var target = Normalize(mode);
        if (target == Current)
        {
            return;
        }
        Current = target;
        BackdropChanged?.Invoke(target);

        if (AgentLink.Instance.Connected)
        {
            AgentLink.Instance.SendBackdrop(target);
        }
        else
        {
            var settings = Core.AppSettings.Load();
            settings.Backdrop = target;
            settings.Save();
        }
    }

    private static string Normalize(string mode) => mode == Acrylic ? Acrylic : mode == Solid ? Solid : Mica;
}

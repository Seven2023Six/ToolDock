namespace ToolDock.Abstractions;

/// <summary>外壳提供给插件的全部能力，收口于此。每个插件拿到的是按插件隔离的实例。</summary>
public interface IAgentContext
{
    ITrayService Tray { get; }
    INotificationService Notify { get; }
    ISettingsStore Settings { get; }
    IHotkeyService Hotkeys { get; }
    IElevationService Elevation { get; }
    ILogger Log { get; }
}

/// <summary>托盘贡献。图标由 Agent 独占——插件只允许往菜单和 tooltip 状态行里"放东西"。</summary>
public interface ITrayService
{
    /// <summary>往托盘右键菜单里加一项（位于"退出"上方）。</summary>
    void AddMenu(string header, Action onClick);

    /// <summary>移除该文字对应的菜单项（工具停用时撤掉入口）。</summary>
    void RemoveMenu(string header);

    /// <summary>设置本插件在托盘 tooltip 里的状态行（如"蓝牙耳机 72%"），传 null 清除。</summary>
    void SetStatusLine(string? text);
}

public interface INotificationService
{
    /// <summary>托盘气泡通知（如"设备电量低于 20%"）。</summary>
    void Show(string title, string message);
}

/// <summary>按插件命名空间隔离的设置读写，宿主负责持久化（JSON）。</summary>
public interface ISettingsStore
{
    T Get<T>(string key, T fallback);
    void Set<T>(string key, T value);
    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>全局热键注册（轮盘/快捷面板唤出的底座）。释放返回的句柄即注销。</summary>
public interface IHotkeyService
{
    IDisposable Register(int modifiers, int virtualKey, Action callback);
}

/// <summary>
/// 提权请求路由。实现待 UAC 方案定案（整壳提权=直通；拆分提权=转发给 ElevatedHelper），
/// 插件代码两种情况下零改动。
/// </summary>
public interface IElevationService
{
    bool IsElevated { get; }

    /// <summary>以管理员身份运行指定程序，返回是否成功。</summary>
    Task<bool> RunElevatedAsync(string executablePath, string arguments);
}

public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

using System.IO.Pipes;
using System.Text.Json.Nodes;
using ToolDock.Abstractions;
using ToolDock.Agent.Native;

namespace ToolDock.Agent;

/// <summary>跨进程会话的运行时开关（设置页经管道下发）。</summary>
internal static class AgentRuntimeState
{
    public static bool NotificationsEnabled = true;
}

/// <summary>后台插件拿到的上下文：托盘/通知共享，设置与日志按插件隔离。</summary>
internal sealed class PluginContext(string pluginId, TrayIconController tray) : IAgentContext
{
    public ITrayService Tray { get; } = new TrayServiceAdapter(tray, pluginId);
    public INotificationService Notify { get; } = new BalloonNotifier(tray);
    public ISettingsStore Settings { get; } = new JsonFileSettingsStore(pluginId);
    public IHotkeyService Hotkeys { get; } = new HotkeyStub();
    public IElevationService Elevation { get; } = new ElevationService();
    public ILogger Log { get; } = new FileLogger(pluginId);
}

internal sealed class TrayServiceAdapter(TrayIconController tray, string pluginId) : ITrayService
{
    public void AddMenu(string header, Action onClick) => tray.AddPluginItem(header, onClick);

    public void SetStatusLine(string? text) => tray.UpdateStatusLine(pluginId, text);
}

internal sealed class BalloonNotifier(TrayIconController tray) : INotificationService
{
    public void Show(string title, string message)
    {
        if (AgentRuntimeState.NotificationsEnabled)
        {
            tray.ShowBalloon(title, message);
        }
    }
}

/// <summary>按插件隔离的 JSON 设置存储（%APPDATA%\ToolDock\plugindata\&lt;id&gt;.json）。
/// 只支持基元类型，走 JsonNode 具体分支而非泛型 Create/GetValue——AOT 下不产生动态代码。</summary>
internal sealed class JsonFileSettingsStore(string pluginId) : ISettingsStore
{
    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "plugindata");

    private static string PathFor(string id) => Path.Combine(DirectoryPath, $"{id}.json");

    private readonly JsonObject _root = LoadRoot(PathFor(pluginId));
    private readonly object _gate = new();

    public T Get<T>(string key, T fallback)
    {
        lock (_gate)
        {
            if (_root[key] is JsonValue value)
            {
                // 逐类型具体分支：AOT 不为未知 T 生成代码
                if (value.TryGetValue<bool>(out var b) && typeof(T) == typeof(bool))
                {
                    return (T)(object)b;
                }
                if (value.TryGetValue<int>(out var i) && typeof(T) == typeof(int))
                {
                    return (T)(object)i;
                }
                if (value.TryGetValue<uint>(out var u) && typeof(T) == typeof(uint))
                {
                    return (T)(object)u;
                }
                if (value.TryGetValue<long>(out var l) && typeof(T) == typeof(long))
                {
                    return (T)(object)l;
                }
                if (value.TryGetValue<double>(out var d) && typeof(T) == typeof(double))
                {
                    return (T)(object)d;
                }
                if (value.TryGetValue<string>(out var s) && typeof(T) == typeof(string))
                {
                    return (T)(object)s!;
                }
            }
        }
        return fallback;
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            _root[key] = value switch
            {
                null => null,
                bool v => JsonValue.Create(v),
                int v => JsonValue.Create(v),
                uint v => JsonValue.Create(v),
                long v => JsonValue.Create(v),
                double v => JsonValue.Create(v),
                string v => JsonValue.Create(v),
                _ => throw new NotSupportedException("插件设置仅支持 bool/int/uint/long/double/string"),
            };
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            lock (_gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(PathFor(pluginId),
                    _root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }, cancellationToken);

    private static JsonObject LoadRoot(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
            {
                return obj;
            }
        }
        catch
        {
            // 损坏即重建
        }
        return new JsonObject();
    }
}

/// <summary>全局热键在轮盘/快捷面板里程碑接入；先给占位实现防插件炸。</summary>
internal sealed class HotkeyStub : IHotkeyService
{
    public IDisposable Register(int modifiers, int virtualKey, Action callback)
    {
        LogFile.Write($"[hotkey] 全局热键尚未实现，注册被忽略（modifiers={modifiers}, key={virtualKey}）");
        return new NullDisposable();
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>最小可用的提权服务：接口已冻结，帮手进程由 UAC 方案定案后接入。</summary>
internal sealed class ElevationService : IElevationService
{
    public bool IsElevated => NativeMethods.IsUserAnAdmin();

    public Task<bool> RunElevatedAsync(string executablePath, string arguments)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executablePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 用户在 UAC 弹窗点取消 / 启动失败
            return Task.FromResult(false);
        }
    }
}

internal sealed class FileLogger(string category) : ILogger
{
    public void Info(string message) => LogFile.Write($"[{category}] {message}");
    public void Warn(string message) => LogFile.Write($"[{category}][WARN] {message}");
    public void Error(string message, Exception? exception = null)
        => LogFile.Write($"[{category}][ERROR] {message}{(exception is null ? "" : $" — {exception.GetType().Name}: {exception.Message}")}");
}

internal static class LogFile
{
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ToolDock", "logs");
            Directory.CreateDirectory(dir);
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(dir, $"agent-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响运行
        }
    }
}

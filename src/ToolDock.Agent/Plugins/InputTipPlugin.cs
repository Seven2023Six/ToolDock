using System.Diagnostics;
using ToolDock.Abstractions;
using ToolDock.InputTip;

namespace ToolDock.Agent;

/// <summary>
/// 输入法状态提示（InputTip 原生移植）：引擎随 Agent 进程内运行，
/// 没有 AutoHotkey.exe、没有第二个托盘；设置页直接编辑
/// %APPDATA%\ToolDock\inputtip\settings.json，保存后由 Agent 重载引擎。
/// </summary>
internal sealed class InputTipPlugin : IBackgroundPlugin
{
    public PluginMetadata Metadata { get; } = new("inputtip", "输入法状态提示", "1.0.0", "系统");

    private const string MenuPause = "暂停输入法提示";
    private const string MenuResume = "恢复输入法提示";
    private const string MenuReload = "重新加载输入法提示配置";
    private const string MenuStateCode = "显示输入法状态码（切换）";

    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "inputtip");

    private IAgentContext? _context;
    private InputTipSettings? _settings;
    private InputTipEngine? _engine;
    private bool _enabled;
    private bool _paused;
    private string _state = "EN";
    private string _statusTip = "";
    private bool _menusAdded;
    private readonly object _gate = new();

    public Task InitializeAsync(IAgentContext context, CancellationToken cancellationToken)
    {
        _context = context;
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            EnsureAssets();
            _settings = InputTipSettings.Load(ConfigDirectory);
            _enabled = _settings.Enabled;
            if (_enabled)
            {
                StartEngine();
            }
            if (_enabled)
            {
                AddMenus();
            }
            context.Log.Info(_enabled
                ? $"输入法状态提示已初始化（配置：{_settings.ConfigPath}）"
                : "输入法状态提示已停用（配置保留）");
        }
        catch (Exception exception)
        {
            context.Log.Error("输入法状态提示初始化失败", exception);
        }
        return Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        StopEngine();
        return Task.CompletedTask;
    }

    public (bool Enabled, bool Running, bool Paused, string State, string ConfigPath, string Engine) GetConfig()
    {
        lock (_gate)
        {
            return (_enabled, _engine is not null, _paused, _state, _settings?.ConfigPath ?? "", "1.0");
        }
    }

    public InputTipSettings? Settings => _settings;

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_settings is not null)
            {
                _settings.Enabled = enabled;
                _settings.Save();
            }
            _enabled = enabled;
            if (enabled)
            {
                AddMenus();
                StartEngine();
            }
            else
            {
                RemoveMenus();
                StopEngine();
            }
        }
        _context?.Log.Info("输入法状态提示已" + (enabled ? "启用" : "停用"));
        UpdateStatusLine();
    }

    public void TogglePause() => SetPaused(!_paused);

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_engine is null)
            {
                _paused = paused;
                return;
            }
            _engine.SetPaused(paused);
        }
    }

    /// <summary>设置页保存后调用：重读配置并重启引擎。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            StopEngine();
            try
            {
                _settings = InputTipSettings.Load(ConfigDirectory);
                _enabled = _settings.Enabled;
            }
            catch (Exception exception)
            {
                _context?.Log.Error("输入法提示配置读取失败", exception);
                return;
            }
            if (_enabled)
            {
                StartEngine();
            }
        }
        _context?.Log.Info("输入法提示配置已重新加载");
        UpdateStatusLine();
    }

    private void StartEngine()
    {
        if (_engine is not null || _settings is null)
        {
            return;
        }
        try
        {
            EnsureAssets();
            var engine = new InputTipEngine(_settings,
                Path.Combine(_settings.AssetsDirectory, "symbol"),
                Path.Combine(_settings.AssetsDirectory, "default-cursor"));
            engine.Log = message => _context?.Log.Warn("[inputtip] " + message);
            InputTipDiagnostics.Log = message => _context?.Log.Warn("[inputtip] " + message);
            engine.StateChanged += OnStateChanged;
            engine.PausedChanged += OnPausedChanged;
            engine.TrayTipChanged += OnTrayTipChanged;
            engine.ExitRequested += OnExitRequested;
            engine.RestartRequested += Reload;
            engine.Start();
            _engine = engine;
            _paused = false;
            _context?.Log.Info("输入法提示引擎已启动");
        }
        catch (Exception exception)
        {
            _context?.Log.Error("输入法提示引擎启动失败", exception);
            _engine = null;
        }
    }

    private void StopEngine()
    {
        var engine = _engine;
        _engine = null;
        if (engine is null)
        {
            return;
        }
        try
        {
            engine.StateChanged -= OnStateChanged;
            engine.PausedChanged -= OnPausedChanged;
            engine.TrayTipChanged -= OnTrayTipChanged;
            engine.ExitRequested -= OnExitRequested;
            engine.RestartRequested -= Reload;
            engine.Stop();
            engine.Dispose();
        }
        catch (Exception exception)
        {
            _context?.Log.Warn("输入法提示引擎停止失败：" + exception.Message);
        }
        _paused = false;
    }

    private void OnStateChanged(string state)
    {
        _state = state;
        UpdateStatusLine();
    }

    private void OnPausedChanged(bool paused)
    {
        _paused = paused;
        if (_menusAdded && _context is { } context)
        {
            if (paused)
            {
                context.Tray.RemoveMenu(MenuPause);
                context.Tray.AddMenu(MenuResume, () => TogglePause());
            }
            else
            {
                context.Tray.RemoveMenu(MenuResume);
                context.Tray.AddMenu(MenuPause, () => TogglePause());
            }
        }
        UpdateStatusLine();
    }

    private void AddMenus()
    {
        if (_menusAdded || _context is not { } context)
        {
            return;
        }
        context.Tray.AddMenu(_paused ? MenuResume : MenuPause, () => TogglePause());
        context.Tray.AddMenu(MenuReload, () => Reload());
        context.Tray.AddMenu(MenuStateCode, () => RunTrigger("showStateCode"));
        _menusAdded = true;
    }

    private void RemoveMenus()
    {
        if (!_menusAdded || _context is not { } context)
        {
            return;
        }
        context.Tray.RemoveMenu(MenuPause);
        context.Tray.RemoveMenu(MenuResume);
        context.Tray.RemoveMenu(MenuReload);
        context.Tray.RemoveMenu(MenuStateCode);
        _menusAdded = false;
    }

    private void OnTrayTipChanged(string tip)
    {
        _statusTip = tip;
        UpdateStatusLine();
    }

    private void OnExitRequested()
    {
        // 规则动作在引擎线程上触发：停用会 Join 引擎线程，必须转到线程池，避免自锁
        _ = Task.Run(() =>
        {
            _context?.Log.Info("规则触发「退出」：停用输入法状态提示");
            SetEnabled(false);
            _context?.Notify.Show("输入法状态提示", "规则触发了退出，已在工具箱内停用该工具");
        });
    }

    private void UpdateStatusLine()
    {
        if (_context is not { } context)
        {
            return;
        }
        if (!_enabled)
        {
            context.Tray.SetStatusLine(null);
            return;
        }
        string label = InputTipStates.ShortLabels.TryGetValue(_state, out var shortLabel) ? shortLabel : _state;
        string text = $"输入法 {label}" + (_paused ? "（已暂停）" : "");
        if (_settings?.EnableKeyStats == true && !string.IsNullOrWhiteSpace(_statusTip))
        {
            text += " · " + _statusTip.Replace("\r", "").Replace("\n", " ");
        }
        context.Tray.SetStatusLine(text);
    }

    public void RunTrigger(string trigger)
    {
        _engine?.RunTriggers([trigger]);
    }

    /// <summary>首次运行把随包的默认光标/符号复制到用户配置目录（已存在的不覆盖）。</summary>
    private void EnsureAssets()
    {
        if (_settings is null)
        {
            return;
        }
        string? source = ResolveBundledAssets();
        if (source is null)
        {
            return;
        }
        CopyMissing(source, _settings.AssetsDirectory);
    }

    private static string? ResolveBundledAssets()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "tools", "inputtip", "assets"),
            Path.Combine(baseDirectory, "assets", "tools", "inputtip", "assets"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "assets", "tools", "inputtip", "assets"),
        ];
        return candidates.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
    }

    private static void CopyMissing(string source, string target)
    {
        try
        {
            Directory.CreateDirectory(target);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, directory);
                Directory.CreateDirectory(Path.Combine(target, relative));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string destination = Path.Combine(target, relative);
                if (!File.Exists(destination))
                {
                    File.Copy(file, destination);
                }
            }
        }
        catch
        {
            // 资源复制失败不阻塞引擎：自定义资源缺失时回落到内置默认
        }
    }

    /// <summary>打开设置页（托盘菜单/界面共用）。</summary>
    public static string GetConfigPath()
        => Path.Combine(ConfigDirectory, "settings.json");
}

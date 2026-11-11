using System.Runtime.InteropServices;
using System.Text;
using ToolDock.Abstractions;

namespace ToolDock.Agent;

/// <summary>
/// 窗口拖拽（内置窗口拖拽引擎 1.0，基于 AltSnap 1.68 内核）。
/// 不再启动 AltSnap.exe：把官方 hooks.dll 引擎 + 去掉托盘/配置窗口的宿主 DLL
/// 加载进 Agent 进程，由 Agent 的专用 STA 线程跑它自己的消息循环。
/// 设置直接写 AltSnap.ini，保存后 Stop/Start 让引擎重新读取，功能与上游一致。
/// </summary>
internal sealed class AltSnapPlugin : IBackgroundPlugin
{
    public PluginMetadata Metadata { get; } = new("altsnap", "窗口拖拽", "1.0.0", "系统");

    private const string EngineVersion = "1.0";

    private readonly object _gate = new();
    private static readonly string IniDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolDock", "altsnap");
    private static readonly string IniPath = Path.Combine(IniDirectory, "AltSnap.ini");

    private IAgentContext? _context;
    private bool _enabled = true;
    private bool _running;
    private Thread? _thread;
    private IntPtr _module;
    private IntPtr _startExport;
    private IntPtr _stopExport;
    private IntPtr _readyExport;
    private IntPtr _iniPtr;
    private IntPtr _hooksPtr;

    public Task InitializeAsync(IAgentContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _enabled = context.Settings.Get("Enabled", true);
        EnsureIni();
        if (_enabled)
        {
            StartEngine();
        }
        context.Log.Info(_enabled
            ? $"AltSnap 引擎已初始化（v{EngineVersion}，随工具箱后台运行）"
            : "AltSnap 引擎已停用（配置保留）");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        StopEngine();
        return Task.CompletedTask;
    }

    public (bool Enabled, bool Running, string IniPath, string Engine) GetConfig()
        => (_enabled, _running, IniPath, EngineVersion);

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Persist();
        if (enabled)
        {
            StartEngine();
        }
        else
        {
            StopEngine();
        }
        _context?.Log.Info("AltSnap 窗口拖拽已" + (enabled ? "启用" : "停用"));
    }

    /// <summary>配置文件已改写：重启引擎使其重新读取。</summary>
    public void RestartEngine()
    {
        StopEngine();
        if (_enabled)
        {
            StartEngine();
        }
    }

    private void Persist()
    {
        if (_context is not { } context)
        {
            return;
        }
        context.Settings.Set("Enabled", _enabled);
        context.Settings.SaveAsync().GetAwaiter().GetResult();
    }

    private void StartEngine()
    {
        lock (_gate)
        {
            if (_running || _thread is { IsAlive: true })
            {
                return;
            }
            string? dir = ResolveEngineDir();
            if (dir is null)
            {
                _context?.Log.Error("AltSnap 引擎缺失：找不到 tools/altsnap/altsnap_host.dll");
                return;
            }
            string hostPath = Path.Combine(dir, "altsnap_host.dll");
            string hooksPath = Path.Combine(dir, "hooks.dll");
            if (!File.Exists(hooksPath))
            {
                _context?.Log.Error("AltSnap 引擎缺失：找不到 hooks.dll");
                return;
            }
            EnsureIni();

            try
            {
                _module = NativeLibrary.Load(hostPath);
                _startExport = NativeLibrary.GetExport(_module, "ToolDockAltSnapStart");
                _stopExport = NativeLibrary.GetExport(_module, "ToolDockAltSnapStop");
                _readyExport = NativeLibrary.GetExport(_module, "ToolDockAltSnapReady");
                _iniPtr = Marshal.StringToHGlobalUni(IniPath);
                _hooksPtr = Marshal.StringToHGlobalUni(dir);
                _running = true;
                _thread = new Thread(EngineThread)
                {
                    IsBackground = true,
                    Name = "AltSnapEngine",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                for (int i = 0; i < 40 && _running && !IsEngineReady(); i++)
                {
                    Thread.Sleep(50);
                }
                _context?.Log.Info($"AltSnap 引擎已启动（{hostPath}，ready={IsEngineReady()}）");
            }
            catch (Exception exception)
            {
                _context?.Log.Error("AltSnap 引擎启动失败", exception);
                CleanupModule();
            }
        }
    }

    private unsafe bool IsEngineReady()
    {
        if (_readyExport == 0)
        {
            return false;
        }
        var ready = (delegate* unmanaged[Stdcall]<int>)_readyExport;
        return ready() != 0;
    }

    private unsafe void EngineThread()
    {
        try
        {
            if (_startExport != 0)
            {
                var start = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)_startExport;
                int rc = start(_iniPtr, _hooksPtr);
                _context?.Log.Info($"AltSnap 引擎消息循环结束 rc={rc}");
            }
        }
        catch (Exception exception)
        {
            _context?.Log.Error("AltSnap 引擎线程异常", exception);
        }
        finally
        {
            _running = false;
        }
    }

    private void StopEngine()
    {
        lock (_gate)
        {
            if (_thread is null && _module == IntPtr.Zero)
            {
                return;
            }
            try
            {
                if (_running && _stopExport != 0)
                {
                    unsafe
                    {
                        var stop = (delegate* unmanaged[Stdcall]<void>)_stopExport;
                        stop();
                    }
                }
                if (_thread is { IsAlive: true })
                {
                    _thread.Join(3000);
                }
                if (_thread is { IsAlive: true })
                {
                    // 引擎没有在 3 秒内退出：保留模块，避免卸载正在執行代码的 DLL。
                    _context?.Log.Warn("AltSnap 引擎未在 3 秒内退出，模块保持加载");
                    return;
                }
                _running = false;
                _thread = null;
                CleanupModule();
            }
            catch (Exception exception)
            {
                _context?.Log.Error("AltSnap 引擎停止失败", exception);
            }
        }
    }

    private void CleanupModule()
    {
        if (_iniPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_iniPtr);
            _iniPtr = IntPtr.Zero;
        }
        if (_hooksPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_hooksPtr);
            _hooksPtr = IntPtr.Zero;
        }
        if (_module != IntPtr.Zero)
        {
            try
            {
                NativeLibrary.Free(_module);
            }
            catch
            {
                // DLL 已卸载或引用计数异常：忽略
            }
            _module = IntPtr.Zero;
        }
        _startExport = IntPtr.Zero;
        _stopExport = IntPtr.Zero;
        _readyExport = IntPtr.Zero;
    }

    /// <summary>发布布局 tools\altsnap；开发布局回退仓库 assets\tools\altsnap。</summary>
    private static string? ResolveEngineDir()
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (string relative in new[] { "tools/altsnap", "assets/tools/altsnap" })
        {
            string dir = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(Path.Combine(dir, "altsnap_host.dll")) &&
                File.Exists(Path.Combine(dir, "hooks.dll")))
            {
                return dir;
            }
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileStringW(
        string section, string key, string defaultValue, StringBuilder returned,
        uint size, string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WritePrivateProfileStringW(
        string section, string key, string value, string filePath);

    /// <summary>首次使用把官方默认配置复制成可编辑的 AltSnap.ini，并保留一份模板供“恢复默认”。</summary>
    private static void EnsureIni()
    {
        try
        {
            Directory.CreateDirectory(IniDirectory);
            string? dir = ResolveEngineDir();
            if (dir is null)
            {
                return;
            }
            string template = Path.Combine(dir, "AltSnap.dni");
            if (!File.Exists(template))
            {
                return;
            }
            string defaultPath = Path.Combine(IniDirectory, "AltSnap.dni");
            if (!File.Exists(defaultPath))
            {
                File.Copy(template, defaultPath);
            }
            if (!File.Exists(IniPath))
            {
                File.Copy(template, IniPath);
            }
            // 引擎菜单/动作菜单按 ini 的 Language 加载 Lang\*.ini；工具栏界面固定中文
            var language = new StringBuilder(32);
            GetPrivateProfileStringW("General", "Language", "Auto", language, 32, IniPath);
            if (language.Length == 0 ||
                language.ToString().Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                WritePrivateProfileStringW("General", "Language", "zh-CN", IniPath);
            }
        }
        catch
        {
            // 目录不可写：引擎会用空配置启动，页面仍可提示
        }
    }
}

using System.IO;
using System.IO.Pipes;
using System.Windows;
using ToolDock.Core;

namespace ToolDock.App.Services;

/// <summary>
/// 与常驻 Agent 的管道连接（客户端）。
/// Agent 在跑 → 握手拿全局设置，主题变更回传给 Agent 落盘；
/// Agent 不在（纯开发场景）→ 独立模式，主题改动直接写本地文件。
///
/// 并发纪律（重要）：
/// 1. 客户端必须用 PipeOptions.Asynchronous（重叠 IO）——读循环常驻一个 pending read，
///    非重叠句柄的写会被内核排在同一个文件对象队列里，UI 线程的写就永远等不到（卡死根源）。
/// 2. UI 线程禁止同步等待任何管道 IO：写操作一律后台线程发射（SendTheme），
///    握手用 Task.Run + 有界 Wait。
/// </summary>
internal sealed class AgentLink : IDisposable
{
    public static AgentLink Instance { get; } = new();

    public bool Connected { get; private set; }

    /// <summary>Agent 上报的协议版本（握手时记录；仅用于诊断与日志）。</summary>
    public int AgentProtocolVersion { get; private set; }

    /// <summary>true = Agent 报告已有界面窗口，本实例应立即退出。</summary>
    public bool DuplicateRequested { get; private set; }

    /// <summary>Agent 下发的当前主题（连接成功时有效）。</summary>
    public string? WelcomeTheme { get; private set; }

    public ulong CurrentMask { get; set; } = 0xE7;
    public bool CurrentAutoClean { get; set; }
    public uint CurrentThreshold { get; set; } = 90;
    public uint CurrentIntervalM { get; set; } = 30;
    public bool CurrentSkipUac { get; set; }

    /// <summary>Agent 纠正免 UAC 状态时触发（值 = 真实状态）。</summary>
    public static event Action<bool>? SkipUacChanged;

    /// <summary>托盘再次请求打开界面。后台贴片模式下主窗还没创建，由 App 负责按需创建。</summary>
    public static event Action? ActivateRequested;
    public bool CurrentAllowFreezes { get; set; }
    public bool CurrentConfirmClean { get; set; } = true;
    public bool CurrentBubbleResults { get; set; } = true;
    public bool CurrentHotkeyEnabled { get; set; }
    public uint CurrentHotkeyMods { get; set; } = 0x0002;
    public uint CurrentHotkeyVk { get; set; } = 0x70;
    public bool CurrentLogResults { get; set; }

    /// <summary>最近一次清理结果（welcome 下发，仅用于界面展示）。</summary>
    public ulong CurrentLastCleanFreed { get; set; }
    public string CurrentLastCleanAt { get; set; } = "";
    public int CurrentLastCleanFailed { get; set; }

    // ---- 内置窗口拖拽（AltSnap 引擎 v0.1）----
    public bool CurrentAltSnapEnabled { get; set; } = true;
    public bool CurrentAltSnapRunning { get; set; }
    public string CurrentAltSnapIniPath { get; set; } = "";
    public string CurrentAltSnapEngine { get; set; } = "";

    /// <summary>Agent 下发窗口拖拽配置（页面加载后异步填充）。</summary>
    public static event Action? AltSnapUpdated;

    // ---- 内置输入法状态提示（InputTip 引擎 v1.0）----
    public bool CurrentInputTipEnabled { get; set; } = true;
    public bool CurrentInputTipRunning { get; set; }
    public bool CurrentInputTipPaused { get; set; }
    public string CurrentInputTipState { get; set; } = "";
    public string CurrentInputTipConfigPath { get; set; } = "";
    public string CurrentInputTipEngine { get; set; } = "";

    /// <summary>Agent 下发输入法提示状态（页面加载后异步填充）。</summary>
    public static event Action? InputTipUpdated;

    /// <summary>Agent 下发的窗口材质（连接成功时有效），随设置变更更新。</summary>
    public string CurrentBackdrop { get; set; } = "Mica";

    /// <summary>Agent 下发的开机自启状态，随设置变更更新。</summary>
    public bool CurrentAutoStart { get; set; }

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private System.Threading.Timer? _heartbeat;
    private DateTime _lastActivity = DateTime.MinValue;
    private int _reconnecting;
    private bool _disposed;

    public void Connect(int timeoutMilliseconds)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", "tooldock-agent", PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(timeoutMilliseconds);

            // 握手放到线程池并有界等待：绝不让 UI 线程无限等 IO
            var handshake = Task.Run(async () =>
            {
                await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "hello", Ver = PipeProtocol.Version }, CancellationToken.None)
                    .ConfigureAwait(false);
                return await PipeFrame.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false);
            });
            if (!handshake.Wait(1500))
            {
                pipe.Dispose();
                return;
            }

            var welcome = handshake.Result;
            if (welcome is null)
            {
                pipe.Dispose();
                return;
            }
            if (welcome.Duplicate)
            {
                DuplicateRequested = true;
                pipe.Dispose();
                return;
            }

            _pipe = pipe;
            Connected = true;
            AgentProtocolVersion = welcome.Ver;
            _lastActivity = DateTime.UtcNow;
            WelcomeTheme = welcome.Theme;
            CurrentBackdrop = welcome.Backdrop ?? "Mica";
            CurrentAutoStart = welcome.AutoStart;
            CurrentMask = welcome.Mask;
            CurrentAutoClean = welcome.AutoClean;
            CurrentThreshold = welcome.Threshold;
            CurrentIntervalM = welcome.IntervalM;
            CurrentSkipUac = welcome.SkipUac;
            CurrentAllowFreezes = welcome.AllowFreezes;
            CurrentConfirmClean = welcome.ConfirmClean;
            CurrentBubbleResults = welcome.BubbleResults;
            CurrentHotkeyEnabled = welcome.HotkeyEnabled;
            CurrentHotkeyMods = welcome.HotkeyMods;
            CurrentHotkeyVk = welcome.HotkeyVk;
            CurrentLogResults = welcome.LogResults;
            CurrentLastCleanFreed = welcome.LastCleanFreed;
            CurrentLastCleanAt = welcome.LastCleanAt ?? "";
            CurrentLastCleanFailed = welcome.LastCleanFailed;
            ApplyAltSnap(welcome);
            ApplyInputTip(welcome);
            _ = ReadLoopAsync(pipe);
            StartHeartbeat();
        }
        catch (TimeoutException)
        {
            Connected = false; // Agent 未运行，进入独立模式
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Connected = false;
        }
    }

    /// <summary>
    /// 主题变更回传 Agent 落盘。发射后不管：UI 线程只做入队，永不阻塞。
    /// 写入失败只置断线标记；调用方在 Connected=false 时自行走本地兜底。
    /// </summary>
    public void SendTheme(string theme)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "setTheme", Theme = theme }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>窗口材质变更回传 Agent 落盘（发射后不管，同 SendTheme）。</summary>
    public void SendBackdrop(string backdrop)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "setBackdrop", Backdrop = backdrop }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>开机自启变更回传 Agent（Agent 负责写/删注册表并落盘）。</summary>
    public void SendAutoStart(bool enabled)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "setAutoStart", Enable = enabled }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>清理配置（区域掩码 + 自动策略）下发 Agent（发射后不管）。</summary>
    public void SendCleanCfg(ulong mask, bool autoClean, uint threshold, uint intervalMinutes, bool allowFreezes, bool confirmClean, bool bubbleResults = true, bool logResults = false)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage
                    {
                        T = "setCleanCfg",
                        Mask = mask,
                        AutoClean = autoClean,
                        Threshold = threshold,
                        IntervalM = intervalMinutes,
                        AllowFreezes = allowFreezes,
                        ConfirmClean = confirmClean,
                        BubbleResults = bubbleResults,
                        LogResults = logResults,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>免 UAC 提权助手开关下发 Agent。</summary>
    public void SendSkipUac(bool enabled)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "setSkipUac", Enable = enabled }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>全局热键清理配置下发 Agent。</summary>
    public void SendHotkey(bool enabled, uint modifiers, uint virtualKey)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage
                    {
                        T = "setHotkey",
                        HotkeyEnabled = enabled,
                        HotkeyMods = modifiers,
                        HotkeyVk = virtualKey,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>工具总开关下发 Agent（停用后移除托盘入口、停止自动清理）。</summary>
    public void SendToolEnabled(bool enabled)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "setToolEnabled", Enable = enabled }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>让 Agent 弹托盘气泡（低电量提醒等）。</summary>
    public void SendNotify(string title, string text)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "notify", Title = title, Text = text }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>把最低电量同步到托盘提示（null 清空）。</summary>
    public void SendBatteryStatus(string? text)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "batteryStatus", Text = text }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>立即清理指令下发 Agent（confirmed=true 表示 App 端已弹过确认框）。</summary>
    public void SendCleanNow(bool confirmed, bool silent = false)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "cleanNow", Confirmed = confirmed, Silent = silent }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>请求 Agent 下发当前窗口拖拽配置（页面打开时调用）。</summary>
    public void RequestAltSnap() => SendAltSnapMessage(new PipeMessage { T = "getAltSnap" });

    /// <summary>请求 Agent 下发当前输入法提示状态。</summary>
    public void RequestInputTip() => SendInputTipMessage(new PipeMessage { T = "getInputTip" });

    /// <summary>设置已写入磁盘：让 Agent 重载输入法提示引擎。</summary>
    public void SendInputTipRestart()
        => SendInputTipMessage(new PipeMessage { T = "restartInputTip" });

    public void SendInputTipEnabled(bool enabled)
        => SendInputTipMessage(new PipeMessage { T = "setInputTipEnabled", Enable = enabled });

    public void SendInputTipPaused(bool paused)
        => SendInputTipMessage(new PipeMessage { T = "setInputTipPaused", Enable = paused });

    public void SendInputTipAction(string trigger)
        => SendInputTipMessage(new PipeMessage { T = "inputTipAction", Text = trigger });

    private void SendInputTipMessage(PipeMessage message)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, message, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    private void ApplyInputTip(PipeMessage message)
    {
        CurrentInputTipEnabled = message.InputTipEnabled;
        CurrentInputTipRunning = message.InputTipRunning;
        CurrentInputTipPaused = message.InputTipPaused;
        CurrentInputTipState = message.InputTipState ?? "";
        CurrentInputTipConfigPath = message.InputTipConfigPath ?? "";
        CurrentInputTipEngine = message.InputTipEngine ?? "";
    }

    /// <summary>配置文件已写入：让 Agent 重启内置引擎重新读取。</summary>
    public void SendAltSnapRestart()
        => SendAltSnapMessage(new PipeMessage { T = "restartAltSnap" });

    /// <summary>只切换总开关（不等待保存按钮）。</summary>
    public void SendAltSnapEnabled(bool enabled)
        => SendAltSnapMessage(new PipeMessage { T = "setAltSnapEnabled", Enable = enabled });

    private void SendAltSnapMessage(PipeMessage message)
    {
        if (!Connected || _pipe is null)
        {
            return;
        }
        var pipe = _pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, message, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    private void ApplyAltSnap(PipeMessage message)
    {
        CurrentAltSnapEnabled = message.AltSnapEnabled;
        CurrentAltSnapRunning = message.AltSnapRunning;
        CurrentAltSnapIniPath = message.AltSnapIniPath ?? "";
        CurrentAltSnapEngine = message.AltSnapEngine ?? "";
    }

    // ---- 心跳与重连 ----
    // Agent 重启/崩溃后，读循环会自然结束，但写操作可能静默失败；
    // 心跳每 10 秒探活，35 秒无任何回包视为断线，随后每 10 秒尝试重连一次。

    private void StartHeartbeat()
    {
        if (_disposed)
        {
            return;
        }
        _heartbeat?.Dispose();
        _heartbeat = new System.Threading.Timer(_ => HeartbeatTick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private void HeartbeatTick()
    {
        if (_disposed)
        {
            return;
        }
        if (!Connected)
        {
            TryReconnect();
            return;
        }
        if (DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(35))
        {
            Connected = false;
            var stale = _pipe;
            _pipe = null;
            try
            {
                stale?.Dispose();
            }
            catch
            {
                // 已释放
            }
            TryReconnect();
            return;
        }
        SendPing();
    }

    private void TryReconnect()
    {
        if (_disposed || Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                Connect(500);
            }
            catch
            {
                Connected = false;
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
        });
    }

    private void SendPing()
    {
        var pipe = _pipe;
        if (pipe is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "ping", Ver = PipeProtocol.Version }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Connected = false;
            }
        });
    }

    /// <summary>监听 Agent 推送：activate = 托盘又点了一次"打开"，把已有窗口置前。</summary>
    private async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        try
        {
            while (true)
            {
                var message = await PipeFrame.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }
                _lastActivity = DateTime.UtcNow;
                if (message.T == "pong")
                {
                    continue; // 心跳回包，无业务语义
                }
                if (message.T == "shutdown")
                {
                    // 不阻塞读取线程：界面忙时 Invoke 会卡住管道
                    Application.Current?.Dispatcher.BeginInvoke(App.ExitApp);
                    break;
                }
                if (message.T == "activate")
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (ActivateRequested is not null)
                        {
                            ActivateRequested();
                        }
                        else
                        {
                            (Application.Current.MainWindow as MainWindow)?.BringToFront();
                        }
                    });
                }
                if (message.T == "cleanState")
                {
                    CurrentLastCleanFreed = message.LastCleanFreed;
                    CurrentLastCleanAt = message.LastCleanAt ?? "";
                    CurrentLastCleanFailed = message.LastCleanFailed;
                }
                if (message.T == "skipUac")
                {
                    // Agent 侧安装失败回退时推回真实状态，界面开关跟着复位
                    CurrentSkipUac = message.Enable;
                    Application.Current?.Dispatcher.BeginInvoke(() => SkipUacChanged?.Invoke(message.Enable));
                }
                if (message.T == "altSnap")
                {
                    ApplyAltSnap(message);
                    Application.Current?.Dispatcher.BeginInvoke(() => AltSnapUpdated?.Invoke());
                }
                if (message.T == "inputTip")
                {
                    ApplyInputTip(message);
                    Application.Current?.Dispatcher.BeginInvoke(() => InputTipUpdated?.Invoke());
                }
            }
            Connected = false;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Connected = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _heartbeat?.Dispose();
        _heartbeat = null;
        Connected = false;
        _pipe?.Dispose();
        _pipe = null;
    }
}

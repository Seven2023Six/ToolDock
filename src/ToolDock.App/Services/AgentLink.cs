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

    /// <summary>Agent 下发的窗口材质（连接成功时有效），随设置变更更新。</summary>
    public string CurrentBackdrop { get; set; } = "Mica";

    /// <summary>Agent 下发的开机自启状态，随设置变更更新。</summary>
    public bool CurrentAutoStart { get; set; }

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NamedPipeClientStream? _pipe;

    public void Connect(int timeoutMilliseconds)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", "tooldock-agent", PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(timeoutMilliseconds);

            // 握手放到线程池并有界等待：绝不让 UI 线程无限等 IO
            var handshake = Task.Run(async () =>
            {
                await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "hello", Ver = 1 }, CancellationToken.None)
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
            _ = ReadLoopAsync(pipe);
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

    /// <summary>免 UAC 计划任务开关下发 Agent。</summary>
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
    public void SendCleanNow(bool confirmed)
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
                await PipeFrame.WriteAsync(pipe, new PipeMessage { T = "cleanNow", Confirmed = confirmed }, CancellationToken.None)
                    .ConfigureAwait(false);
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
                if (message.T == "skipUac")
                {
                    // Agent 侧安装失败回退时推回真实状态，界面开关跟着复位
                    CurrentSkipUac = message.Enable;
                    Application.Current?.Dispatcher.BeginInvoke(() => SkipUacChanged?.Invoke(message.Enable));
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
        Connected = false;
        _pipe?.Dispose();
        _pipe = null;
    }
}

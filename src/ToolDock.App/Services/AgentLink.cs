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
                if (message.T == "activate")
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        (Application.Current.MainWindow as MainWindow)?.BringToFront());
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

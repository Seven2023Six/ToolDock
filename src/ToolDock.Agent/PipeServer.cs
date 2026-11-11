using System.IO.Pipes;
using ToolDock.Core;

namespace ToolDock.Agent;

/// <summary>
/// 命名管道服务端（\\.\pipe\tooldock-agent）。
/// App 是短命进程：握手（hello → welcome），随后双向收发。
/// settings.json 的唯一写者在这里——App 的主题变更通过管道送达后落盘。
/// </summary>
internal sealed class PipeServer(AppSettings settings, string agentExePath, TrayIconController tray, MemoryCleanPlugin? memclean = null, AltSnapPlugin? altsnap = null, InputTipPlugin? inputtip = null)
{
    private const string PipeName = "tooldock-agent";

    private NamedPipeServerStream? _appConnection;
    private readonly object _gate = new();

    /// <summary>写界面连接的串行锁：push 状态、激活、退出通知可能来自不同线程，帧不能交错。</summary>
    private readonly SemaphoreSlim _pushGate = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch (IOException)
            {
                server.Dispose();
                continue;
            }
            _ = HandleClientAsync(server, cancellationToken);
        }
    }

    /// <summary>把状态推送回界面进程（界面被动更新，不做请求/应答）。</summary>
    private void PushState(PipeMessage message)
    {
        NamedPipeServerStream? app;
        lock (_gate)
        {
            app = _appConnection;
        }
        WriteToApp(app, message);
    }

    /// <summary>串行写一帧到界面连接；超时/断开静默放弃（界面是短命进程）。</summary>
    private void WriteToApp(NamedPipeServerStream? app, PipeMessage message, int timeoutMs = 800)
    {
        if (app is not { IsConnected: true })
        {
            return;
        }
        bool acquired = false;
        try
        {
            acquired = _pushGate.Wait(timeoutMs);
            if (!acquired)
            {
                return;
            }
            PipeFrame.WriteAsync(app, message, CancellationToken.None).Wait(timeoutMs);
        }
        catch
        {
            // 界面已退出/管道已断，无需处理
        }
        finally
        {
            if (acquired)
            {
                _pushGate.Release();
            }
        }
    }

    /// <summary>托盘「退出」时通知界面进程一起收尾（界面关窗只是隐藏，需要这条消息才会真退出）。</summary>
    public void NotifyShutdown()
    {
        NamedPipeServerStream? app;
        lock (_gate)
        {
            app = _appConnection;
        }
        WriteToApp(app, new PipeMessage { T = "shutdown" });
    }

    private void FillInputTip(PipeMessage message)
    {
        if (inputtip is null)
        {
            message.InputTipEnabled = false;
            message.InputTipRunning = false;
            return;
        }
        var (enabled, running, paused, state, configPath, engine) = inputtip.GetConfig();
        message.InputTipEnabled = enabled;
        message.InputTipRunning = running;
        message.InputTipPaused = paused;
        message.InputTipState = state;
        message.InputTipConfigPath = configPath;
        message.InputTipEngine = engine;
    }

    private void FillAltSnap(PipeMessage message)
    {
        if (altsnap is null)
        {
            message.AltSnapEnabled = false;
            message.AltSnapRunning = false;
            return;
        }
        var (enabled, running, iniPath, engine) = altsnap.GetConfig();
        message.AltSnapEnabled = enabled;
        message.AltSnapRunning = running;
        message.AltSnapIniPath = iniPath;
        message.AltSnapEngine = engine;
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientCoreAsync(server, cancellationToken);
        }
        catch (IOException)
        {
            // 管道断开属于常态（App 关窗即退）
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // 任何其他异常都要留痕：连接层问题不能无声消失
            LogFile.Write($"[pipe] 客户端处理异常：{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            // 连接结束时务必清空引用：否则死连接滞留会让后续新连接误判 duplicate
            lock (_gate)
            {
                if (ReferenceEquals(_appConnection, server))
                {
                    _appConnection = null;
                }
            }
            server.Dispose();
            MemoryTrim.TrimCurrent(); // 客户端断开后回收工作集
        }
    }

    private async Task HandleClientCoreAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var hello = await PipeFrame.ReadAsync(server, cancellationToken);
            if (hello is not { T: "hello" })
            {
                LogFile.Write($"[pipe] 客户端握手无效（hello={hello?.T ?? "null"}），断开");
                server.Dispose();
                return;
            }
            LogFile.Write($"[pipe] 客户端接入（协议 v{hello.Ver}）");

            PipeMessage welcome;
            NamedPipeServerStream? activateTarget;
            lock (_gate)
            {
                activateTarget = _appConnection is { IsConnected: true } ? _appConnection : null;
                if (activateTarget is not null)
                {
                    // 已有界面窗口：让旧实例置前，新实例拿 duplicate 标记自行退出
                    welcome = new PipeMessage { T = "welcome", Duplicate = true, Ver = PipeProtocol.Version };
                }
                else
                {
                    _appConnection = server;
                    welcome = new PipeMessage
                    {
                        T = "welcome",
                        Ver = PipeProtocol.Version,
                        Theme = settings.Theme,
                        Backdrop = settings.Backdrop,
                        AutoStart = settings.AutoStart,
                    };
                    if (memclean is not null)
                    {
                        var (mask, autoclean, threshold, interval, skipuac, allowfreezes, confirmclean, bubble, logres) = memclean.GetCleanState();
                        welcome.Mask = mask;
                        welcome.AutoClean = autoclean;
                        welcome.Threshold = threshold;
                        welcome.IntervalM = interval;
                        welcome.SkipUac = skipuac;
                        welcome.AllowFreezes = allowfreezes;
                        welcome.ConfirmClean = confirmclean;
                        welcome.BubbleResults = bubble;
                        var hotkey = memclean.GetHotkey();
                        welcome.HotkeyEnabled = hotkey.Enabled;
                        welcome.HotkeyMods = hotkey.Mods;
                        welcome.HotkeyVk = hotkey.Vk;
                        welcome.LogResults = logres;
                        var lastClean = memclean.GetLastClean();
                        welcome.LastCleanFreed = lastClean.Freed;
                        welcome.LastCleanAt = lastClean.At;
                        welcome.LastCleanFailed = lastClean.Failed;
                    }
                    FillAltSnap(welcome);
                    FillInputTip(welcome);
                }
            }
            // 激活旧界面在锁外串行写，避免与 push 状态帧交错
            if (activateTarget is not null)
            {
                WriteToApp(activateTarget, new PipeMessage { T = "activate" });
            }

            await PipeFrame.WriteAsync(server, welcome, cancellationToken);

            if (welcome.Duplicate)
            {
                server.Dispose();
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await PipeFrame.ReadAsync(server, cancellationToken);
                if (message is null)
                {
                    break; // App 正常退出
                }
                switch (message.T)
                {
                    case "ping":
                        // 心跳：App 定时探活，原样回 pong（不触发状态刷新）
                        await PipeFrame.WriteAsync(server, new PipeMessage { T = "pong", Ver = PipeProtocol.Version }, cancellationToken);
                        break;
                    case "getSettings":
                    {
                        var reply = new PipeMessage
                        {
                            T = "settings",
                            Ver = PipeProtocol.Version,
                            Theme = settings.Theme,
                            Backdrop = settings.Backdrop,
                            AutoStart = settings.AutoStart,
                        };
                        if (memclean is not null)
                        {
                            var (mask, autoclean, threshold, interval, skipuac, allowfreezes, confirmclean, bubble, logres) = memclean.GetCleanState();
                            reply.Mask = mask;
                            reply.AutoClean = autoclean;
                            reply.Threshold = threshold;
                            reply.IntervalM = interval;
                            reply.SkipUac = skipuac;
                            reply.AllowFreezes = allowfreezes;
                            reply.ConfirmClean = confirmclean;
                        reply.BubbleResults = bubble;
                        var hotkey2 = memclean.GetHotkey();
                        reply.HotkeyEnabled = hotkey2.Enabled;
                        reply.HotkeyMods = hotkey2.Mods;
                        reply.HotkeyVk = hotkey2.Vk;
                        reply.LogResults = logres;
                        var lastClean = memclean.GetLastClean();
                        reply.LastCleanFreed = lastClean.Freed;
                        reply.LastCleanAt = lastClean.At;
                        reply.LastCleanFailed = lastClean.Failed;
                        }
                        FillAltSnap(reply);
                        FillInputTip(reply);
                        await PipeFrame.WriteAsync(server, reply, cancellationToken);
                        break;
                    }
                    case "getAltSnap":
                    {
                        var reply = new PipeMessage { T = "altSnap", Ver = PipeProtocol.Version };
                        FillAltSnap(reply);
                        FillInputTip(reply);
                        await PipeFrame.WriteAsync(server, reply, cancellationToken);
                        break;
                    }
                    case "setAltSnap":
                        // 旧版界面会发这条消息；配置文件方式已取代逐项下发，忽略。
                        break;
                    case "getInputTip":
                    {
                        var reply = new PipeMessage { T = "inputTip", Ver = PipeProtocol.Version };
                        FillInputTip(reply);
                        await PipeFrame.WriteAsync(server, reply, cancellationToken);
                        break;
                    }
                    case "setInputTipEnabled":
                        inputtip?.SetEnabled(message.Enable);
                        break;
                    case "setInputTipPaused":
                        inputtip?.SetPaused(message.Enable);
                        break;
                    case "inputTipAction":
                        if (!string.IsNullOrWhiteSpace(message.Text))
                        {
                            inputtip?.RunTrigger(message.Text);
                        }
                        break;
                    case "restartInputTip":
                        _ = Task.Run(() =>
                        {
                            inputtip?.Reload();
                            var state = new PipeMessage { T = "inputTip", Ver = PipeProtocol.Version };
                            FillInputTip(state);
                            PushState(state);
                        });
                        break;
                    case "setAltSnapEnabled":
                        altsnap?.SetEnabled(message.Enable);
                        break;
                    case "restartAltSnap":
                        _ = Task.Run(() =>
                        {
                            altsnap?.RestartEngine();
                            var state = new PipeMessage { T = "altSnap", Ver = PipeProtocol.Version };
                            FillAltSnap(state);
                            PushState(state);
                        });
                        break;
                    case "setTheme":
                        if (message.Theme is "Light" or "Dark")
                        {
                            settings.Theme = message.Theme;
                            settings.Save();
                            LogFile.Write($"[pipe] 主题已切换并保存：{settings.Theme}");
                        }
                        break;
                    case "setBackdrop":
                        if (message.Backdrop is "Mica" or "Acrylic" or "Solid")
                        {
                            settings.Backdrop = message.Backdrop;
                            settings.Save();
                            LogFile.Write($"[pipe] 窗口材质已切换并保存：{settings.Backdrop}");
                        }
                        break;
                    case "setCleanCfg":
                        memclean?.ApplyConfig(message.Mask, message.AutoClean, message.Threshold, message.IntervalM, message.AllowFreezes, message.ConfirmClean, message.BubbleResults, message.LogResults);
                        LogFile.Write($"[pipe] 清理配置已更新：mask=0x{message.Mask:X} 自动={message.AutoClean} 阈值={message.Threshold}% 间隔={message.IntervalM}分 自动含冻结区={message.AllowFreezes} 清理确认={message.ConfirmClean} 气泡={message.BubbleResults} 日志={message.LogResults}");
                        break;
                    case "setSkipUac":
                        // 启动提权助手会请求一次管理员授权并等待管道就绪，放到后台避免堵住管道
                        LogFile.Write($"[pipe] 免UAC开关请求：{message.Enable}");
                        _ = Task.Run(() =>
                        {
                            bool ok = memclean?.SetSkipUac(message.Enable) ?? false;
                            bool actual = memclean?.SkipUacEnabled ?? false;
                            LogFile.Write($"[pipe] 免UAC开关结果：ok={ok} 当前={actual}");
                            // 无论成败都把真实状态推回界面，避免开关显示「开着却用不了」
                            PushState(new PipeMessage { T = "skipUac", Enable = actual });
                        });
                        break;
                    case "cleanNow":
                        LogFile.Write($"[pipe] 收到清理请求（confirmed={message.Confirmed}）");
                        memclean?.CleanNowExternal(message.Confirmed, message.Silent);
                        // 清理在后台线程执行（1-5 秒）：稍后把最近一次结果推回界面，避免"上次清理"要重开页面才更新
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(6000);
                            if (memclean is null)
                            {
                                return;
                            }
                            var (freed, at, failed) = memclean.GetLastClean();
                            PushState(new PipeMessage
                            {
                                T = "cleanState",
                                LastCleanFreed = freed,
                                LastCleanAt = at,
                                LastCleanFailed = failed,
                            });
                        });
                        break;
                    case "notify":
                        if (!string.IsNullOrWhiteSpace(message.Text))
                        {
                            tray.ShowBalloon(message.Title ?? "工具坞", message.Text);
                            LogFile.Write($"[pipe] 通知：{message.Title} / {message.Text}");
                        }
                        break;
                    case "batteryStatus":
                        tray.UpdateStatusLine("battery", string.IsNullOrWhiteSpace(message.Text) ? null : message.Text);
                        break;
                    case "setHotkey":
                        memclean?.SetHotkey(message.HotkeyEnabled, message.HotkeyMods, message.HotkeyVk);
                        LogFile.Write($"[pipe] 热键清理：{message.HotkeyEnabled} mods=0x{message.HotkeyMods:X} vk=0x{message.HotkeyVk:X}");
                        break;
                    case "setToolEnabled":
                        memclean?.SetEnabled(message.Enable);
                        LogFile.Write($"[pipe] 内存清理工具启用状态：{message.Enable}");
                        break;
                    case "setAutoStart":
                        // 用户在应用里关/开：开启时顺带清掉任务管理器的禁用标记
                        AutoStartManager.Apply(message.Enable, agentExePath, clearStartupApproval: message.Enable);
                        settings.AutoStart = message.Enable;
                        settings.Save();
                        LogFile.Write($"[pipe] 开机自启已更新：{message.Enable}");
                        break;
                }
            }
    }
}

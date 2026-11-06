using System.IO.Pipes;
using ToolDock.Core;

namespace ToolDock.Agent;

/// <summary>
/// 命名管道服务端（\\.\pipe\tooldock-agent）。
/// App 是短命进程：握手（hello → welcome），随后双向收发。
/// settings.json 的唯一写者在这里——App 的主题变更通过管道送达后落盘。
/// </summary>
internal sealed class PipeServer(AppSettings settings, string agentExePath, MemoryCleanPlugin? memclean = null)
{
    private const string PipeName = "tooldock-agent";

    private NamedPipeServerStream? _appConnection;
    private readonly object _gate = new();

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

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            var hello = await PipeFrame.ReadAsync(server, cancellationToken);
            if (hello is not { T: "hello" })
            {
                LogFile.Write($"[pipe] 客户端握手无效（hello={hello?.T ?? "null"}），断开");
                server.Dispose();
                return;
            }
            LogFile.Write("[pipe] 客户端接入");

            PipeMessage welcome;
            lock (_gate)
            {
                if (_appConnection is { IsConnected: true })
                {
                    // 已有界面窗口：让旧实例置前，新实例拿 duplicate 标记自行退出
                    _ = PipeFrame.WriteAsync(_appConnection, new PipeMessage { T = "activate" }, CancellationToken.None);
                    welcome = new PipeMessage { T = "welcome", Duplicate = true };
                }
                else
                {
                    _appConnection = server;
                    welcome = new PipeMessage
                    {
                        T = "welcome",
                        Theme = settings.Theme,
                        Backdrop = settings.Backdrop,
                        AutoStart = settings.AutoStart,
                    };
                    if (memclean is not null)
                    {
                        var (mask, autoclean, threshold, interval, skipuac) = memclean.GetCleanState();
                        welcome.Mask = mask;
                        welcome.AutoClean = autoclean;
                        welcome.Threshold = threshold;
                        welcome.IntervalM = interval;
                        welcome.SkipUac = skipuac;
                    }
                }
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
                    case "getSettings":
                    {
                        var reply = new PipeMessage
                        {
                            T = "settings",
                            Theme = settings.Theme,
                            Backdrop = settings.Backdrop,
                            AutoStart = settings.AutoStart,
                        };
                        if (memclean is not null)
                        {
                            var (mask, autoclean, threshold, interval, skipuac) = memclean.GetCleanState();
                            reply.Mask = mask;
                            reply.AutoClean = autoclean;
                            reply.Threshold = threshold;
                            reply.IntervalM = interval;
                            reply.SkipUac = skipuac;
                        }
                        await PipeFrame.WriteAsync(server, reply, cancellationToken);
                        break;
                    }
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
                        memclean?.ApplyConfig(message.Mask, message.AutoClean, message.Threshold, message.IntervalM, message.SkipUac);
                        LogFile.Write($"[pipe] 清理配置已更新：mask=0x{message.Mask:X} 自动={message.AutoClean} 阈值={message.Threshold}% 间隔={message.IntervalM}分 免UAC={message.SkipUac}");
                        break;
                    case "cleanNow":
                        memclean?.CleanNowExternal();
                        break;
                    case "setAutoStart":
                        AutoStartManager.Apply(message.Enable, agentExePath);
                        settings.AutoStart = message.Enable;
                        settings.Save();
                        LogFile.Write($"[pipe] 开机自启已更新：{message.Enable}");
                        break;
                }
            }
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
}

using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using ToolDock.Abstractions;
using ToolDock.Core;

namespace ToolDock.Agent;

/// <summary>
/// 常驻提权助手（--clean-daemon）与 Agent 之间的通道。
///
/// 设计要点：**管道服务端在 Agent（中完整性）一侧，提权助手作为客户端接入**。
/// Windows 的强制完整性策略禁止中完整性进程写高完整性对象，反过来高完整性写中完整性是允许的，
/// 所以由助手当客户端可以免去给管道设置私有安全描述符/完整性标签，稳定且不需要额外特权。
///
/// 相比计划任务：
/// - 不写系统任务库，安全软件不会把任务当成可疑启动项删除；
/// - 授权只在启动助手时发生一次，之后全部静默；
/// - 助手脱离 Agent 独立存活，重启界面/Agent 不会重复弹 UAC（会自动重连）。
/// </summary>
internal static class CleanerDaemon
{
    public const string ModeArg = "--clean-daemon";

    private const int DefaultTimeoutMs = 1500;
    private const int CleanTimeoutMs = 60000;
    private const int ConnectTimeoutMs = 2000;

    private static string? _userKey;

    public static string PipeName => "tooldock-cleaner." + UserKey();

    private static string MutexName => @"Local\ToolDock.CleanerDaemon." + UserKey();

    private static string UserKey()
    {
        if (_userKey is not null)
        {
            return _userKey;
        }
        string identity;
        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            identity = string.IsNullOrEmpty(sid) ? Environment.UserName : sid.Replace('-', '_');
        }
        catch
        {
            // 取不到 SID 就退回用户名
            identity = Environment.UserName;
        }
        // 同一用户的多个登录会话（控制台 + RDP）各自独立，避免管道名冲突
        int session = Process.GetCurrentProcess().SessionId;
        _userKey = identity + "_" + session.ToString(CultureInfo.InvariantCulture);
        return _userKey;
    }

    public readonly record struct DaemonResult(ulong FreedBytes, IReadOnlyList<CleanupCore.RegionResult> Regions);

    // ---- Agent 侧：管道服务端 + 请求 ----

    private static readonly object Gate = new();
    private static readonly object RequestGate = new();
    private static readonly ManualResetEventSlim Disconnected = new(false);
    private static NamedPipeServerStream? _server;
    private static StreamReader? _reader;
    private static StreamWriter? _writer;
    private static volatile bool _connected;
    private static volatile bool _started;

    /// <summary>启动接入线程（幂等）。必须在使用助手前调用。</summary>
    public static void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        var thread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "tooldock-cleaner-link",
        };
        thread.Start();
    }

    private static void AcceptLoop()
    {
        while (true)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                lock (Gate)
                {
                    _server = server;
                }
                server.WaitForConnection();

                var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                };
                lock (Gate)
                {
                    _reader = reader;
                    _writer = writer;
                    _connected = true;
                }
                LogFile.Write("[cleaner] 提权助手已接入");

                // 连接断开由请求失败触发；这里只等待，不主动读（读会抢走助手的响应）
                Disconnected.Wait();
                Disconnected.Reset();
            }
            catch (Exception exception)
            {
                LogFile.Write("[cleaner] 等待提权助手接入失败：" + exception.Message);
                Thread.Sleep(500);
            }
            finally
            {
                lock (Gate)
                {
                    _connected = false;
                    _reader = null;
                    _writer = null;
                    _server = null;
                }
                try
                {
                    server?.Dispose();
                }
                catch
                {
                    // 已释放
                }
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        try
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.CurrentUserOnly);
        }
        catch
        {
            // CurrentUserOnly 在某些系统/受限环境下不可用，退回默认 ACL（默认也是当前用户）
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.None);
        }
    }

    /// <summary>管道连接是否就绪（只判断状态，不发 ping）。</summary>
    public static bool IsConnected => _connected;

    /// <summary>探活：向助手发 ping，超时/断链会标记断开。</summary>
    public static bool IsRunning(int timeoutMs = DefaultTimeoutMs)
    {
        if (!_connected)
        {
            return false;
        }
        return Exchange("ping", expect_end: false, timeoutMs, out var lines, out _)
               && lines.Count > 0
               && lines[0] == "ok=1";
    }

    /// <summary>
    /// 确保助手在跑。allowPrompt=true 时若助手未接入会用 runas 启动（可能弹一次授权）。
    /// allowPrompt=false 只探测，不启动（避免开机阶段弹授权）。
    /// </summary>
    public static bool EnsureStarted(IAgentContext? context, bool allowPrompt, out string error)
    {
        error = "";
        if (IsRunning())
        {
            return true;
        }
        if (!allowPrompt)
        {
            error = "提权助手未运行";
            return false;
        }

        string? self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || !File.Exists(self))
        {
            error = "找不到主程序，无法启动提权助手";
            return false;
        }
        if (context is null)
        {
            error = "没有可用的提权接口";
            return false;
        }

        bool started = context.Elevation.RunElevatedAsync(self, ModeArg).GetAwaiter().GetResult();
        if (!started)
        {
            error = "管理员授权被取消或被安全软件拦截";
            return false;
        }

        for (int i = 0; i < 50; i++)
        {
            if (IsRunning(500))
            {
                return true;
            }
            Thread.Sleep(100);
        }

        error = "提权助手启动后无响应";
        return false;
    }

    /// <summary>请求关闭助手（关闭免 UAC 开关时调用）。</summary>
    public static void RequestStop()
    {
        if (!_connected)
        {
            return;
        }
        try
        {
            Exchange("quit", expect_end: false, 2000, out _, out _);
        }
        finally
        {
            MarkDisconnected();
        }
    }

    /// <summary>把清理请求发给助手并等待结果；超时/连接失败返回 false。</summary>
    public static bool TryClean(ulong mask, out DaemonResult result, out string error, int timeoutMs = CleanTimeoutMs)
    {
        result = new DaemonResult(0, Array.Empty<CleanupCore.RegionResult>());
        error = "";

        if (!_connected)
        {
            error = "提权助手未连接";
            return false;
        }

        if (!Exchange("clean " + mask.ToString(CultureInfo.InvariantCulture), expect_end: true, timeoutMs, out var lines, out error))
        {
            return false;
        }

        if (lines.Count == 0 || lines[0] != "ok=1")
        {
            error = lines.Count == 0 ? "提权助手无响应" : lines[0];
            return false;
        }

        ulong freed = 0;
        var regions = new List<CleanupCore.RegionResult>();
        foreach (var line in lines)
        {
            if (line.StartsWith("freed=", StringComparison.Ordinal) &&
                ulong.TryParse(line[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                freed = value;
            }
            else if (line.StartsWith("region=", StringComparison.Ordinal))
            {
                var parts = line[7..].Split('|');
                if (parts.Length == 2 && parts[1].StartsWith("status=0x", StringComparison.Ordinal) &&
                    uint.TryParse(parts[1][8..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var status))
                {
                    regions.Add(new CleanupCore.RegionResult(parts[0], status));
                }
            }
        }
        result = new DaemonResult(freed, regions);
        return true;
    }

    /// <summary>一次命令往返：写一行，读一行或读到 end；超时会断开管道让助手重连。</summary>
    private static bool Exchange(string command, bool expect_end, int timeoutMs, out List<string> lines, out string error)
    {
        lines = new List<string>();
        error = "";

        List<string>? output = null;
        string failure = "";
        var task = Task.Run(() =>
        {
            lock (RequestGate)
            {
                var writer = _writer;
                var reader = _reader;
                if (writer is null || reader is null)
                {
                    failure = "提权助手未连接";
                    return;
                }
                try
                {
                    writer.WriteLine(command);
                    if (command != "ping")
                    {
                        LogFile.Write($"[cleaner-link] 发送：{command}");
                    }
                    var collected = new List<string>();
                    while (true)
                    {
                        string? line = reader.ReadLine();
                        if (line is null)
                        {
                            failure = "提权助手连接已断开";
                            return;
                        }
                        collected.Add(line);
                        if (!expect_end || line == "end")
                        {
                            break;
                        }
                    }
                    output = collected;
                }
                catch (Exception exception)
                {
                    failure = exception.Message;
                }
            }
        });

        if (!task.Wait(timeoutMs))
        {
            LogFile.Write($"[cleaner-link] 等待响应超时：{command}");
            MarkDisconnected();
            error = "等待提权助手响应超时";
            return false;
        }
        if (output is { } collected)
        {
            if (command != "ping")
            {
                LogFile.Write($"[cleaner-link] 收到响应：{command} -> {collected.Count} 行");
            }
            lines = collected;
            return true;
        }
        LogFile.Write($"[cleaner-link] 交换失败：{command} -> {failure}");
        error = string.IsNullOrEmpty(failure) ? "提权助手未返回结果" : failure;
        MarkDisconnected();
        return false;
    }

    private static void MarkDisconnected()
    {
        NamedPipeServerStream? server;
        lock (Gate)
        {
            _connected = false;
            _reader = null;
            _writer = null;
            server = _server;
            _server = null;
        }
        // 只关底层流：阻塞中的 ReadLine 会以异常退出；不要在这里 Dispose StreamReader/Writer，
        // 它们正被读取线程使用，跨线程 Dispose 可能卡住。
        try
        {
            server?.Dispose();
        }
        catch
        {
            // 已释放
        }
        Disconnected.Set();
    }

    // ---- 提权助手进程：客户端 ----

    /// <summary>提权副本入口：连接 Agent 的管道并持续处理请求，直到 quit 或进程被杀。</summary>
    public static int Run()
    {
        if (!CleanupCore.IsElevated())
        {
            LogFile.Write("[cleaner] 助手需要管理员权限，退出");
            return 2;
        }

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, MutexName);
            try
            {
                if (!mutex.WaitOne(0))
                {
                    mutex.Dispose();
                    LogFile.Write("[cleaner] 已有助手在运行，本次退出");
                    return 0;
                }
            }
            catch (AbandonedMutexException)
            {
                // 上一个助手异常退出，接管
            }
        }
        catch (Exception exception)
        {
            LogFile.Write("[cleaner] 互斥量创建失败（继续运行）：" + exception.Message);
            mutex = null;
        }

        LogFile.Write("[cleaner] 提权助手已启动，等待连接 Agent");
        var last_connected = DateTime.UtcNow;
        try
        {
            while (true)
            {
                NamedPipeClientStream? client = null;
                try
                {
                    client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                    client.Connect(ConnectTimeoutMs);
                    last_connected = DateTime.UtcNow;

                    using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
                    using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true)
                    {
                        AutoFlush = true,
                    };
                    LogFile.Write("[cleaner] 已连接 Agent，等待清理请求");

                    string? command;
                    while ((command = reader.ReadLine()) is not null)
                    {
                        if (command == "ping")
                        {
                            writer.WriteLine("ok=1");
                            continue;
                        }
                        if (command == "quit")
                        {
                            writer.WriteLine("ok=1");
                            LogFile.Write("[cleaner] 收到退出指令，助手结束");
                            return 0;
                        }
                        if (command.StartsWith("clean", StringComparison.Ordinal))
                        {
                            HandleClean(command, writer);
                            continue;
                        }
                        writer.WriteLine("error=unknown");
                    }
                }
                catch (TimeoutException)
                {
                    // Agent 未就绪，稍后重试
                }
                catch (IOException)
                {
                    // 连接中断，稍后重连
                }
                catch (Exception exception)
                {
                    LogFile.Write("[cleaner] 连接异常：" + exception.Message);
                }
                finally
                {
                    try
                    {
                        client?.Dispose();
                    }
                    catch
                    {
                        // 已释放
                    }
                }

                Thread.Sleep(2000);

                // Agent 长时间不在（退出/被结束）就不再保留高权限进程
                if (DateTime.UtcNow - last_connected > TimeSpan.FromMinutes(30))
                {
                    LogFile.Write("[cleaner] 30 分钟未连接 Agent，助手退出");
                    return 0;
                }
            }
        }
        finally
        {
            if (mutex is not null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // 释放失败不影响退出
                }
                mutex.Dispose();
            }
        }
    }

    private static void HandleClean(string command, TextWriter writer)
    {
        ulong mask = CleanupCore.MaskDefault;
        int space = command.IndexOf(' ');
        if (space > 0 && ulong.TryParse(command[(space + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            mask = parsed;
        }

        try
        {
            var result = CleanupCore.Clean(mask);
            writer.WriteLine("ok=1");
            writer.WriteLine("freed=" + result.FreedBytes.ToString(CultureInfo.InvariantCulture));
            foreach (var region in result.Regions)
            {
                writer.WriteLine($"region={region.Name}|status=0x{region.Status:X8}");
            }
            writer.WriteLine("end");
            LogFile.Write($"[cleaner] 清理完成 mask=0x{mask:X} 释放 {result.FreedBytes / 1048576.0:0} MB");
        }
        catch (Exception exception)
        {
            writer.WriteLine("error=" + exception.Message);
            LogFile.Write("[cleaner] 清理异常：" + exception.Message);
        }
    }
}

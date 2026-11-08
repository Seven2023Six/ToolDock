using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ToolDock.App.Services;

/// <summary>
/// AltSnap 的进程与配置访问：便携目录下的 AltSnap.exe / AltSnap.ini（UTF-16LE）。
/// ini 读写走 Win32 Profile API，保留注释与编码；配置改动后重启进程生效。
/// </summary>
internal static class AltSnapService
{
    public const string ToolKey = "altsnap";

    public static string? ToolDir { get; } = ResolveDir();
    public static string? ExePath => ToolDir is null ? null : Path.Combine(ToolDir, "AltSnap.exe");
    public static string? IniPath => ToolDir is null ? null : Path.Combine(ToolDir, "AltSnap.ini");
    public static string? DefaultIniPath => ToolDir is null ? null : Path.Combine(ToolDir, "AltSnap.dni");

    public static bool Installed => ExePath is { } exe && File.Exists(exe);

    public static bool IsRunning => Process.GetProcessesByName("AltSnap").Length > 0;

    public static string Version
    {
        get
        {
            try
            {
                return ExePath is { } exe && File.Exists(exe)
                    ? FileVersionInfo.GetVersionInfo(exe).FileVersion ?? ""
                    : "";
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>发布布局 tools\altsnap；开发布局回退仓库 assets\tools\altsnap。</summary>
    private static string? ResolveDir()
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (string relative in new[] { "tools/altsnap", "assets/tools/altsnap" })
        {
            string dir = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(Path.Combine(dir, "AltSnap.exe")))
            {
                return dir;
            }
        }
        return null;
    }

    public static bool Start()
    {
        if (!Installed || IsRunning)
        {
            return IsRunning;
        }
        try
        {
            Process.Start(new ProcessStartInfo(ExePath!)
            {
                UseShellExecute = true,
                WorkingDirectory = ToolDir,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>向 AltSnap 的隐藏窗口发 WM_CLOSE（正常退出），超时再强杀。</summary>
    public static void Stop()
    {
        foreach (var process in Process.GetProcessesByName("AltSnap"))
        {
            try
            {
                IntPtr hwnd = FindWindowW("AltSnap", null);
                if (hwnd != IntPtr.Zero)
                {
                    PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程已退出/无权限：忽略
            }
        }
    }

    /// <summary>App 启动时按工具开关自动拉起。</summary>
    public static void StartIfEnabled()
    {
        try
        {
            if (UiSettings.Load().IsToolEnabled(ToolKey))
            {
                Start();
            }
        }
        catch
        {
            // 插片/启动阶段失败不影响主界面
        }
    }

    // ---- ini ----

    /// <summary>首次使用：把默认模板复制成可编辑的 AltSnap.ini（便携配置）。</summary>
    public static void EnsureIni()
    {
        try
        {
            if (IniPath is { } ini && DefaultIniPath is { } template &&
                !File.Exists(ini) && File.Exists(template))
            {
                File.Copy(template, ini);
            }
        }
        catch
        {
            // 目录不可写时由调用方提示
        }
    }

    public static string ReadIni(string section, string key, string fallback)
        => ReadProfile(IniPath, section, key, fallback);

    public static int ReadIniInt(string section, string key, int fallback)
        => int.TryParse(ReadIni(section, key, fallback.ToString()), out var value) ? value : fallback;

    public static string ReadDefault(string section, string key, string fallback)
        => ReadProfile(DefaultIniPath, section, key, fallback);

    public static void WriteIni(string section, string key, string value)
    {
        if (IniPath is null)
        {
            return;
        }
        WritePrivateProfileStringW(section, key, value, IniPath);
    }

    private static string ReadProfile(string? path, string section, string key, string fallback)
    {
        if (path is null || !File.Exists(path))
        {
            return fallback;
        }
        var buffer = new StringBuilder(4096);
        GetPrivateProfileStringW(section, key, fallback, buffer, (uint)buffer.Capacity, path);
        return buffer.ToString();
    }

    private const uint WM_CLOSE = 0x0010;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileStringW(
        string section, string key, string defaultValue, StringBuilder returned,
        uint size, string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileStringW(
        string section, string key, string value, string filePath);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}

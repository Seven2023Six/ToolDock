using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ToolDock.InputTip;

/// <summary>
/// 鼠标样式提示（移植自 InputTip cursor.ahk）：按状态切换整套系统光标，
/// 恢复时从注册表 HKCU\Control Panel\Cursors 读回原始光标。
/// </summary>
internal sealed class CursorService
{
    private sealed class CursorSlot
    {
        public required int Id { get; init; }
        public required string Origin { get; init; }
        public Dictionary<string, string> Paths { get; } = new();
    }

    private static readonly (string Name, int Id, string DefaultFile)[] Definitions =
    [
        ("ARROW", 32512, "aero_arrow.cur"),
        ("IBEAM", 32513, "beam_m.cur"),
        ("WAIT", 32514, "aero_busy.ani"),
        ("CROSSHAIR", 32515, "cross_m.cur"),
        ("UPARROW", 32516, "aero_up.cur"),
        ("SIZENWSE", 32642, "aero_nwse.cur"),
        ("SIZENESW", 32643, "aero_nesw.cur"),
        ("SIZEWE", 32644, "aero_ew.cur"),
        ("SIZENS", 32645, "aero_ns.cur"),
        ("SIZEALL", 32646, "aero_move.cur"),
        ("NO", 32648, "aero_unavail.cur"),
        ("HAND", 32649, "aero_link.cur"),
        ("APPSTARTING", 32650, "aero_working.ani"),
        ("HELP", 32651, "aero_helpsel.cur"),
        ("PIN", 32671, "aero_pin.cur"),
        ("PERSON", 32672, "aero_person.cur"),
        ("NWPEN", 32631, "aero_pen.cur"),
    ];

    private readonly InputTipSettings _settings;
    private readonly Dictionary<string, CursorSlot> _slots = new();
    private string _lastState = "";

    public CursorService(InputTipSettings settings)
    {
        _settings = settings;
        foreach (var (name, id, defaultFile) in Definitions)
        {
            string origin = ReadOrigin(name);
            if (string.IsNullOrWhiteSpace(origin) || !File.Exists(origin))
            {
                origin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", defaultFile);
            }
            _slots[name] = new CursorSlot { Id = id, Origin = origin };
        }
        Update();
    }

    private static string ReadOrigin(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
            string? value = key?.GetValue(name) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }
            return Environment.ExpandEnvironmentVariables(value);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>按当前配置重建 状态 → 光标文件 的映射。</summary>
    public void Update()
    {
        foreach (var slot in _slots.Values)
        {
            slot.Paths.Clear();
        }

        foreach (string state in InputTipStates.All)
        {
            string configured = _settings.CursorPaths.TryGetValue(state, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : "default-" + InputTipStates.ColorNames[state];

            string? directory = ResolveDirectory(configured);
            if (directory is null)
            {
                // 配置的目录不存在：回落到默认配色
                configured = "default-" + InputTipStates.ColorNames[state];
                _settings.CursorPaths[state] = configured;
                directory = ResolveDirectory(configured);
            }

            if (directory is null)
            {
                foreach (var slot in _slots.Values)
                {
                    slot.Paths[state] = slot.Origin;
                }
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.*"))
            {
                string name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                if (_slots.TryGetValue(name, out var slot))
                {
                    slot.Paths[state] = file;
                }
            }
        }
        _lastState = "";
    }

    private string? ResolveDirectory(string name)
    {
        string custom = Path.Combine(_settings.CursorDirectory, name);
        if (Directory.Exists(custom))
        {
            return custom;
        }
        string builtin = Path.Combine(_settings.AssetsDirectory, "default-cursor", name);
        if (Directory.Exists(builtin))
        {
            return builtin;
        }
        // 发布布局：默认光标与符号随工具箱放在 assets/inputtip 下，首次启动时复制
        return null;
    }

    /// <summary>把当前状态的光标应用到系统（change=true 时强制重载）。</summary>
    public void Load(string state, bool change = false)
    {
        if (!_settings.CursorActive || (state == _lastState && !change))
        {
            return;
        }
        if (_settings.LoadOnlyIBeamCursor)
        {
            if (_slots.TryGetValue("IBEAM", out var ibeam) && ibeam.Paths.TryGetValue(state, out var path))
            {
                ApplyCursor(path, ibeam.Id);
            }
        }
        else
        {
            foreach (var slot in _slots.Values)
            {
                if (slot.Paths.TryGetValue(state, out var path) && !string.IsNullOrWhiteSpace(path))
                {
                    ApplyCursor(path, slot.Id);
                }
            }
        }
        _lastState = state;
    }

    private static void ApplyCursor(string path, int id)
    {
        try
        {
            IntPtr cursor = Win32.LoadCursorFromFileW(path);
            if (cursor != IntPtr.Zero)
            {
                Win32.SetSystemCursor(cursor, id);
            }
        }
        catch
        {
        }
    }

    /// <summary>恢复系统原始光标（停用/暂停/退出时调用）。</summary>
    public void Revert()
    {
        foreach (var slot in _slots.Values)
        {
            if (!string.IsNullOrWhiteSpace(slot.Origin))
            {
                ApplyCursor(slot.Origin, slot.Id);
            }
        }
        _lastState = "";
    }

    /// <summary>可选的目录列表（设置页下拉）。</summary>
    public IReadOnlyList<string> ListDirectories()
    {
        var result = new List<string>();
        void Scan(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    bool hasCursor = Directory.EnumerateFiles(directory, "*.*")
                        .Any(f => Path.GetExtension(f) is ".cur" or ".ani");
                    if (hasCursor)
                    {
                        result.Add(Path.GetFileName(directory));
                    }
                }
            }
            catch
            {
            }
        }
        Scan(Path.Combine(_settings.AssetsDirectory, "default-cursor"));
        Scan(_settings.CursorDirectory);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

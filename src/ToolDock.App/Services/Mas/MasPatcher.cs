using System.Text;
using System.Text.RegularExpressions;

namespace ToolDock.App.Services.Mas;

/// <summary>
/// MAS 主菜单汉化补丁：只替换显示文案（echo/标题/提示语），不触碰命令与逻辑字符串。
/// 官方脚本原样保留在磁盘上，每次启动时对内存中的副本应用补丁；
/// 「检查更新」拉到新版后如果官方改了文案，缺失的替换项自动跳过（只记日志），不影响运行。
/// </summary>
internal static class MasPatcher
{
    /// <summary>
    /// 补丁版本号：写入汉化脚本首行标记（@::: ToolDock mas-cn/N）。
    /// 补丁表变化时递增，保证已缓存的汉化脚本被重新生成。
    /// </summary>
    internal const int PatchVersion = 1;

    internal const string HeaderMarker = "@::: ToolDock";

    internal static readonly (string From, string To)[] MenuPatch =
    {
        ("title  Microsoft %blank%Activation %blank%Scripts %masver%", "title  MAS 激活工具 %masver%"),
        ("title  Microsoft_Activation_Scripts %masver%", "title  MAS 激活工具 %masver%"),
        ("echo:                 Activation Methods:", "echo:                 激活方式："),
        ("call :dk_color3 %_White% \"             [1] \" %_Green% \"HWID\" %_White% \"                - Windows\"",
         "call :dk_color3 %_White% \"             [1] \" %_Green% \"HWID\" %_White% \"       - 永久激活 Windows\""),
        ("call :dk_color3 %_White% \"             [2] \" %_Green% \"Ohook\" %_White% \"               - Office\"",
         "call :dk_color3 %_White% \"             [2] \" %_Green% \"Ohook\" %_White% \"       - 永久激活 Office\""),
        ("call :dk_color3 %_White% \"             [3] \" %_Green% \"TSforge\" %_White% \"             - Windows / Office / ESU\"",
         "call :dk_color3 %_White% \"             [3] \" %_Green% \"TSforge\" %_White% \"       - 激活 Windows / Office / ESU\""),
        ("echo:             [1] HWID                - Windows", "echo:             [1] HWID       - 永久激活 Windows"),
        ("echo:             [2] Ohook               - Office", "echo:             [2] Ohook       - 永久激活 Office"),
        ("echo:             [3] TSforge             - Windows / Office / ESU", "echo:             [3] TSforge       - 激活 Windows / Office / ESU"),
        ("echo:             [4] Online KMS          - Windows / Office", "echo:             [4] Online KMS    - 周期激活 Windows / Office"),
        ("echo:             [5] Check Activation Status", "echo:             [5] 查看激活状态"),
        ("echo:             [6] Change Windows Edition", "echo:             [6] 更改 Windows 版本"),
        ("echo:             [7] Change Office Edition", "echo:             [7] 更改 Office 版本"),
        ("echo:             [8] Troubleshoot", "echo:             [8] 激活疑难解答"),
        ("echo:             [E] Extras", "echo:             [E] 附加工具"),
        ("echo:             [H] Help", "echo:             [H] 帮助"),
        ("echo:             [0] Exit", "echo:             [0] 退出"),
        ("call :dk_color2 %_Green% \"       Tip:\" %_White% \" To activate ESU updates after W10 EOL, use TSforge option.\"",
         "call :dk_color2 %_Green% \"       提示：\" %_White% \" Win10 停止支持后激活 ESU 更新请选 TSforge\""),
        ("Choose a menu option using your keyboard", "请用键盘选择菜单选项"),
    };

    /// <summary>应用补丁；返回命中的补丁条数，patched 为替换后的脚本正文。</summary>
    internal static int Apply(string payload, out string patched)
    {
        patched = payload;
        int applied = 0;
        foreach (var (from, to) in MenuPatch)
        {
            if (patched.Contains(from, StringComparison.Ordinal))
            {
                patched = patched.Replace(from, to, StringComparison.Ordinal);
                applied++;
            }
        }
        return applied;
    }

    /// <summary>生成最终写入磁盘的汉化脚本内容：首行补丁标记 + 补丁后的官方正文。</summary>
    internal static string BuildContent(string payload, out int applied)
    {
        applied = Apply(payload, out string patched);
        return HeaderMarker + " mas-cn/" + PatchVersion + "\r\n" + patched;
    }

    /// <summary>解析 @set masver=3.12 → "v3.12"；解析失败返回 null。</summary>
    internal static string? ParseVersion(string text)
    {
        var match = Regex.Match(text, @"@set\s+masver=([0-9][0-9.]*)");
        return match.Success ? "v" + match.Groups[1].Value : null;
    }

    /// <summary>GBK（cp936）：控制台默认代码页，汉化文案原生显示；提供程序不可用时回退 UTF-8。</summary>
    internal static Encoding GbkEncoding()
    {
        try
        {
            // 幂等注册：不依赖 MasService 静态构造的执行顺序（单元测试直接调用本类）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch (ArgumentException)
        {
            return new UTF8Encoding(false);
        }
    }
}

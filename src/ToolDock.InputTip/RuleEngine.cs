using System.Text.RegularExpressions;

namespace ToolDock.InputTip;

/// <summary>
/// 规则匹配（移植自 InputTip var.ahk 的 getMatchingRuleLists / matchWindowRules /
/// matchWindowDisplay）：进程精确匹配优先于正则，附加条件按长度加权，
/// 同一触发行为取优先级最高的一条。
/// </summary>
internal sealed class RuleEngine
{
    private readonly InputTipSettings _settings;
    private readonly Dictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);

    public RuleEngine(InputTipSettings settings)
    {
        _settings = settings;
    }

    public InputTipSettings Settings => _settings;

    public bool RegexMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }
        Regex? regex = GetRegex(pattern);
        if (regex is null)
        {
            return false;
        }
        try
        {
            return regex.IsMatch(input);
        }
        catch
        {
            return false;
        }
    }

    private Regex? GetRegex(string pattern)
    {
        if (_regexCache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }
        Regex? regex = null;
        try
        {
            string normalized = pattern;
            RegexOptions options = RegexOptions.None;
            // 兼容 AHK 风格的前缀：i) 不区分大小写、m) 多行
            while (normalized.Length > 2 && normalized[1] == ')' &&
                   "imnsx".Contains(char.ToLowerInvariant(normalized[0])))
            {
                char flag = char.ToLowerInvariant(normalized[0]);
                options |= flag switch
                {
                    'i' => RegexOptions.IgnoreCase,
                    'm' => RegexOptions.Multiline,
                    's' => RegexOptions.Singleline,
                    'x' => RegexOptions.IgnorePatternWhitespace,
                    _ => RegexOptions.None,
                };
                normalized = normalized[2..];
            }
            regex = new Regex(normalized, options | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            regex = null;
        }
        _regexCache[pattern] = regex;
        return regex;
    }

    public static bool IsStateCondition(string condition)
        => condition is "class" or "title" or "classAndTitle";

    public static bool IsMonitorCondition(string condition)
        => condition is "textMonitor" or "hotkeyMonitor" or "idleTimer";

    /// <summary>进程匹配：精确进程名优先，其次正则；空键表示"所有进程"的默认规则。</summary>
    private List<(InputTipRule Rule, double ProcessPriority)> GetCandidates(
        IEnumerable<InputTipRule> rules, string process, bool includeDefault = true)
    {
        var exact = new List<(InputTipRule, double)>();
        var pattern = new List<(InputTipRule, double)>();
        var fallback = new List<(InputTipRule, double)>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.Process))
            {
                if (includeDefault)
                {
                    fallback.Add((rule, 0));
                }
                continue;
            }
            if (string.Equals(rule.Process, process, StringComparison.OrdinalIgnoreCase))
            {
                exact.Add((rule, 100));
            }
            else if (RegexMatch(process, rule.Process))
            {
                pattern.Add((rule, 50 + rule.Process.Length / 2.0));
            }
        }
        exact.AddRange(pattern);
        exact.AddRange(fallback);
        return exact;
    }

    public bool MatchCondition(InputTipRule rule, WindowSnapshot snapshot)
    {
        return rule.Condition switch
        {
            "class" => RegexMatch(snapshot.Class, rule.Class) ||
                       (!string.IsNullOrEmpty(snapshot.ControlClass) &&
                        snapshot.ControlClass != snapshot.Class &&
                        RegexMatch(snapshot.ControlClass, rule.Class)),
            "title" => RegexMatch(snapshot.Title, rule.Title),
            "classAndTitle" => (RegexMatch(snapshot.Class, rule.Class) ||
                                (!string.IsNullOrEmpty(snapshot.ControlClass) &&
                                 snapshot.ControlClass != snapshot.Class &&
                                 RegexMatch(snapshot.ControlClass, rule.Class))) &&
                               RegexMatch(snapshot.Title, rule.Title),
            _ => true,
        };
    }

    /// <summary>
    /// 取某类规则中命中的"最佳"规则（每个触发行为一条）。
    /// 无条件规则只在窗口切换时生效，避免持续抢占条件规则。
    /// </summary>
    public List<InputTipRule> MatchWindowRules(IEnumerable<InputTipRule> rules, WindowSnapshot snapshot, bool hasProcessChange)
    {
        var result = new List<InputTipRule>();
        var candidates = GetCandidates(rules, snapshot.Process);
        if (candidates.Count == 0)
        {
            return result;
        }

        var bestConditional = new Dictionary<string, (InputTipRule Rule, double Priority)>(StringComparer.Ordinal);
        var bestUnconditional = new Dictionary<string, (InputTipRule Rule, double Priority)>(StringComparer.Ordinal);

        foreach (var (rule, processPriority) in candidates)
        {
            if (string.IsNullOrEmpty(rule.Trigger) || !MatchCondition(rule, snapshot))
            {
                continue;
            }
            if (IsMonitorCondition(rule.Condition))
            {
                continue;
            }

            double conditionPriority = rule.Condition switch
            {
                "classAndTitle" => 20 + rule.Class.Length + rule.Title.Length,
                "class" => 10 + rule.Class.Length,
                "title" => 10 + rule.Title.Length,
                _ => 0,
            };
            double total = processPriority + conditionPriority;
            var map = string.IsNullOrEmpty(rule.Condition) ? bestUnconditional : bestConditional;
            if (!map.TryGetValue(rule.Trigger, out var current) || total > current.Priority)
            {
                map[rule.Trigger] = (rule, total);
            }
        }

        foreach (var (trigger, value) in bestConditional)
        {
            if (trigger == "exit" && string.Equals(snapshot.Process, "explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result.Add(value.Rule);
        }
        if (hasProcessChange)
        {
            foreach (var (trigger, value) in bestUnconditional)
            {
                if (!bestConditional.ContainsKey(trigger))
                {
                    result.Add(value.Rule);
                }
            }
        }
        return result;
    }

    /// <summary>显示类规则（悬浮小窗/边框/符号）：任意命中即为 true。</summary>
    public bool MatchDisplay(IEnumerable<InputTipRule> rules, WindowSnapshot snapshot)
    {
        foreach (var (rule, _) in GetCandidates(rules, snapshot.Process))
        {
            if (!string.IsNullOrEmpty(rule.Trigger) && MatchCondition(rule, snapshot))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>该进程的规则列表（含默认规则），用于热键/监控/空闲计时器。</summary>
    public List<InputTipRule> ProcessRules(IEnumerable<InputTipRule> rules, string process, bool includeDefault)
        => GetCandidates(rules, process, includeDefault).Select(v => v.Rule).ToList();

    /// <summary>光标捕获模式链（Window.CaretSymbol.Rule 的 capture 触发）。</summary>
    public string GetCaptureChain(WindowSnapshot snapshot)
    {
        foreach (var rule in _settings.CaretSymbolRules)
        {
            if (!string.Equals(rule.Trigger, "capture", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.IsNullOrEmpty(rule.Process))
            {
                continue;
            }
            if (string.Equals(rule.Process, snapshot.Process, StringComparison.OrdinalIgnoreCase))
            {
                return rule.Capture;
            }
        }
        foreach (var rule in _settings.CaretSymbolRules)
        {
            if (!string.Equals(rule.Trigger, "capture", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(rule.Process))
            {
                continue;
            }
            if (RegexMatch(snapshot.Process, rule.Process))
            {
                return rule.Capture;
            }
        }
        return "";
    }

    public string GetCaptureOffset(WindowSnapshot snapshot)
    {
        foreach (var rule in _settings.CaretSymbolRules)
        {
            if (!string.Equals(rule.Trigger, "capture", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(rule.Process))
            {
                continue;
            }
            if (string.Equals(rule.Process, snapshot.Process, StringComparison.OrdinalIgnoreCase))
            {
                return rule.CaptureOffset;
            }
        }
        foreach (var rule in _settings.CaretSymbolRules)
        {
            if (!string.Equals(rule.Trigger, "capture", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(rule.Process))
            {
                continue;
            }
            if (RegexMatch(snapshot.Process, rule.Process))
            {
                return rule.CaptureOffset;
            }
        }
        return "";
    }

    /// <summary>窗口偏移规则（trigger=offset）命中的第一条。</summary>
    public InputTipRule? GetWindowOffsetRule(WindowSnapshot snapshot)
    {
        foreach (var (rule, _) in GetCandidates(_settings.CaretSymbolRules.Where(r =>
                     string.Equals(r.Trigger, "offset", StringComparison.OrdinalIgnoreCase)), snapshot.Process))
        {
            if (MatchCondition(rule, snapshot))
            {
                return rule;
            }
        }
        return null;
    }

    public static (int X, int Y) ParseScreenOffset(string text, int screenNumber)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (0, 0);
        }
        foreach (string part in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = part.Split('/');
            if (pieces.Length >= 3 && pieces[0].Trim() == screenNumber.ToString())
            {
                int.TryParse(pieces[1], out int x);
                int.TryParse(pieces[2], out int y);
                return (x, y);
            }
        }
        return (0, 0);
    }
}

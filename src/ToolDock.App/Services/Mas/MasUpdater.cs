using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ToolDock.App.Services.Mas;

/// <summary>
/// MAS 脚本更新：从官方引导脚本（get.activated.win）解析最新下载源与发布 SHA-256，
/// 多源随机尝试拉取，校验通过后替换本地运行时副本。官方引导格式变化时返回明确错误而不是乱写文件。
/// </summary>
internal static class MasUpdater
{
    internal const string BootstrapUrl = "https://get.activated.win";
    internal const string ProjectUrl = "https://massgrave.dev";

    /// <summary>
    /// 解析官方引导脚本中的 $URLs 与 $releaseHash。
    /// 返回 false 表示格式不符（引导脚本升级或下载到了错误内容）。
    /// </summary>
    internal static bool TryParseBootstrap(string bootstrap, out List<string> urls, out string releaseHash)
    {
        urls = new List<string>();
        releaseHash = "";

        var block = Regex.Match(bootstrap, @"\x24URLs\s*=\s*@\((?<body>[^)]*)\)", RegexOptions.Singleline);
        if (block.Success)
        {
            foreach (Match match in Regex.Matches(block.Groups["body"].Value, "'(?<u>https://[^']+)'"))
            {
                urls.Add(match.Groups["u"].Value);
            }
        }

        var hashMatch = Regex.Match(bootstrap, @"\x24releaseHash\s*=\s*'(?<h>[0-9A-Fa-f]{64})'");
        if (hashMatch.Success)
        {
            releaseHash = hashMatch.Groups["h"].Value;
        }

        return urls.Count > 0 && releaseHash.Length == 64;
    }

    internal static string Sha256(string text)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        return Convert.ToHexString(SHA256.HashData(bytes)).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// 检查并更新本地脚本。返回 (是否成功, 展示消息, 更新后的版本号)。
    /// 网络失败、哈希不符、引导格式变化都返回失败消息，不会留下半截文件。
    /// </summary>
    internal static async Task<(bool Ok, string Message, string? Version)> CheckUpdateAsync(
        string localScriptPath, string localDir, Action<string>? log = null)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ToolDock");

            string bootstrap = await http.GetStringAsync(BootstrapUrl);
            if (!TryParseBootstrap(bootstrap, out var urls, out string expected))
            {
                log?.Invoke("引导脚本解析失败：缺少 $URLs 或 $releaseHash");
                return (false, "无法解析官方引导脚本（发布格式可能已变更），请到 " + ProjectUrl + " 手动获取", null);
            }
            log?.Invoke($"引导解析：{urls.Count} 个源，期望哈希 {expected[..12]}…");

            string? payload = null;
            string? lastError = null;
            foreach (string url in urls.OrderBy(_ => Random.Shared.Next()))
            {
                try
                {
                    payload = await http.GetStringAsync(url);
                    break;
                }
                catch (Exception exception)
                {
                    lastError = exception.Message;
                }
            }
            if (payload is null)
            {
                log?.Invoke("所有官方源下载失败：" + lastError);
                return (false, $"官方源下载失败（{lastError}）。杀毒软件或防火墙可能拦截了下载，可将其加入信任后重试", null);
            }

            string hash = Sha256(payload);
            if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"哈希校验失败：{hash} != {expected}");
                return (false, $"哈希校验失败（{hash[..12]}…），已放弃更新", null);
            }

            string current = File.Exists(localScriptPath) ? Sha256(File.ReadAllText(localScriptPath)) : "";
            string? version = MasPatcher.ParseVersion(payload);
            if (string.Equals(hash, current, StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"已是最新版本（{ProjectUrl} {version}）", version);
            }

            Directory.CreateDirectory(localDir);
            await File.WriteAllTextAsync(localScriptPath, payload, new UTF8Encoding(false));
            log?.Invoke($"脚本已更新：{localScriptPath}（{version}）");
            return (true, $"已更新到 MAS {version}", version);
        }
        catch (Exception exception)
        {
            log?.Invoke("检查更新异常：" + exception.Message);
            return (false, $"检查更新失败：{exception.Message}。杀毒软件的网页防护可能拦截了 {BootstrapUrl}", null);
        }
    }
}

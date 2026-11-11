using System.IO;

namespace ToolDock.Core;

/// <summary>
/// 按大小轮转日志：超过上限就把当前文件改名为 .1（覆盖旧 .1）。
/// 多进程同时写同一日志时，轮转竞态最坏只会让新进程重开一个文件，不会丢已写内容。
/// </summary>
public static class LogRotation
{
    public const long DefaultMaxBytes = 4 * 1024 * 1024;

    public static void Prepare(string path, long maxBytes = DefaultMaxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= maxBytes)
            {
                return;
            }
            string archive = path + ".1";
            try
            {
                File.Delete(archive);
            }
            catch
            {
                // 旧归档删不掉就覆盖失败，保留当前文件继续写
                return;
            }
            File.Move(path, archive);
        }
        catch
        {
            // 轮转失败不影响写日志
        }
    }
}

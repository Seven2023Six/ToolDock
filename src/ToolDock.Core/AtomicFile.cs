using System.IO;

namespace ToolDock.Core;

/// <summary>
/// 原子写文本文件：先写同目录临时文件再替换，避免进程崩溃/并发读时读到半截 JSON。
/// （提权助手会读插件的 memclean.json 决定清理掩码，半截文件会退回默认值，语义就变了。）
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}

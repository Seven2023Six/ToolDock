namespace ToolDock.Core;

/// <summary>
/// 管道协议版本。App 与 Agent 各自独立发布，握手时交换版本号：
/// 高版本新增字段时低版本必须容忍未知成员（JSON 反序列化默认忽略），
/// 版本号只用于诊断与未来做能力协商，不做强制拒绝。
/// </summary>
public static class PipeProtocol
{
    public const int Version = 2;
}

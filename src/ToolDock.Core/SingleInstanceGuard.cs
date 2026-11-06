namespace ToolDock.Core;

/// <summary>单实例互斥。防止双开出现两个托盘图标；第二个实例直接退出。</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    public SingleInstanceGuard(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        Acquired = createdNew;
    }

    /// <summary>是否成功取得唯一实例身份。false = 已有实例在跑。</summary>
    public bool Acquired { get; }

    public void Dispose()
    {
        if (Acquired)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* 非持有线程释放时忽略 */ }
        }
        _mutex.Dispose();
    }
}

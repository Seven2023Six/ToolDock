using System.Windows.Threading;

namespace ToolDock.App.Services;

/// <summary>把 WinUI 的 DispatcherQueue.TryEnqueue 语义映射到 WPF Dispatcher。</summary>
internal static class DispatcherCompat
{
    public static bool TryEnqueue(this Dispatcher dispatcher, Action action)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }
        dispatcher.BeginInvoke(action);
        return true;
    }
}

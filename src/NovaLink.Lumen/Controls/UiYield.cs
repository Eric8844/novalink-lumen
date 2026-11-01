using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// 让出一帧：把后续工作排到下一个 UI 空闲时机执行。
/// 用来把大段 UI 构建切成小批，避免长时间占住 UI 线程（背景动画会跟着卡住）。
/// </summary>
public static class UiYield
{
    public static Task NextFrameAsync()
    {
        var tcs = new TaskCompletionSource();
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null) { tcs.SetResult(); return tcs.Task; }
        if (!queue.TryEnqueue(DispatcherQueuePriority.Low, () => tcs.TrySetResult()))
            tcs.TrySetResult();
        return tcs.Task;
    }
}

using NovaLink.Lumen.Core.Plugins;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 交给插件的上下文实现：日志、设置、宿主能力，外加事件订阅登记。
/// 事件分发点在这里做异常隔离 —— 插件事件处理器抛出的异常只记日志，绝不冒泡到宿主调用栈。
/// </summary>
public sealed class PluginContext : IPluginContext
{
    private readonly Dictionary<PluginEvent, List<Action<PluginEventArgs>>> _handlers = [];
    private readonly object _gate = new();

    public PluginContext(string pluginId, IPluginLogger log, IPluginSettings settings, IPluginHost host, IPluginUi ui)
    {
        PluginId = pluginId;
        Log = log;
        Settings = settings;
        Host = host;
        Ui = ui;
    }

    public string PluginId { get; }

    public IPluginLogger Log { get; }

    public IPluginSettings Settings { get; }

    public IPluginHost Host { get; }

    public IPluginUi Ui { get; }

    /// <summary>登记一个事件处理器；Dispose 返回值即退订。</summary>
    public IDisposable Subscribe(PluginEvent pluginEvent, Action<PluginEventArgs> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        lock (_gate)
        {
            if (!_handlers.TryGetValue(pluginEvent, out var list))
            {
                list = [];
                _handlers[pluginEvent] = list;
            }
            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(pluginEvent, out var list)) list.Remove(handler);
            }
        });
    }

    /// <summary>该事件是否有插件订阅（宿主可据此跳过无意义的参数组装）。</summary>
    public bool HasSubscribers(PluginEvent pluginEvent)
    {
        lock (_gate) return _handlers.TryGetValue(pluginEvent, out var list) && list.Count > 0;
    }

    /// <summary>分发事件；单个处理器异常只记日志。</summary>
    public void Dispatch(PluginEvent pluginEvent, PluginEventArgs args)
    {
        Action<PluginEventArgs>[] snapshot;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(pluginEvent, out var list) || list.Count == 0) return;
            snapshot = list.ToArray();
        }

        foreach (var handler in snapshot)
        {
            try
            {
                handler(args);
            }
            catch (Exception ex)
            {
                Log.Error("事件 " + pluginEvent + " 的处理器抛出异常，已隔离", ex);
            }
        }
    }

    /// <summary>清空全部订阅（插件关闭时调用）。</summary>
    public void Clear()
    {
        lock (_gate) _handlers.Clear();
    }

    /// <summary>退订令牌。</summary>
    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

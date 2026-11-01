using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services.Hooks;

namespace NovaLink.Lumen.Services.Plugins.Scripting;

/// <summary>脚本插件可注册的钩子类别。</summary>
internal enum ScriptHookKind
{
    /// <summary>下载源改写（对应 HookRegistry.DownloadResolve）。</summary>
    DownloadRequest,

    /// <summary>文本替换（对应 HookRegistry.TextReplace）。</summary>
    TextReplace,

    /// <summary>通知过滤（对应 HookRegistry.NotificationFilter）。</summary>
    NotificationFilter
}

/// <summary>脚本 onDownloadRequest 回调的返回值（已校验过的字段）。</summary>
internal sealed class ScriptDownloadResult
{
    internal string Url = "";

    internal string? DisplayName;

    internal string? Sha1;

    internal IReadOnlyDictionary<string, string>? Headers;
}

/// <summary>
/// 单条脚本钩子注册：保存注册 id、插件归属与隔离过的回调。
/// 回调异常在这里统一吞掉并记日志，绝不向下载/文本/通知调用链抛异常。
/// </summary>
internal sealed class ScriptHookRegistration
{
    internal int Id { get; set; }

    internal string PluginId { get; init; } = "";

    internal ScriptHookKind Kind { get; init; }

    internal Func<DownloadRequest, ScriptDownloadResult?>? DownloadHandler { get; init; }

    internal Func<string, string?>? TextHandler { get; init; }

    internal Func<string, string, bool?>? NotificationHandler { get; init; }

    internal IPluginLogger? Log { get; init; }

    internal ScriptDownloadResult? InvokeDownload(DownloadRequest request)
    {
        try
        {
            return DownloadHandler?.Invoke(request);
        }
        catch (Exception ex)
        {
            Report("onDownloadRequest", ex);
            return null;
        }
    }

    internal string? InvokeText(string text)
    {
        try
        {
            return TextHandler?.Invoke(text);
        }
        catch (Exception ex)
        {
            Report("onTextReplace", ex);
            return null;
        }
    }

    internal bool? InvokeNotification(string title, string message)
    {
        try
        {
            return NotificationHandler?.Invoke(title, message);
        }
        catch (Exception ex)
        {
            Report("onNotification", ex);
            return null;
        }
    }

    private void Report(string hook, Exception ex)
    {
        try
        {
            Log?.Error("插件钩子异常已隔离（" + hook + "）：" + ex.Message, ex);
        }
        catch
        {
            // 日志都失败时不再向外抛。
        }
    }
}

/// <summary>
/// 脚本钩子的多播管理器：HookRegistry 的 DownloadResolve / TextReplace / NotificationFilter 都是静态单播委托，
/// 多个脚本插件同时注册会互相覆盖。这里用"注册表 + 组合委托"接管：第一次注册时把 HookRegistry 的原委托记下来，
/// 之后的调用按注册顺序链式叠加；最后一个脚本注销时把 HookRegistry 还原成原委托。
/// 所有组合都做异常隔离（单个脚本抛异常只记日志，不影响其它脚本与宿主）。
/// </summary>
internal static class ScriptHookBus
{
    private const string LogArea = "plugins";

    private static readonly object Gate = new();
    private static readonly List<ScriptHookRegistration> Registrations = [];
    private static int _nextId;

    private static Func<DownloadRequest, CancellationToken, Task<DownloadSource?>>? _installedDownload;
    private static Func<DownloadRequest, CancellationToken, Task<DownloadSource?>>? _previousDownload;
    private static Func<string, string>? _installedText;
    private static Func<string, string>? _previousText;
    private static Func<string, string, bool>? _installedNotification;
    private static Func<string, string, bool>? _previousNotification;

    /// <summary>下载开始解析前触发（含脚本自己的 download.before 订阅者）。</summary>
    internal static event Action<DownloadRequest>? DownloadBefore;

    /// <summary>下载源确定后触发（source 为 null 表示没有钩子接管）。</summary>
    internal static event Action<DownloadSource?>? DownloadAfter;

    /// <summary>分配全局唯一的注册 id（钩子与事件共用，保证 lumen.hooks.off / lumen.events.off 不串号）。</summary>
    internal static int AllocateId() => Interlocked.Increment(ref _nextId);

    /// <summary>注册一条脚本钩子，返回注册 id；首次注册某类别时接管 HookRegistry 对应委托。</summary>
    internal static int Register(ScriptHookRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (Gate)
        {
            if (registration.Id <= 0) registration.Id = AllocateId();
            Registrations.Add(registration);
            switch (registration.Kind)
            {
                case ScriptHookKind.DownloadRequest:
                    if (_installedDownload is null)
                    {
                        _previousDownload = HookRegistry.DownloadResolve;
                        _installedDownload = CompositeDownload;
                        HookRegistry.DownloadResolve = _installedDownload;
                    }
                    break;
                case ScriptHookKind.TextReplace:
                    if (_installedText is null)
                    {
                        _previousText = HookRegistry.TextReplace;
                        _installedText = CompositeText;
                        HookRegistry.TextReplace = _installedText;
                    }
                    break;
                case ScriptHookKind.NotificationFilter:
                    if (_installedNotification is null)
                    {
                        _previousNotification = HookRegistry.NotificationFilter;
                        _installedNotification = CompositeNotification;
                        HookRegistry.NotificationFilter = _installedNotification;
                    }
                    break;
            }
            return registration.Id;
        }
    }

    /// <summary>注销一条钩子；该类别最后一个脚本注销时把 HookRegistry 还原成接管前的委托。</summary>
    internal static bool Unregister(int id)
    {
        lock (Gate)
        {
            var registration = Registrations.FirstOrDefault(r => r.Id == id);
            if (registration is null) return false;
            Registrations.Remove(registration);
            if (!Registrations.Any(r => r.Kind == registration.Kind)) RestoreLocked(registration.Kind);
            return true;
        }
    }

    private static string CompositeText(string text)
    {
        ScriptHookRegistration[] snapshot;
        lock (Gate) snapshot = Registrations.Where(r => r.Kind == ScriptHookKind.TextReplace).ToArray();

        var current = text;
        foreach (var registration in snapshot)
        {
            var next = registration.InvokeText(current);
            if (next is not null) current = next;
        }

        Func<string, string>? previous;
        lock (Gate) previous = _previousText;
        if (previous is null) return current;
        try
        {
            return previous(current) ?? current;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "上一个文本替换钩子抛出异常，已忽略：" + ex.Message);
            return current;
        }
    }

    private static async Task<DownloadSource?> CompositeDownload(DownloadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RaiseDownloadBefore(request);

        ScriptHookRegistration[] snapshot;
        lock (Gate) snapshot = Registrations.Where(r => r.Kind == ScriptHookKind.DownloadRequest).ToArray();

        foreach (var registration in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = registration.InvokeDownload(request);
            if (result is null) continue;

            var source = new DownloadSource(
                string.IsNullOrWhiteSpace(result.Url) ? request.Url : result.Url,
                result.Headers,
                result.Sha1 ?? request.Sha1,
                result.DisplayName);
            RaiseDownloadAfter(source);
            return source;
        }

        Func<DownloadRequest, CancellationToken, Task<DownloadSource?>>? previous;
        lock (Gate) previous = _previousDownload;
        DownloadSource? resolved = null;
        if (previous is not null)
        {
            try
            {
                resolved = await previous(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogArea, "上一个下载钩子抛出异常，已忽略：" + ex.Message);
            }
        }
        RaiseDownloadAfter(resolved);
        return resolved;
    }

    private static bool CompositeNotification(string title, string message)
    {
        ScriptHookRegistration[] snapshot;
        lock (Gate) snapshot = Registrations.Where(r => r.Kind == ScriptHookKind.NotificationFilter).ToArray();

        var allowed = true;
        foreach (var registration in snapshot)
        {
            var decision = registration.InvokeNotification(title, message);
            if (decision is false) allowed = false;
        }

        Func<string, string, bool>? previous;
        lock (Gate) previous = _previousNotification;
        if (previous is null) return allowed;
        try
        {
            if (!previous(title, message)) allowed = false;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "上一个通知过滤钩子抛出异常，已忽略：" + ex.Message);
        }
        return allowed;
    }

    private static void RaiseDownloadBefore(DownloadRequest request)
    {
        try
        {
            DownloadBefore?.Invoke(request);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "download.before 事件分发失败：" + ex.Message);
        }
    }

    private static void RaiseDownloadAfter(DownloadSource? source)
    {
        try
        {
            DownloadAfter?.Invoke(source);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "download.after 事件分发失败：" + ex.Message);
        }
    }

    private static void RestoreLocked(ScriptHookKind kind)
    {
        switch (kind)
        {
            case ScriptHookKind.DownloadRequest:
                if (ReferenceEquals(HookRegistry.DownloadResolve, _installedDownload))
                    HookRegistry.DownloadResolve = _previousDownload;
                _installedDownload = null;
                _previousDownload = null;
                break;
            case ScriptHookKind.TextReplace:
                if (ReferenceEquals(HookRegistry.TextReplace, _installedText))
                    HookRegistry.TextReplace = _previousText;
                _installedText = null;
                _previousText = null;
                break;
            case ScriptHookKind.NotificationFilter:
                if (ReferenceEquals(HookRegistry.NotificationFilter, _installedNotification))
                    HookRegistry.NotificationFilter = _previousNotification;
                _installedNotification = null;
                _previousNotification = null;
                break;
        }
    }
}

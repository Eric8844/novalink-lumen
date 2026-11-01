using NovaLink.Lumen.Core.Plugins;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>把插件日志写到宿主统一日志（logs\lumen-yyyyMMdd.log），区域名固定为 plugin:&lt;id&gt;。</summary>
public sealed class PluginLogger : IPluginLogger
{
    private readonly string _area;

    public PluginLogger(string pluginId) => _area = "plugin:" + pluginId;

    /// <summary>日志区域名（如 plugin:example.hello）。</summary>
    public string Area => _area;

    public void Info(string message) => Write(() => AppLog.Info(_area, message ?? ""));

    public void Warn(string message) => Write(() => AppLog.Warn(_area, message ?? ""));

    public void Error(string message, Exception? exception = null)
        => Write(() => AppLog.Error(_area, message ?? "", exception));

    /// <summary>日志自身失败不能反过来影响插件（AppLog 未初始化等场景）。</summary>
    private static void Write(Action action)
    {
        try { action(); } catch { }
    }
}

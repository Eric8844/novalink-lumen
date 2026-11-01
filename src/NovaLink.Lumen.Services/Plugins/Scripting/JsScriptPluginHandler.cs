using Jint;
using NovaLink.Lumen.Core.Plugins;

namespace NovaLink.Lumen.Services.Plugins.Scripting;

/// <summary>
/// 类型 B（免编译脚本插件）的运行时：一个 script 插件对应一个 Jint 引擎与一个 <see cref="ScriptHostApi"/>。
/// Initialize 时按清单里的 scripts 列表（when=startup、engine=jint）同步执行顶层脚本；
/// Shutdown / 卸载时注销该脚本注册的所有钩子与事件，并释放引擎。
/// </summary>
internal sealed class JsScriptPluginHandler : ILumenPlugin, IDisposable
{
    private const string LogArea = "plugins";

    private readonly PluginDescriptor _descriptor;
    private readonly string _dataRoot;
    private ScriptHostApi? _api;
    private bool _initialized;

    internal JsScriptPluginHandler(PluginDescriptor descriptor, string dataRoot)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _dataRoot = dataRoot ?? "";
    }

    /// <summary>当前注册的钩子数量（诊断/测试用）。</summary>
    internal int HookCount => _api?.HookCount ?? 0;

    /// <summary>当前注册的事件数量（诊断/测试用）。</summary>
    internal int EventCount => _api?.EventCount ?? 0;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_initialized) return;
        _initialized = true;

        var manifest = _descriptor.Manifest
                       ?? throw new InvalidOperationException("插件清单为空，无法加载脚本插件。");
        if (manifest.Scripts.Count == 0)
            throw new InvalidOperationException("kind=script 的插件必须提供非空的 scripts 列表。");

        var dataDirectory = ResolveDataDirectory(manifest.Id);
        var engine = ScriptEngineFactory.Create();
        var api = new ScriptHostApi(engine, manifest.Id, manifest.Version, dataDirectory, context.Log, context.Settings);
        try
        {
            foreach (var script in manifest.Scripts)
            {
                if (script is null || string.IsNullOrWhiteSpace(script.File)) continue;

                if (!IsStartup(script.When))
                {
                    AppLog.Warn(LogArea, "插件 " + manifest.Id + " 的脚本 " + script.File
                        + " 的 when=\"" + script.When + "\" 暂不支持，已跳过（V1.4.1 仅支持 startup）。");
                    continue;
                }

                if (!IsJint(script.Engine))
                    throw new InvalidOperationException("不支持的脚本引擎 \"" + script.Engine + "\"，当前仅支持 jint。");

                var filePath = PluginManifestReader.ResolveInside(_descriptor.Directory, script.File)
                               ?? throw new InvalidOperationException("脚本文件不在插件目录内：" + script.File);
                if (!File.Exists(filePath))
                    throw new FileNotFoundException("脚本文件不存在：" + script.File, filePath);

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > ScriptEngineFactory.MaxScriptFileBytes)
                    throw new InvalidOperationException("脚本文件过大（上限 2 MB）：" + script.File);

                try
                {
                    api.RunScriptFile(filePath, script.File);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(BuildFailureMessage(script.File, ex), ex);
                }
            }
        }
        catch
        {
            api.Dispose();
            throw;
        }

        _api = api;
    }

    public void Shutdown()
    {
        var api = Interlocked.Exchange(ref _api, null);
        api?.Dispose();
    }

    public void Dispose() => Shutdown();

    private string ResolveDataDirectory(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(_dataRoot))
            throw new InvalidOperationException("插件数据根目录未配置，无法为脚本插件创建独立数据目录。");

        var directory = Path.Combine(_dataRoot, PluginPaths.PluginDataFolderName, pluginId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsStartup(string? when) =>
        string.IsNullOrWhiteSpace(when) || string.Equals(when.Trim(), "startup", StringComparison.OrdinalIgnoreCase);

    private static bool IsJint(string? engine) =>
        string.IsNullOrWhiteSpace(engine) || string.Equals(engine.Trim(), "jint", StringComparison.OrdinalIgnoreCase);

    private static string BuildFailureMessage(string scriptFile, Exception exception)
    {
        var reason = IsSyntaxError(exception) ? "脚本编译失败：" : "脚本执行失败：";
        return reason + scriptFile + "（" + exception.Message + "）";
    }

    /// <summary>
    /// 区分语法错误与运行期错误：Jint 解析失败抛出的异常消息里带 SyntaxError / Unexpected token，
    /// 这样日志关键字（脚本编译失败 / 脚本执行失败）对文档与排查都稳定。
    /// </summary>
    private static bool IsSyntaxError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("SyntaxError", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase)
                || message.Contains("解析", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

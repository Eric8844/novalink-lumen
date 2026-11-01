using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services.Hooks;

namespace NovaLink.Lumen.Services.Plugins.Scripting;

/// <summary>脚本事件订阅记录（app.startup / app.shutdown / download.before / download.after）。</summary>
internal sealed class ScriptEventRegistration
{
    internal int Id { get; init; }

    internal string Name { get; init; } = "";

    internal JsValue Handler { get; init; } = JsValue.Undefined;
}

/// <summary>
/// 脚本插件宿主 API 门面：把一个冻结的全局对象 <c>lumen</c> 暴露给 JS，并把钩子/事件/设置/日志
/// 全部转到宿主侧实现。要点：
/// 1. 引擎关闭 CLR 互操作，只通过 SetValue 暴露少量委托，桥接完成后立即把 __lumen* 全局置为 undefined；
/// 2. 每次执行脚本或回调前重置 Jint 约束（语句数/内存/超时）；
/// 3. 所有回调异常都在这里捕获并记日志，绝不让异常穿到下载/文本/通知调用链。
/// </summary>
internal sealed class ScriptHostApi : IDisposable
{
    private const int MaxLogLength = 2000;
    private const int MaxSettingsKeyLength = 64;
    private const int MaxSettingsValueBytes = 8192;
    private const int MaxHookTextLength = 1_000_000;

    private static readonly string[] SupportedEvents =
    [
        "app.startup", "app.shutdown", "download.before", "download.after"
    ];

    private readonly Engine _engine;
    private readonly IPluginLogger _log;
    private readonly IPluginSettings _settings;
    private readonly object _gate = new();
    private readonly List<ScriptHookRegistration> _hooks = [];
    private readonly List<ScriptEventRegistration> _events = [];
    private readonly HashSet<string> _subscribedEvents = new(StringComparer.Ordinal);
    private readonly JsValue _stringify;
    private readonly JsValue _parseJson;
    private readonly JsValue _freeze;
    private readonly JsValue _createObject;
    private EventHandler<PluginEventArgs>? _appStartupHandler;
    private EventHandler<PluginEventArgs>? _appShutdownHandler;
    private Action<DownloadRequest>? _downloadBeforeHandler;
    private Action<DownloadSource?>? _downloadAfterHandler;
    private bool _disposed;

    internal ScriptHostApi(
        Engine engine,
        string pluginId,
        string pluginVersion,
        string dataDir,
        IPluginLogger log,
        IPluginSettings settings)
    {
        _engine = engine;
        _log = log;
        _settings = settings;

        // 预先编译几个工具函数，避免每次调用都重新解析源码。
        _stringify = engine.Evaluate("(function(v){ try { var s = JSON.stringify(v); return s === undefined ? null : s; } catch (e) { return null; } })");
        _parseJson = engine.Evaluate("(function(s){ try { return JSON.parse(s); } catch (e) { return s; } })");
        _freeze = engine.Evaluate("(function(o){ try { return Object.freeze(o); } catch (e) { return o; } })");
        _createObject = engine.Evaluate("(function(){ return {}; })");

        engine.SetValue("__lumenLog", (Action<int, JsValue>)((level, message) => WriteLog(level, message)));
        engine.SetValue("__lumenSettingGet", (Func<string, JsValue>)(key => ReadSetting(key)));
        engine.SetValue("__lumenSettingSet", (Action<string, JsValue>)((key, value) => WriteSetting(key, value)));
        engine.SetValue("__lumenHook", (Func<string, JsValue, double>)((kind, handler) => RegisterHook(kind, handler)));
        engine.SetValue("__lumenEvent", (Func<string, JsValue, double>)((name, handler) => RegisterEvent(name, handler)));
        engine.SetValue("__lumenOff", (Action<double>)(id => Off(id)));
        engine.SetValue("__lumenPluginId", pluginId ?? "");
        engine.SetValue("__lumenPluginVersion", pluginVersion ?? "");
        engine.SetValue("__lumenDataDir", dataDir ?? "");

        engine.Execute(Bootstrap, "lumen-api-bootstrap");
    }

    internal int HookCount
    {
        get { lock (_gate) return _hooks.Count; }
    }

    internal int EventCount
    {
        get { lock (_gate) return _events.Count; }
    }

    /// <summary>执行一个脚本文件（顶层代码同步执行；异常向上抛给加载器统一包装成"脚本编译/执行失败"）。</summary>
    internal void RunScriptFile(string filePath, string displayName)
    {
        var text = File.ReadAllText(filePath);
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScriptHostApi));
            ScriptEngineFactory.ResetBudget(_engine);
            _engine.Execute(text, displayName);
        }
    }

    /// <summary>释放：注销本插件所有钩子/事件，并释放引擎。</summary>
    public void Dispose()
    {
        ScriptHookRegistration[] hooks;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            hooks = _hooks.ToArray();
            _hooks.Clear();
            _events.Clear();
            UnsubscribeEventsLocked();
            try
            {
                _engine.Dispose();
            }
            catch (Exception ex)
            {
                TryLog(() => _log.Warn("释放脚本引擎时出错：" + ex.Message));
            }
        }

        foreach (var hook in hooks)
        {
            try
            {
                ScriptHookBus.Unregister(hook.Id);
            }
            catch (Exception ex)
            {
                TryLog(() => _log.Warn("注销脚本钩子时出错：" + ex.Message));
            }
        }
    }

    // ── 宿主 API：日志 ─────────────────────────────────────────────────────────

    private void WriteLog(int level, JsValue message)
    {
        try
        {
            var text = MessageToText(message);
            if (text.Length > MaxLogLength) text = text[..MaxLogLength] + "…";
            switch (level)
            {
                case 3:
                    _log.Warn(text);
                    break;
                case 4:
                    _log.Error(text);
                    break;
                default:
                    _log.Info(text);
                    break;
            }
        }
        catch
        {
            // 日志失败也不能影响脚本。
        }
    }

    private string MessageToText(JsValue message)
    {
        try
        {
            if (message.IsUndefined() || message.IsNull()) return "";
            if (message.Type == Types.Object)
            {
                var json = _engine.Invoke(_stringify, new object[] { message });
                if (!json.IsUndefined() && !json.IsNull()) return json.AsString();
            }
            return message.ToString();
        }
        catch
        {
            return "[无法转换为文本的值]";
        }
    }

    // ── 宿主 API：设置 ─────────────────────────────────────────────────────────

    private JsValue ReadSetting(string key)
    {
        try
        {
            if (!IsValidSettingKey(key))
            {
                _log.Warn("lumen.settings.get：键名无效（1-" + MaxSettingsKeyLength + " 个字符）。");
                return JsValue.Null;
            }

            var raw = _settings.Get(key);
            if (raw is null) return JsValue.Null;

            lock (_gate)
            {
                ScriptEngineFactory.ResetBudget(_engine);
                return _engine.Invoke(_parseJson, new object[] { raw });
            }
        }
        catch (Exception ex)
        {
            _log.Error("lumen.settings.get 失败：" + ex.Message, ex);
            return JsValue.Null;
        }
    }

    private void WriteSetting(string key, JsValue value)
    {
        try
        {
            if (!IsValidSettingKey(key))
            {
                _log.Warn("lumen.settings.set：键名无效（1-" + MaxSettingsKeyLength + " 个字符）。");
                return;
            }

            string json;
            lock (_gate)
            {
                ScriptEngineFactory.ResetBudget(_engine);
                var serialized = _engine.Invoke(_stringify, new object[] { value });
                json = serialized.IsUndefined() || serialized.IsNull() ? "null" : serialized.AsString();
            }

            if (json.Length > MaxSettingsValueBytes)
            {
                _log.Warn("lumen.settings.set：值过大（JSON 超过 " + MaxSettingsValueBytes + " 字节），已忽略键 " + key + "。");
                return;
            }

            _settings.Set(key, json);
            _settings.Flush();
        }
        catch (Exception ex)
        {
            _log.Error("lumen.settings.set 失败：" + ex.Message, ex);
        }
    }

    private static bool IsValidSettingKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= MaxSettingsKeyLength;

    // ── 宿主 API：钩子与事件注册 ──────────────────────────────────────────────

    private double RegisterHook(string? kind, JsValue handler)
    {
        try
        {
            if (!IsFunction(handler))
            {
                _log.Warn("lumen.hooks：注册失败，参数必须是函数。");
                return 0;
            }

            var registration = CreateHookRegistration(kind, handler);
            if (registration is null)
            {
                _log.Warn("lumen.hooks：未知的钩子类型 \"" + kind + "\"。");
                return 0;
            }

            var id = ScriptHookBus.Register(registration);
            lock (_gate)
            {
                if (_disposed) return 0;
                _hooks.Add(registration);
            }
            return id;
        }
        catch (Exception ex)
        {
            _log.Error("lumen.hooks 注册失败：" + ex.Message, ex);
            return 0;
        }
    }

    private ScriptHookRegistration? CreateHookRegistration(string? kind, JsValue handler)
    {
        var normalized = (kind ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "download" => new ScriptHookRegistration
            {
                Kind = ScriptHookKind.DownloadRequest,
                PluginId = "",
                Log = _log,
                DownloadHandler = request => InvokeDownloadHook(handler, request)
            },
            "text" => new ScriptHookRegistration
            {
                Kind = ScriptHookKind.TextReplace,
                Log = _log,
                TextHandler = text => InvokeTextHook(handler, text)
            },
            "notification" => new ScriptHookRegistration
            {
                Kind = ScriptHookKind.NotificationFilter,
                Log = _log,
                NotificationHandler = (title, message) => InvokeNotificationHook(handler, title, message)
            },
            _ => null
        };
    }

    private double RegisterEvent(string? name, JsValue handler)
    {
        try
        {
            var normalized = (name ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(SupportedEvents, normalized) < 0)
            {
                _log.Warn("lumen.events.on：不支持的事件名 \"" + name + "\"，支持：" + string.Join("、", SupportedEvents) + "。");
                return 0;
            }

            if (!IsFunction(handler))
            {
                _log.Warn("lumen.events.on：注册失败，参数必须是函数。");
                return 0;
            }

            var registration = new ScriptEventRegistration
            {
                Id = ScriptHookBus.AllocateId(),
                Name = normalized,
                Handler = handler
            };

            lock (_gate)
            {
                if (_disposed) return 0;
                _events.Add(registration);
                EnsureSubscriptionLocked(normalized);
            }
            return registration.Id;
        }
        catch (Exception ex)
        {
            _log.Error("lumen.events.on 注册失败：" + ex.Message, ex);
            return 0;
        }
    }

    /// <summary>注销钩子或事件（id 在两者间唯一）。</summary>
    private void Off(double idValue)
    {
        try
        {
            var id = (int)Math.Round(idValue, MidpointRounding.AwayFromZero);
            if (id <= 0) return;
            if (ScriptHookBus.Unregister(id)) return;
            lock (_gate)
            {
                var registration = _events.FirstOrDefault(e => e.Id == id);
                if (registration is not null) _events.Remove(registration);
            }
        }
        catch (Exception ex)
        {
            TryLog(() => _log.Warn("lumen 注销注册时出错：" + ex.Message));
        }
    }

    // ── 钩子回调 ──────────────────────────────────────────────────────────────

    private ScriptDownloadResult? InvokeDownloadHook(JsValue handler, DownloadRequest request)
    {
        var requestObject = BuildDownloadRequestObject(request);
        var result = InvokeScript(handler, "onDownloadRequest", [requestObject]);
        if (result.IsUndefined() || result.IsNull()) return null;
        if (result is not ObjectInstance resultObject)
        {
            _log.Warn("onDownloadRequest 回调必须返回对象或 null，已忽略返回值。");
            return null;
        }

        var url = ReadStringProperty(resultObject, "url");
        if (string.IsNullOrWhiteSpace(url)) return null;

        return new ScriptDownloadResult
        {
            Url = url.Trim(),
            DisplayName = ReadStringProperty(resultObject, "displayName"),
            Sha1 = ReadStringProperty(resultObject, "sha1"),
            Headers = ReadHeaders(resultObject)
        };
    }

    private string? InvokeTextHook(JsValue handler, string text)
    {
        var result = InvokeScript(handler, "onTextReplace", [JsValue.FromObject(_engine, text)]);
        if (result.IsUndefined() || result.IsNull()) return null;
        if (result.Type != Types.String)
        {
            _log.Warn("onTextReplace 回调返回了非字符串值，已忽略。");
            return null;
        }

        var replaced = result.AsString();
        if (replaced.Length > MaxHookTextLength)
        {
            _log.Warn("onTextReplace 回调返回的文本过长（超过 1 MB），已忽略。");
            return null;
        }

        return string.Equals(replaced, text, StringComparison.Ordinal) ? null : replaced;
    }

    private bool? InvokeNotificationHook(JsValue handler, string title, string message)
    {
        var result = InvokeScript(
            handler,
            "onNotification",
            [JsValue.FromObject(_engine, title), JsValue.FromObject(_engine, message)]);
        if (result.IsUndefined() || result.IsNull()) return null;
        if (result.Type != Types.Boolean)
        {
            _log.Warn("onNotification 回调返回了非布尔值，已按“显示”处理。");
            return null;
        }
        return result.AsBoolean();
    }

    /// <summary>统一执行脚本回调：串行化引擎访问、重置预算、捕获并记录所有异常。</summary>
    private JsValue InvokeScript(JsValue handler, string label, JsValue[] values)
    {
        lock (_gate)
        {
            if (_disposed) return JsValue.Undefined;
            try
            {
                ScriptEngineFactory.ResetBudget(_engine);
                return _engine.Invoke(handler, values);
            }
            catch (StatementsCountOverflowException ex)
            {
                _log.Error(label + " 执行超限（语句/时间预算），已中断：" + ex.Message, ex);
                return JsValue.Undefined;
            }
            catch (MemoryLimitExceededException ex)
            {
                _log.Error(label + " 执行超限（内存 32 MB），已中断：" + ex.Message, ex);
                return JsValue.Undefined;
            }
            catch (TimeoutException ex)
            {
                _log.Error(label + " 执行超时（单次 2 秒），已中断：" + ex.Message, ex);
                return JsValue.Undefined;
            }
            catch (Exception ex)
            {
                if (label.StartsWith("事件", StringComparison.Ordinal))
                    _log.Error("事件处理器抛出异常，已隔离（" + label + "）：" + ex.Message, ex);
                else
                    _log.Error("插件钩子异常已隔离（" + label + "）：" + ex.Message, ex);
                return JsValue.Undefined;
            }
        }
    }

    // ── 事件订阅与分发 ────────────────────────────────────────────────────────

    private void EnsureSubscriptionLocked(string name)
    {
        if (!_subscribedEvents.Add(name)) return;
        switch (name)
        {
            case "app.startup":
                _appStartupHandler ??= (_, args) => RaiseAppEvent("app.startup", args);
                HookRegistry.AppStartup += _appStartupHandler;
                break;
            case "app.shutdown":
                _appShutdownHandler ??= (_, args) => RaiseAppEvent("app.shutdown", args);
                HookRegistry.AppShutdown += _appShutdownHandler;
                break;
            case "download.before":
                _downloadBeforeHandler ??= RaiseDownloadBefore;
                ScriptHookBus.DownloadBefore += _downloadBeforeHandler;
                break;
            case "download.after":
                _downloadAfterHandler ??= RaiseDownloadAfter;
                ScriptHookBus.DownloadAfter += _downloadAfterHandler;
                break;
        }
    }

    private void UnsubscribeEventsLocked()
    {
        if (_appStartupHandler is not null) HookRegistry.AppStartup -= _appStartupHandler;
        if (_appShutdownHandler is not null) HookRegistry.AppShutdown -= _appShutdownHandler;
        if (_downloadBeforeHandler is not null) ScriptHookBus.DownloadBefore -= _downloadBeforeHandler;
        if (_downloadAfterHandler is not null) ScriptHookBus.DownloadAfter -= _downloadAfterHandler;
        _appStartupHandler = null;
        _appShutdownHandler = null;
        _downloadBeforeHandler = null;
        _downloadAfterHandler = null;
        _subscribedEvents.Clear();
    }

    private void RaiseAppEvent(string eventName, PluginEventArgs? args)
    {
        ScriptEventRegistration[] snapshot;
        lock (_gate) snapshot = _events.Where(e => e.Name == eventName).ToArray();
        foreach (var registration in snapshot)
        {
            InvokeScript(registration.Handler, "事件 " + eventName, [BuildEventArgs(args)]);
        }
    }

    private void RaiseDownloadBefore(DownloadRequest request)
    {
        ScriptEventRegistration[] snapshot;
        lock (_gate) snapshot = _events.Where(e => e.Name == "download.before").ToArray();
        foreach (var registration in snapshot)
        {
            InvokeScript(registration.Handler, "事件 download.before", [BuildDownloadRequestObject(request)]);
        }
    }

    private void RaiseDownloadAfter(DownloadSource? source)
    {
        ScriptEventRegistration[] snapshot;
        lock (_gate) snapshot = _events.Where(e => e.Name == "download.after").ToArray();
        if (snapshot.Length == 0) return;

        foreach (var registration in snapshot)
        {
            var payload = source is null ? JsValue.Null : BuildDownloadSourceObject(source);
            InvokeScript(registration.Handler, "事件 download.after", [payload]);
        }
    }

    // ── JS 对象构造 ──────────────────────────────────────────────────────────

    private JsValue BuildEventArgs(PluginEventArgs? args)
    {
        var payload = NewObject();
        if (args?.Data is null) return payload;
        foreach (var pair in args.Data)
        {
            payload.Set(pair.Key, ToJsValue(pair.Value));
        }
        return payload;
    }

    private JsValue BuildDownloadRequestObject(DownloadRequest request)
    {
        var payload = NewObject();
        payload.Set("url", JsValue.FromObject(_engine, request.Url ?? ""));
        payload.Set("fileName", NullableToJs(request.FileName));
        payload.Set("size", request.Size.HasValue ? JsValue.FromObject(_engine, (double)request.Size.Value) : JsValue.Null);
        payload.Set("sha1", NullableToJs(request.Sha1));
        payload.Set("purpose", NullableToJs(request.Purpose));
        return Freeze(payload);
    }

    private JsValue BuildDownloadSourceObject(DownloadSource source)
    {
        var payload = NewObject();
        payload.Set("url", JsValue.FromObject(_engine, source.Url ?? ""));
        payload.Set("displayName", NullableToJs(source.DisplayName));
        payload.Set("sha1", NullableToJs(source.Sha1));
        return Freeze(payload);
    }

    private JsValue NullableToJs(string? value) =>
        value is null ? JsValue.Null : JsValue.FromObject(_engine, value);

    private ObjectInstance NewObject() => _engine.Invoke(_createObject, Array.Empty<object>()).AsObject();

    private JsValue Freeze(JsValue value) => _engine.Invoke(_freeze, new object[] { value });

    private JsValue ToJsValue(object? value) => value switch
    {
        null => JsValue.Null,
        string text => JsValue.FromObject(_engine, text),
        bool flag => JsValue.FromObject(_engine, flag),
        int number => JsValue.FromObject(_engine, (double)number),
        long number => JsValue.FromObject(_engine, (double)number),
        double number => JsValue.FromObject(_engine, number),
        float number => JsValue.FromObject(_engine, (double)number),
        decimal number => JsValue.FromObject(_engine, (double)number),
        _ => JsValue.FromObject(_engine, value.ToString() ?? "")
    };

    private static bool IsFunction(JsValue value) => value is Function;

    private string? ReadStringProperty(ObjectInstance instance, string property)
    {
        var value = instance.Get(property);
        if (value.IsUndefined() || value.IsNull()) return null;
        if (value.Type == Types.String) return value.AsString();
        return MessageToText(value);
    }

    private IReadOnlyDictionary<string, string>? ReadHeaders(ObjectInstance instance)
    {
        var value = instance.Get("headers");
        if (value is not ObjectInstance headersObject) return null;

        try
        {
            if (headersObject.ToObject() is not IDictionary<string, object?> dictionary) return null;
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in dictionary)
            {
                if (pair.Key.Length == 0 || pair.Value is null) continue;
                result[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "";
            }
            return result.Count == 0 ? null : result;
        }
        catch
        {
            return null;
        }
    }

    private static void TryLog(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>
    /// 引导脚本：用 SetValue 暴露的 __lumen* 委托拼出冻结的 lumen 对象，然后把桥接全局全部置 undefined。
    /// 只支持同步返回值 —— TODO(v1.5)：支持 async/Promise 回调（届时用 Jint 的 Task 互操作逐次等待）。
    /// </summary>
    private const string Bootstrap = """
        (function () {
          "use strict";
          var g = this;
          var hLog = g.__lumenLog;
          var hGet = g.__lumenSettingGet;
          var hSet = g.__lumenSettingSet;
          var hHook = g.__lumenHook;
          var hEvent = g.__lumenEvent;
          var hOff = g.__lumenOff;
          var pluginId = String(g.__lumenPluginId || "");
          var pluginVersion = String(g.__lumenPluginVersion || "");
          var dataDir = String(g.__lumenDataDir || "");
          g.__lumenLog = undefined;
          g.__lumenSettingGet = undefined;
          g.__lumenSettingSet = undefined;
          g.__lumenHook = undefined;
          g.__lumenEvent = undefined;
          g.__lumenOff = undefined;
          g.__lumenPluginId = undefined;
          g.__lumenPluginVersion = undefined;
          g.__lumenDataDir = undefined;

          function toText(value) {
            if (value === null || value === undefined) return "";
            try { return String(value); } catch (e) { return "[无法转换为文本的值]"; }
          }

          function logAt(level, value) {
            try { hLog(level, toText(value)); } catch (e) { }
          }

          function isFunction(value) {
            return typeof value === "function";
          }

          function onHook(kind, fn, name) {
            if (!isFunction(fn)) {
              logAt(3, "lumen.hooks." + name + " 需要一个函数参数，已忽略。");
              return 0;
            }
            try { return hHook(kind, fn); }
            catch (e) { logAt(3, "lumen.hooks." + name + " 注册失败：" + toText(e)); return 0; }
          }

          function onEvent(name, fn) {
            if (!isFunction(fn)) {
              logAt(3, "lumen.events.on 需要一个函数参数，已忽略。");
              return 0;
            }
            try { return hEvent(toText(name), fn); }
            catch (e) { logAt(3, "lumen.events.on 注册失败：" + toText(e)); return 0; }
          }

          var lumen = {
            log: function (message) { logAt(1, message); },
            info: function (message) { logAt(2, message); },
            warn: function (message) { logAt(3, message); },
            error: function (message, detail) {
              logAt(4, detail === undefined ? message : toText(message) + "：" + toText(detail));
            },
            plugin: Object.freeze({ id: pluginId, version: pluginVersion, dataDir: dataDir }),
            settings: Object.freeze({
              get: function (key) {
                try { return hGet(toText(key)); }
                catch (e) { logAt(3, "lumen.settings.get 失败：" + toText(e)); return null; }
              },
              set: function (key, value) {
                try { hSet(toText(key), value); }
                catch (e) { logAt(3, "lumen.settings.set 失败：" + toText(e)); }
              }
            }),
            hooks: Object.freeze({
              onDownloadRequest: function (fn) { return onHook("download", fn, "onDownloadRequest"); },
              onTextReplace: function (fn) { return onHook("text", fn, "onTextReplace"); },
              onNotification: function (fn) { return onHook("notification", fn, "onNotification"); },
              off: function (id) {
                try { hOff(toText(id)); }
                catch (e) { logAt(3, "lumen.hooks.off 失败：" + toText(e)); }
              }
            }),
            events: Object.freeze({
              on: function (name, fn) { return onEvent(name, fn); },
              off: function (id) {
                try { hOff(toText(id)); }
                catch (e) { logAt(3, "lumen.events.off 失败：" + toText(e)); }
              }
            })
          };

          Object.freeze(lumen);
          Object.defineProperty(g, "lumen", {
            value: lumen,
            writable: false,
            enumerable: true,
            configurable: false
          });
        }).call(this);
        """;
}

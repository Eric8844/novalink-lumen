using System.Reflection;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services.Hooks;
using NovaLink.Lumen.Services.Plugins.Scripting;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 插件注册表：发现 → 校验 → 加载 → 初始化 → 事件分发 → 关闭。
/// 铁律：任何单个插件的异常都只影响它自己（置 Failed 并记日志），绝不向启动链抛出；
/// DataRoot 为空时整个插件系统自动降级为"无插件"，保证 P0 零行为变化。
/// </summary>
public sealed class PluginRegistry
{
    private const string LogArea = "plugins";

    /// <summary>
    /// 保护 <see cref="_plugins"/> 与 <see cref="_contexts"/> 的锁。
    /// 运行期插件目录监听会在后台线程重扫注册表，而设置页在 UI 线程读列表，两侧必须串行。
    /// 锁内不做任何等待 UI 线程的事（导航发布只投递、不阻塞），因此不会与 UI 线程互等。
    /// </summary>
    private readonly object _sync = new();

    private readonly List<PluginDescriptor> _plugins = [];
    private readonly Dictionary<string, PluginContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    private PluginRuntimeOptions _options;
    private PluginPaths? _paths;
    private PluginWatchdog? _watchdog;
    private bool _warnedNoRoot;
    private bool _bootBegun;
    private bool _shutdownEventDispatched;

    public PluginRegistry(PluginRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // 只读探测一次持久化状态：必须在任何 BeginLoad 之前定下"本次是否安全模式"。
        EnsurePaths();
        InitialSafeModeSuggested = _watchdog?.RequiresSafeMode == true;
        InitialSafeModeReason = InitialSafeModeSuggested
            ? (_watchdog?.SafeModeReason ?? "上次启动在加载插件后未正常完成（连续 " +
               (_watchdog?.Snapshot().Attempts ?? 0) + " 次），已自动进入插件安全模式。")
            : null;
    }

    /// <summary>
    /// 构造时基于 plugin-boot.json 判定的安全模式建议：上次启动崩在插件加载阶段且已连续 2 次。
    /// 为 true 时 Discover 会自动禁用全部插件（不需要 App 层再传 SafeMode）。
    /// </summary>
    public bool InitialSafeModeSuggested { get; }

    /// <summary>自动安全模式的原因（供 UI 展示）；未进入为 null。</summary>
    public string? InitialSafeModeReason { get; }

    /// <summary>本次运行是否处于安全模式（用户手动指定或看门狗自动建议）。</summary>
    public bool SafeModeActive => _options.SafeMode || InitialSafeModeSuggested;

    /// <summary>本次启动是否真正启用了插件系统（DataRoot 有效）。</summary>
    public bool IsEnabled
    {
        get
        {
            EnsurePaths();
            return _paths is not null;
        }
    }

    /// <summary>
    /// 已登记插件（含被禁用的，供设置页展示原因）。
    /// 返回快照：运行期重扫可能在后台线程摘除条目，直接暴露 List 会让 UI 枚举时抛异常。
    /// </summary>
    public IReadOnlyList<PluginDescriptor> Plugins
    {
        get { lock (_sync) return _plugins.ToArray(); }
    }

    /// <summary>正在运行（已 Initialize）的插件数。</summary>
    public int ActiveCount
    {
        get { lock (_sync) return _plugins.Count(p => p.IsActive); }
    }

    /// <summary>启动状态机；DataRoot 无效时为 null。</summary>
    public PluginWatchdog? Watchdog
    {
        get
        {
            EnsurePaths();
            return _watchdog;
        }
    }

    /// <summary>
    /// 扫描插件目录并登记。options 非空时替换本次运行的参数（便于 App 层先建注册表再决定安全模式）。
    /// 注意：必须在 LoadAll 之前调用；已加载后重复调用会被忽略。
    /// </summary>
    public void Discover(PluginRuntimeOptions? options = null)
    {
        lock (_sync) DiscoverCore(options);
    }

    private void DiscoverCore(PluginRuntimeOptions? options = null)
    {
        if (options is not null) _options = options;

        if (ActiveCount > 0)
        {
            AppLog.Warn(LogArea, "插件已加载，忽略重复的 Discover 调用。");
            return;
        }

        _plugins.Clear();
        _contexts.Clear();
        _shutdownEventDispatched = false;

        if (!EnsurePaths()) return;

        // 自动登记"开始加载"：状态机不依赖 App 层记得调用，避免崩溃循环检测失效。
        BeginBoot();

        if (InitialSafeModeSuggested)
        {
            AppLog.Warn(LogArea, "看门狗判定连续多次启动未完成，本次进入安全模式：" + InitialSafeModeReason);
        }

        var root = _paths!.PluginsRoot;
        try
        {
            _paths.EnsureRoot();
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "创建插件根目录失败，插件系统本次不启用：" + root, ex);
            return;
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "枚举插件根目录失败：" + root, ex);
            return;
        }

        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            try
            {
                DiscoverOne(directory);
            }
            catch (Exception ex)
            {
                // 单个目录的意外异常不能影响其它插件与启动链。
                AppLog.Error(LogArea, "检查插件目录时出错（已跳过）：" + directory, ex);
            }
        }

        AppLog.Info(LogArea, "插件扫描完成：共 " + _plugins.Count + " 个，可加载 " +
                             _plugins.Count(p => p.State == PluginState.Discovered) + " 个。");
    }

    /// <summary>加载并初始化所有处于 Discovered 的插件。单插件失败只禁用自己。</summary>
    public void LoadAll()
    {
        lock (_sync) LoadAllCore();
    }

    private void LoadAllCore()
    {
        if (!EnsurePaths()) return;

        foreach (var descriptor in _plugins.ToArray())
        {
            if (descriptor.State != PluginState.Discovered) continue;

            // 类型 C（原生 DLL）：本版不实现加载，但必须给出明确原因，不能静默跳过。
            if (string.Equals(descriptor.Manifest.Kind, PluginManifestReader.NativeKind, StringComparison.OrdinalIgnoreCase))
            {
                Disable(descriptor,
                    "原生依赖插件暂不支持（kind=native 需要加载原生 DLL，本版本仅支持 assembly 与 script 两类插件）。",
                    PluginState.Disabled);
                continue;
            }

            // 加载前打标记：若本次进程在加载该插件时崩溃，下次启动可归因到它。
            _watchdog?.MarkPluginLoading(descriptor.Id, PluginBootPhases.Loading);

            try
            {
                if (descriptor.Manifest.IsScript) LoadScriptOne(descriptor);
                else LoadOne(descriptor);
            }
            catch (Exception ex)
            {
                FailPlugin(descriptor, ex);
            }
        }

        AppLog.Info(LogArea, "插件加载完成：运行中 " + ActiveCount + " 个。");
    }

    /// <summary>按 id 找已登记插件。</summary>
    public PluginDescriptor? Find(string id)
    {
        lock (_sync)
        {
            return _plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>把所有插件的设置落盘（App 退出前调用）。</summary>
    public void FlushSettings()
    {
        foreach (var descriptor in Plugins)
        {
            try { descriptor.Settings?.Flush(); }
            catch (Exception ex) { AppLog.Error(LogArea, "插件 " + descriptor.Id + " 设置落盘失败。", ex); }
        }
    }

    /// <summary>
    /// 向所有运行中的插件分发事件；单个处理器异常由 PluginContext 隔离。
    /// 脚本插件通过 HookRegistry 的静态事件订阅 app.startup / app.shutdown，这里统一桥接一次，
    /// 这样 App 层只需要调用本方法，不需要关心插件属于哪种类型。
    /// </summary>
    public void Dispatch(PluginEvent pluginEvent, IReadOnlyDictionary<string, object?>? data = null)
    {
        if (pluginEvent == PluginEvent.AppStartup)
        {
            HookRegistry.RaiseAppStartup(data);
        }
        else if (pluginEvent == PluginEvent.AppShutdown)
        {
            _shutdownEventDispatched = true;
            HookRegistry.RaiseAppShutdown(data);
        }

        PluginEventArgs? args = null;
        foreach (var descriptor in Plugins)
        {
            if (!descriptor.IsActive) continue;

            // 只有真的有订阅者时才组装参数；没有订阅者时连遍历都省掉。
            if (!_contexts.TryGetValue(descriptor.Id, out var context) || !context.HasSubscribers(pluginEvent)) continue;

            args ??= new PluginEventArgs(pluginEvent.ToString(), data);
            context.Dispatch(pluginEvent, args);
        }
    }

    /// <summary>某事件是否有插件订阅。</summary>
    public bool HasSubscribers(PluginEvent pluginEvent)
        => Plugins.Any(d => d.IsActive
                             && _contexts.TryGetValue(d.Id, out var context)
                             && context.HasSubscribers(pluginEvent));

    /// <summary>写 plugin-boot.json：state=starting、attempts+1（幂等，本次进程内只写一次）。</summary>
    public void BeginBoot()
    {
        if (_bootBegun || !EnsurePaths() || _watchdog is null) return;
        _bootBegun = true;
        _watchdog.BeginLoad();
    }

    /// <summary>
    /// 启动完全成功后调用：state=healthy、attempts 清零、安全模式原因清空。
    /// 各插件的历史失败计数与隔离标记**保留**（一次健康启动不能洗白失败历史）。
    /// App 层应在主窗口激活后调用一次 —— 不调用的话看门狗会把本次启动当成崩溃。
    /// </summary>
    public void MarkBootHealthy()
    {
        if (!EnsurePaths() || _watchdog is null) return;
        _watchdog.MarkHealthy();
        _bootBegun = false;
    }

    /// <summary>关闭所有插件（逆序），退订事件并尝试卸载加载体。任何异常都吞掉。</summary>
    public void ShutdownAll()
    {
        lock (_sync) ShutdownAllCore();
    }

    private void ShutdownAllCore()
    {
        // 先让脚本插件收到 app.shutdown（卸载之后它们就收不到了）。
        // 若 App 已经显式 Dispatch 过，则不重复触发。
        if (!_shutdownEventDispatched)
        {
            _shutdownEventDispatched = true;
            try { HookRegistry.RaiseAppShutdown(); }
            catch (Exception ex) { AppLog.Warn(LogArea, "分发 app.shutdown 时出错，已忽略：" + ex.Message); }
        }

        for (var i = _plugins.Count - 1; i >= 0; i--)
        {
            var descriptor = _plugins[i];
            if (!descriptor.IsActive) continue;

            try
            {
                descriptor.Instance?.Shutdown();
            }
            catch (Exception ex)
            {
                AppLog.Error(LogArea, "插件 " + descriptor.Id + " Shutdown 抛出异常，已忽略。", ex);
            }

            descriptor.Instance = null;
            descriptor.State = PluginState.Discovered;
            descriptor.Settings?.Flush();
            _contexts.Remove(descriptor.Id);
            UnloadContext(descriptor);
        }

        AppLog.Info(LogArea, "插件已全部关闭。");
    }

    /// <summary>加载单个 assembly 插件（内部方法，异常向上抛给 LoadAll 统一处理）。</summary>
    private void LoadOne(PluginDescriptor descriptor)
    {
        var manifest = descriptor.Manifest;
        var entry = manifest.Entry
                    ?? throw new InvalidOperationException("清单缺少 entry 配置。");

        var entryPath = PluginManifestReader.ResolveInside(descriptor.Directory, entry.Assembly)
                        ?? throw new InvalidOperationException("entry.assembly 必须位于插件目录内部：" + entry.Assembly);

        var loadContext = new PluginLoadContext(descriptor.Directory, entryPath);
        descriptor.LoadContext = loadContext;

        var assembly = loadContext.LoadFromAssemblyPath(entryPath);
        descriptor.State = PluginState.Loaded;

        var type = ResolvePluginType(assembly, entry.Type)
                   ?? throw new InvalidOperationException("找不到实现 ILumenPlugin 的入口类型：" + entry.Type);

        // 从这里开始进入宿主代码：崩溃点定位到"正在初始化"。
        _watchdog?.MarkPluginPhase(descriptor.Id, PluginBootPhases.Initializing);

        if (Activator.CreateInstance(type) is not ILumenPlugin instance)
            throw new InvalidOperationException("无法实例化插件入口类型（需要无参构造函数）：" + type.FullName);

        descriptor.Instance = instance;

        var settings = new PluginSettingsStore(
            Path.Combine(descriptor.Directory, PluginPaths.SettingsFileName),
            message => AppLog.Warn("plugin:" + descriptor.Id, message));
        descriptor.Settings = settings;

        var context = new PluginContext(
            descriptor.Id,
            new PluginLogger(descriptor.Id),
            settings,
            _options.Host ?? NullPluginHost.Instance,
            _options.Ui ?? NullPluginUi.Instance,
            _options.Theme);
        _contexts[descriptor.Id] = context;

        instance.Initialize(context);

        descriptor.State = PluginState.Initialized;
        descriptor.LastError = null;
        _watchdog?.MarkPluginLoaded(descriptor.Id);

        // 清单里声明的导航项交由 UI 门面登记。P0 的 NullPluginUi 会直接忽略，
        // P1 的真实实现负责跨线程排队到 UI 线程。
        RegisterNavigation(descriptor, context, assembly);

        AppLog.Info(LogArea, "插件已加载：" + descriptor.Id + " " + (manifest.Version ?? "") +
                             "（UI " + (context.Ui.IsAvailable ? "可用" : "不可用") + "）");
    }

    /// <summary>
    /// 加载单个 script（Jint）插件：创建脚本运行时、执行 when=startup 的脚本、注册钩子。
    /// 异常向上抛给 LoadAll 统一走失败/隔离逻辑。
    /// </summary>
    private void LoadScriptOne(PluginDescriptor descriptor)
    {
        var manifest = descriptor.Manifest;
        if (manifest.Scripts is null || manifest.Scripts.Count == 0)
            throw new InvalidOperationException("清单缺少 scripts 配置。");

        _watchdog?.MarkPluginPhase(descriptor.Id, PluginBootPhases.Initializing);

        var dataRoot = _paths?.DataRoot ?? _options.DataRoot;
        var handler = new JsScriptPluginHandler(descriptor, dataRoot);
        descriptor.Instance = handler;

        var settings = new PluginSettingsStore(
            Path.Combine(descriptor.Directory, PluginPaths.SettingsFileName),
            message => AppLog.Warn("plugin:" + descriptor.Id, message));
        descriptor.Settings = settings;

        var context = new PluginContext(
            descriptor.Id,
            new PluginLogger(descriptor.Id),
            settings,
            _options.Host ?? NullPluginHost.Instance,
            _options.Ui ?? NullPluginUi.Instance,
            _options.Theme);
        _contexts[descriptor.Id] = context;

        handler.Initialize(context);

        descriptor.State = PluginState.Initialized;
        descriptor.LastError = null;
        _watchdog?.MarkPluginLoaded(descriptor.Id, "script");

        AppLog.Info(LogArea, "脚本插件已加载：" + descriptor.Id + " " + (manifest.Version ?? "") +
                             "（脚本 " + manifest.Scripts.Count + " 个，钩子 " + handler.HookCount +
                             "，事件 " + handler.EventCount + "）");
    }

    /// <summary>单个插件加载失败的统一收尾：置 Failed、累计失败、写看门狗并卸载残余。</summary>
    private void FailPlugin(PluginDescriptor descriptor, Exception ex)
    {
        descriptor.Instance = null;
        descriptor.FailCount++;
        descriptor.State = PluginState.Failed;
        descriptor.LastError = "加载失败：" + ex.Message;

        AppLog.Error(LogArea, "插件 " + descriptor.Id + " 加载失败，已禁用：", ex);
        _watchdog?.MarkPluginFailure(descriptor.Id, ex.Message);
        descriptor.Quarantined = _watchdog?.IsQuarantined(descriptor.Id) ?? descriptor.Quarantined;

        // 尽力卸载，避免半初始化状态残留。
        try { descriptor.LoadContext?.Unload(); } catch { }
        descriptor.LoadContext = null;
        _contexts.Remove(descriptor.Id);
    }

    /// <summary>解析入口类型：先按清单 type 精确查找，找不到则要求程序集里只有一个 ILumenPlugin 实现。</summary>
    private static Type? ResolvePluginType(Assembly assembly, string typeName)
    {
        var exact = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (exact is not null && IsPluginType(exact)) return exact;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }
        catch
        {
            return null;
        }

        var candidates = types.Where(IsPluginType).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsPluginType(Type type)
        => type.IsClass && !type.IsAbstract && typeof(ILumenPlugin).IsAssignableFrom(type);

    /// <summary>取消隔离（设置页"重新启用"按钮调用）。运行中的插件不受影响。</summary>
    public void ClearQuarantine(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        _watchdog?.ClearQuarantine(id);

        var descriptor = Find(id);
        if (descriptor is null || descriptor.IsActive) return;

        descriptor.Quarantined = false;
        descriptor.FailCount = 0;
        descriptor.LastError = null;
        if (descriptor.State is PluginState.Disabled)
        {
            descriptor.State = PluginState.Discovered;
            descriptor.DisabledByUser = false;
            AppLog.Info(LogArea, "插件 " + id + " 已解除隔离，下次启动会重新尝试加载。");
        }
    }

    /// <summary>
    /// 一次性解除全部插件的隔离与历史失败（设置页"重新启用插件"按钮用，避免逐个调用 ClearQuarantine）。
    /// 返回被清理的插件数。运行中的插件不受影响。
    /// </summary>
    public int ClearAllQuarantines()
    {
        var count = _watchdog?.ClearAllQuarantines() ?? 0;

        foreach (var descriptor in _plugins)
        {
            if (descriptor.IsActive) continue;

            descriptor.Quarantined = false;
            descriptor.FailCount = 0;
            descriptor.LastError = null;
            if (descriptor.State is PluginState.Disabled && descriptor.DisabledByUser is false)
            {
                descriptor.State = PluginState.Discovered;
            }
        }

        if (count > 0)
        {
            AppLog.Info(LogArea, "已解除全部插件隔离（" + count + " 个），下次启动会重新尝试加载。");
        }

        return count;
    }

    /// <summary>某插件当前是否被隔离（内存状态与看门狗持久化记录取并集），供设置页展示。</summary>
    public bool IsPluginQuarantined(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        return Find(id)?.Quarantined == true || (_watchdog?.IsQuarantined(id) ?? false);
    }

    /// <summary>当前安全模式原因（含上次启动的崩溃归因），供设置页展示；未进入为 null。</summary>
    public string? GetBootReason() => InitialSafeModeReason ?? _watchdog?.GetBootReason();

    /// <summary>
    /// 插件目录在运行期发生变化（用户往 plugins 里放/删插件、或插件自己写文件）时重扫一遍：
    /// 新目录走和启动时完全相同的发现/加载路径；消失的目录摘出注册表并卸载加载体。
    /// 返回差值供 App 层撤销导航项、刷新列表与提示用户。
    /// 铁律不变：任何单个插件的失败只影响它自己，绝不抛出到调用方。
    /// </summary>
    public PluginRefreshResult RefreshFromDisk()
    {
        if (!IsEnabled) return PluginRefreshResult.Empty;

        var root = _paths!.PluginsRoot;
        string[] directories;
        try
        {
            if (!Directory.Exists(root)) return PluginRefreshResult.Empty;
            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "重扫插件目录失败（保持当前列表）：" + ex.Message);
            return PluginRefreshResult.Empty;
        }

        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);

        var addedIds = new List<string>();
        var removedIds = new List<string>();
        var navTags = new List<string>();
        var failed = 0;

        lock (_sync)
        {
            // 1) 目录已经不在的插件：从内存里彻底摘掉，并把导航 tag 交给 App 层撤销。
            //    这里不做"标 Disabled"的处理 —— 目录都没了，留着它只会在列表里显示一个点不开的死条目。
            var removed = _plugins
                .Where(p => !directories.Any(d => string.Equals(d, p.Directory, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            foreach (var descriptor in removed)
            {
                try
                {
                    descriptor.Instance?.Shutdown();
                }
                catch (Exception ex)
                {
                    AppLog.Warn(LogArea, "移除插件 " + descriptor.Id + " 时 Shutdown 出错（已忽略）：" + ex.Message);
                }

                descriptor.Instance = null;
                descriptor.State = PluginState.Discovered;
                descriptor.Settings?.Flush();
                _contexts.Remove(descriptor.Id);
                UnloadContext(descriptor);
                _plugins.Remove(descriptor);
                removedIds.Add(descriptor.Id);

                var nav = descriptor.Manifest.Ui?.Nav;
                if (nav is null) continue;
                foreach (var item in nav)
                {
                    if (!string.IsNullOrWhiteSpace(item.Tag)) navTags.Add(item.Tag);
                }
            }

            // 2) 新出现的目录：DiscoverOne 里已有 id 校验、版本判定、安全模式、用户开关与隔离判定，
            //    所以这里只补"加载"这一步，保证运行期加载和启动加载的行为完全一致。
            foreach (var directory in directories)
            {
                if (_plugins.Any(p => string.Equals(p.Directory, directory, StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    var before = _plugins.Count;
                    DiscoverOne(directory);
                    if (_plugins.Count == before) continue; // 清单读不出来或 id 重复，DiscoverOne 已记日志

                    var descriptor = _plugins[^1];
                    addedIds.Add(descriptor.Id);

                    // 被判定禁用（不兼容/用户关闭/隔离）的插件只出现在列表里，不尝试加载。
                    if (descriptor.State != PluginState.Discovered) continue;

                    _watchdog?.MarkPluginLoading(descriptor.Id, PluginBootPhases.Loading);
                    try
                    {
                        if (descriptor.Manifest.IsScript) LoadScriptOne(descriptor);
                        else LoadOne(descriptor);
                    }
                    catch (Exception ex)
                    {
                        FailPlugin(descriptor, ex);
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Error(LogArea, "运行期加载插件目录失败（已跳过）：" + directory, ex);
                }
            }
        }

        if (addedIds.Count > 0 || removedIds.Count > 0 || failed > 0)
        {
            AppLog.Info(LogArea, "插件目录已变化：新增 " + addedIds.Count + " 个，移除 " + removedIds.Count +
                                 " 个，加载失败 " + failed + " 个。");
        }

        return new PluginRefreshResult(addedIds.Count, removedIds.Count, failed, addedIds, removedIds, navTags);
    }

    /// <summary>
    /// 把插件从内存注册表摘掉（用户在启动器里删除插件后调用），返回它登记过的导航 tag 供撤销。
    /// 只动内存状态：目录删除与"禁用"偏好由 App 层负责。
    /// </summary>
    public IReadOnlyList<string> Forget(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Array.Empty<string>();

        lock (_sync)
        {
            var descriptor = Find(id);
            if (descriptor is null) return Array.Empty<string>();

            var tags = new List<string>();
            var nav = descriptor.Manifest.Ui?.Nav;
            if (nav is not null)
            {
                foreach (var item in nav)
                {
                    if (!string.IsNullOrWhiteSpace(item.Tag)) tags.Add(item.Tag);
                }
            }

            try
            {
                descriptor.Instance?.Shutdown();
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogArea, "删除插件时 Shutdown 出错（已忽略）：" + ex.Message);
            }

            descriptor.Instance = null;
            descriptor.Settings?.Flush();
            _contexts.Remove(descriptor.Id);
            UnloadContext(descriptor);
            _plugins.Remove(descriptor);

            AppLog.Info(LogArea, "插件已从列表移除：" + id);
            return tags;
        }
    }

    /// <summary>把清单 ui.nav 注册到 UI 门面；UI 不可用或单项出错都不影响插件本身。</summary>
    private void RegisterNavigation(PluginDescriptor descriptor, PluginContext context, Assembly assembly)
    {
        var items = descriptor.Manifest.Ui?.Nav;
        if (items is null || items.Count == 0) return;

        if (!context.Ui.IsAvailable)
        {
            AppLog.Info(LogArea, "插件 " + descriptor.Id + " 声明了 " + items.Count + " 个导航项，但当前 UI 注入不可用，已跳过。");
            return;
        }

        foreach (var item in items)
        {
            try
            {
                Func<object>? factory = null;
                if (!string.IsNullOrWhiteSpace(item.Page))
                {
                    var pageType = assembly.GetType(item.Page!, throwOnError: false, ignoreCase: false);
                    if (pageType is null || !pageType.IsClass || pageType.IsAbstract)
                    {
                        AppLog.Warn(LogArea, "插件 " + descriptor.Id + " 的导航页类型无效，已跳过：" + item.Page);
                        continue;
                    }
                    factory = () => Activator.CreateInstance(pageType)
                                     ?? throw new InvalidOperationException("无法创建页面实例：" + pageType.FullName);
                }

                context.Ui.AddNavigation(new PluginNavItem(item.Tag, item.Title, factory, item.Order, item.Glyph));
            }
            catch (Exception ex)
            {
                AppLog.Error(LogArea, "插件 " + descriptor.Id + " 的导航项注册失败：" + item.Tag, ex);
            }
        }
    }

    /// <summary>扫描单个插件目录并按清单/用户设置决定状态。</summary>
    private void DiscoverOne(string directory)
    {
        var dirName = Path.GetFileName(directory);
        var result = PluginManifestReader.Load(directory);

        // 连清单都读不出来：无法登记，只能记日志。
        if (result.Manifest is null)
        {
            AppLog.Warn(LogArea, "忽略插件目录 " + dirName + "：" + result.ErrorText);
            return;
        }

        var manifest = result.Manifest;
        var id = manifest.Id;

        if (Find(id) is not null)
        {
            AppLog.Warn(LogArea, "忽略重复的插件 id：" + id + "（" + directory + "）");
            return;
        }

        var descriptor = new PluginDescriptor(id, manifest, directory)
        {
            FailCount = _watchdog?.GetFailCount(id) ?? 0,
            Quarantined = _watchdog?.IsQuarantined(id) ?? false
        };
        _plugins.Add(descriptor);

        if (_options.DevMode)
            AppLog.Info(LogArea, "发现插件 " + id + "（" + (manifest.Version ?? "无版本") + "，目录 " + dirName + "）");

        if (result.Errors.Count > 0)
        {
            Disable(descriptor, "清单校验失败：" + result.ErrorText, PluginState.Disabled);
            return;
        }

        if (!PluginVersion.IsCompatible(manifest.MinLauncher, manifest.MaxLauncher, _options.LauncherVersion))
        {
            Disable(descriptor,
                "与当前启动器版本不兼容（需要 " + (manifest.MinLauncher ?? "any") + " ~ " +
                (manifest.MaxLauncher ?? "any") + "，当前 " + _options.LauncherVersion + "）",
                PluginState.Disabled);
            return;
        }

        if (SafeModeActive)
        {
            Disable(descriptor,
                "安全模式（" + (InitialSafeModeSuggested ? "看门狗自动判定" : "用户手动开启") + "）：本次启动不加载任何插件。",
                PluginState.Disabled);
            return;
        }

        // 用户开关：查询闭包由 App 层提供（读 settings.json 扩展段），它自己也可能出错，绝不能影响发现流程。
        bool? enabled = null;
        if (_options.PluginEnabledQuery is not null)
        {
            try { enabled = _options.PluginEnabledQuery(id); }
            catch (Exception ex) { AppLog.Warn(LogArea, "读取插件开关失败（按启用处理）：" + id + " / " + ex.Message); }
        }

        if (enabled == false)
        {
            descriptor.DisabledByUser = true;
            Disable(descriptor, "已在设置中禁用。", PluginState.Disabled);
            return;
        }

        if (descriptor.Quarantined)
        {
            Disable(descriptor,
                "连续加载失败 " + descriptor.FailCount + " 次，已隔离（可在插件设置中重新启用）。",
                PluginState.Disabled);
        }
    }

    /// <summary>标记插件禁用并记录原因。</summary>
    private static void Disable(PluginDescriptor descriptor, string reason, PluginState state)
    {
        descriptor.State = state;
        descriptor.LastError = reason;
        AppLog.Warn(LogArea, "插件 " + descriptor.Id + " 未加载：" + reason);
    }

    /// <summary>尽力卸载加载体（collectible）；失败只记日志。</summary>
    private static void UnloadContext(PluginDescriptor descriptor)
    {
        var loadContext = descriptor.LoadContext;
        descriptor.LoadContext = null;
        if (loadContext is null) return;

        try
        {
            loadContext.Unload();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "插件 " + descriptor.Id + " 加载体卸载失败（不影响运行）：" + ex.Message);
        }
    }

    /// <summary>按当前 DataRoot 准备路径与看门狗；DataRoot 为空时降级为"无插件"。</summary>
    private bool EnsurePaths()
    {
        if (string.IsNullOrWhiteSpace(_options.DataRoot))
        {
            if (!_warnedNoRoot)
            {
                _warnedNoRoot = true;
                AppLog.Warn(LogArea, "插件系统未启用：未提供数据根目录（PluginRuntimeOptions.DataRoot 为空）。");
            }
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(_options.DataRoot);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "插件数据根目录无效，插件系统本次不启用：" + _options.DataRoot, ex);
            return false;
        }

        if (_paths is not null && string.Equals(_paths.DataRoot, normalized, StringComparison.OrdinalIgnoreCase))
            return true;

        _paths = new PluginPaths(normalized);
        _watchdog = new PluginWatchdog(
            _paths.BootFile,
            message => AppLog.Info(LogArea, message),
            (message, ex) => AppLog.Error(LogArea, message, ex));
        return true;
    }
}

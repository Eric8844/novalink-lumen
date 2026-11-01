using NovaLink.Lumen.Core;
using NovaLink.Lumen.Plugins;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Accounts;
using NovaLink.Lumen.Services.Forum;
using NovaLink.Lumen.Services.Home;
using NovaLink.Lumen.Services.Hooks;
using NovaLink.Lumen.Services.Http;
using NovaLink.Lumen.Services.Minecraft;
using NovaLink.Lumen.Services.Microsoft;
using NovaLink.Lumen.Services.Java;
using NovaLink.Lumen.Services.Modrinth;
using NovaLink.Lumen.Services.News;
using NovaLink.Lumen.Services.NovaLink;
using NovaLink.Lumen.Services.Plugins;
using NovaLink.Lumen.Services.Plugins.Store;
using NovaLink.Lumen.Services.Storage;
using NovaLink.Lumen.Services.Updates;
using NovaLink.Lumen.Startup;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NovaLink.Lumen;

public static class LumenRuntime
{
    public static AppPaths Paths { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static SecretProtector Secrets { get; private set; } = null!;
    public static HttpService Http { get; private set; } = null!;
    public static NovaLinkClient NovaLink { get; private set; } = null!;
    public static AccountService Accounts { get; private set; } = null!;
    public static MicrosoftAuthService Microsoft { get; private set; } = null!;
    public static VersionManifestService Manifest { get; private set; } = null!;
    public static InstanceService Instances { get; private set; } = null!;
    public static NewsService News { get; private set; } = null!;
    public static JavaService Java { get; private set; } = null!;
    public static JavaInstallerService JavaInstaller { get; private set; } = null!;
    public static LaunchService Launch { get; private set; } = null!;
    public static LoaderService Loaders { get; private set; } = null!;
    public static VersionInstallService Installer { get; private set; } = null!;
    public static ModrinthService Modrinth { get; private set; } = null!;
    public static ModpackService Modpacks { get; private set; } = null!;
    public static ForumService Forum { get; private set; } = null!;
    public static HeroTaglineService HeroTaglines { get; private set; } = null!;
    public static HeroArtService HeroArt { get; private set; } = null!;
    public static LibraryService Library { get; private set; } = null!;
    public static UpdateService Updates { get; private set; } = null!;

    /// <summary>
    /// 插件商店（发现页「启动器插件」栏）。数据来自 GitHub 上打了 topic 的开源仓库。
    /// 实验性功能「启动器插件」关闭时为 null（调用方必须判空）。
    /// </summary>
    public static PluginStoreClient Store { get; private set; } = null!;

    /// <summary>
    /// 插件系统。<see cref="PluginsEnabled"/> 关闭时为 null（调用方必须判空）；
    /// 开启但数据目录不可用时 <see cref="PluginRegistry.IsEnabled"/> 为 false，全链路降级。
    /// </summary>
    public static PluginRegistry Plugins { get; private set; } = null!;

    /// <summary>
    /// 本次启动是否启用了实验性功能「启动器插件」。只在启动链里判定一次：
    /// 设置里的开关（settings.json 的 ExperimentalPlugins）或 <c>--experimental-plugins</c> 都能开启。
    /// 关闭时：不建商店客户端、不建注册表、不扫描插件、不加载任何钩子，界面也不出现插件入口。
    /// </summary>
    public static bool PluginsEnabled { get; private set; }

    /// <summary>
    /// 插件目录（&lt;DataRoot&gt;\plugins）内容发生变化时触发。设置页订阅它来实时刷新插件列表；
    /// 触发时注册表已经重扫完毕（新增已尝试加载、消失的已摘除），订阅方只需重建界面。
    /// 事件在后台线程抛出，UI 必须自己 DispatcherQueue.TryEnqueue 回来。
    /// </summary>
    public static event EventHandler? PluginsFolderChanged;

    /// <summary>插件目录监听器；插件未启用、安全模式或初始化失败时为 null。</summary>
    private static PluginFolderWatcher? _pluginWatcher;

    /// <summary>一次目录变化触发的重扫可能很慢（要加载程序集），用它在重入时直接丢弃后来的通知。</summary>
    private static int _pluginRefreshRunning;

    /// <summary>本次启动的升级判定结果（null 表示还没跑到那一步）。</summary>
    public static UpgradePlan? Upgrade { get; private set; }

    /// <summary>本次启动是否真的建了升级前备份。</summary>
    public static bool UpgradeBackupCreated { get; private set; }

    /// <summary>本次启动是否执行了 --restore-data-backup（true 时运行时装到一半就返回了，UI 必须提示重启）。</summary>
    public static bool RestorePerformed { get; private set; }

    /// <summary>程序集版本（如 1.4.1）；插件兼容性判定与关于页都读它。</summary>
    public static string LauncherVersion
    {
        get
        {
            try
            {
                var version = typeof(LumenRuntime).Assembly.GetName().Version;
                return version is null ? "0.0.0" : version.ToString(3);
            }
            catch
            {
                return "0.0.0";
            }
        }
    }

    /// <summary>
    /// 启动链。顺序即安全策略：
    /// 数据恢复 → 目录 → 日志 → 升级前备份 →（备份成功才）schema 迁移 → 服务 → 插件。
    /// </summary>
    public static async Task InitializeAsync(StartupOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Paths = new AppPaths();

        if (options.RestoreDataBackup)
        {
            var outcome = await UpgradeGuard.RestoreAsync(Paths, ct).ConfigureAwait(false);
            if (outcome.Success)
            {
                RestorePerformed = true;
                BootLog.Info("已从备份恢复数据：" + outcome.BackupPath);
                return;
            }

            BootLog.Warn("从备份恢复失败：" + (outcome.Error ?? "原因未知"));
            BootState.Notices.Add("没有找到可用的数据备份，已按正常方式启动。" +
                                  (string.IsNullOrWhiteSpace(outcome.Error) ? "" : "（" + outcome.Error + "）"));
        }

        Paths.EnsureDirectories();
        AppLog.Initialize(Paths.Logs);
        BootLog.Configure(Paths.Root);

        await RunUpgradeGuardAsync(options, ct).ConfigureAwait(false);

        Settings = new SettingsService(Paths);
        ApplyPendingMigration(options);

        // 实验性功能判定：设置里的开关或本轮启动参数，二者其一即可；本次进程只判一次。
        // 关闭时后面既不建商店客户端，也不进 InitializePlugins —— 插件与钩子完全不存在。
        PluginsEnabled = Settings.Settings.ExperimentalPlugins || options.ExperimentalPlugins;

        Secrets = new SecretProtector();
        Http = new HttpService();
        NovaLink = new NovaLinkClient(Http);
        Accounts = new AccountService(Settings, Secrets, NovaLink);
        Microsoft = new MicrosoftAuthService(Http, Accounts);
        Manifest = new VersionManifestService(Paths, Http);
        Instances = new InstanceService(Settings, Manifest);
        News = new NewsService(Paths, Http);
        Java = new JavaService(Paths, Settings);
        JavaInstaller = new JavaInstallerService(Paths, Settings, Java, Http);
        Launch = new LaunchService(Paths, Settings, Java, Accounts, Secrets, Microsoft, JavaInstaller);
        Loaders = new LoaderService(Paths, Settings, Http, Manifest, Instances, Launch, Java);
        Installer = new VersionInstallService(Paths, Settings, Http, Manifest, Instances, Launch);
        Modrinth = new ModrinthService(Http)
        {
            MaxParallelChunks = Math.Clamp(Settings.Settings.DownloadThreads, 2, 8)
        };
        Modpacks = new ModpackService(Paths, Settings, Http, Manifest, Instances, Launch);
        Forum = new ForumService(NovaLink, Accounts);
        HeroTaglines = new HeroTaglineService(Paths, Http);
        HeroArt = new HeroArtService(Paths, Http, News);
        Library = new LibraryService(Paths);
        Updates = new UpdateService(Paths, Http);
        if (PluginsEnabled)
        {
            Store = new PluginStoreClient(Http, new PluginPaths(Paths.Root)) { LauncherVersion = LauncherVersion };
            ApplyStoreDevOverrides(options);
        }

        InitializePlugins(options);

        AppLog.Info("app", "NovaLink Lumen 启动，数据目录：" + Paths.Root + "，版本 " + LauncherVersion);
    }

    /// <summary>
    /// 开发者专用：<c>--dev-mode</c> 启动时允许用环境变量把插件商店指向本地桩服务，
    /// 方便在没有真实仓库的情况下调试商店界面（普通用户不会触发）。
    /// </summary>
    private static void ApplyStoreDevOverrides(StartupOptions options)
    {
        if (!options.DevMode) return;

        var api = Environment.GetEnvironmentVariable("NOVALINK_STORE_API");
        var raw = Environment.GetEnvironmentVariable("NOVALINK_STORE_RAW");
        var web = Environment.GetEnvironmentVariable("NOVALINK_STORE_WEB");
        if (string.IsNullOrWhiteSpace(api) && string.IsNullOrWhiteSpace(raw) && string.IsNullOrWhiteSpace(web)) return;

        if (!string.IsNullOrWhiteSpace(api)) Store.ApiBaseUrl = api!.Trim();
        if (!string.IsNullOrWhiteSpace(raw)) Store.RawBaseUrl = raw!.Trim();
        if (!string.IsNullOrWhiteSpace(web)) Store.WebBaseUrl = web!.Trim();
        AppLog.Info("plugins", "开发者模式：插件商店指向本地桩服务 API=" + Store.ApiBaseUrl + " RAW=" + Store.RawBaseUrl + " WEB=" + Store.WebBaseUrl);
    }

    /// <summary>同步兼容入口（脚本/旧调用方）。新代码请用 <see cref="InitializeAsync"/>，否则会阻塞调用线程。</summary>
    public static void Initialize() => InitializeAsync(StartupOptions.FromEnvironment()).GetAwaiter().GetResult();

    // ------------------------------------------------------------------
    // 升级防线
    // ------------------------------------------------------------------

    private static async Task RunUpgradeGuardAsync(StartupOptions options, CancellationToken ct)
    {
        try
        {
            var guard = new UpgradeGuard(Paths) { IncludeCache = options.BackupIncludeCache };
            var plan = guard.Inspect();
            Upgrade = plan;

            if (plan.Kind == UpgradeKind.AlreadyCurrent)
            {
                AppLog.Info("upgrade", "数据已是最新（schema " + plan.SourceSchema + "），跳过升级备份");
                return;
            }

            AppLog.Info("upgrade", "升级判定：" + plan.Kind + "，源版本 " + (plan.SourceVersion ?? "unknown") +
                                    "，源 schema " + plan.SourceSchema + "，备份到 " + (plan.BackupRoot ?? "(无需备份)"));

            var progress = new Progress<string>(message => AppLog.Info("upgrade", message));
            var outcome = await guard.EnsureBackupAsync(plan, progress, ct).ConfigureAwait(false);

            if (!outcome.Success)
            {
                // 备份失败 → 绝不动数据：跳过 schema 迁移，让用户在旧 schema 上先把这一版用起来。
                AppLog.Error("upgrade", "升级前备份失败，已跳过 schema 迁移：" + (outcome.Error ?? "原因未知"));
                BootState.Notices.Add("旧数据没能自动备份，因此这次没有升级数据格式。你可以正常使用，" +
                                      "但建议把数据目录复制一份到别处。");
                return;
            }

            UpgradeBackupCreated = outcome.BackupPath is not null && !outcome.AlreadyExisted;
            if (outcome.BackupPath is not null)
            {
                BootState.UpgradeBackupCreated = true;
                BootState.UpgradeBackupPath = outcome.BackupPath;
                AppLog.Info("upgrade", "升级前备份就绪：" + outcome.BackupPath +
                                        (outcome.AlreadyExisted ? "（已存在，未覆盖）" : ""));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 防线自己出问题也不能挡启动：退化成"不迁移"，并留一条给用户看的话。
            AppLog.Error("upgrade", "升级防线异常，已跳过备份与迁移", ex);
            BootState.Notices.Add("升级检查没有完成，数据格式保持原样。");
        }
    }

    /// <summary>备份成功后才允许把数据升到当前 schema（备份失败时 <see cref="Upgrade"/> 仍在，但这里会跳过）。</summary>
    private static void ApplyPendingMigration(StartupOptions options)
    {
        var plan = Upgrade;
        if (plan is null) return;
        if (plan.Kind == UpgradeKind.AlreadyCurrent) return;

        if (plan.Kind == UpgradeKind.NeedsUpgrade &&
            !UpgradeBackupCreated &&
            !(plan.BackupRoot is not null && UpgradeGuard.HasCompletedBackup(plan.BackupRoot)))
        {
            AppLog.Warn("schema", "没有可信的升级前备份，跳过 schema 迁移");
            return;
        }

        try
        {
            var applied = SchemaMigrator.Migrate(Settings.Settings, plan.SourceSchema, plan.SourceVersion);
            if (applied.Count == 0) return;

            Settings.Save();
            AppLog.Info("schema", "数据迁移完成：" + string.Join("；", applied));
        }
        catch (Exception ex)
        {
            AppLog.Error("schema", "数据迁移失败，本次以原配置启动（可从备份恢复）", ex);
            BootState.Notices.Add("数据格式升级没有完成，启动器仍可正常使用。");
        }
    }

    // ------------------------------------------------------------------
    // 插件
    // ------------------------------------------------------------------

    private static void InitializePlugins(StartupOptions options)
    {
        if (!PluginsEnabled)
        {
            // 实验性功能未开启：不建注册表、不扫描、不加载、不注册任何钩子。
            // HookRegistry.Reset() 是双保险 —— 三个委托与两个静态事件在这里被清空，
            // 即使同进程里有人提前挂过钩子，也不会影响插件关闭状态下的行为。
            HookRegistry.Reset();
            AppLog.Info("plugins", "实验性功能未开启：本次不加载任何插件。");
            return;
        }

        try
        {
            var pluginOptions = new PluginRuntimeOptions
            {
                DataRoot = Paths.Root,
                LauncherVersion = LauncherVersion,
                SafeMode = options.PluginsDisabled,
                DevMode = options.DevMode,
                Host = new LumenPluginHost(),
                Ui = PluginUiService.Instance,
                PluginEnabledQuery = IsPluginEnabled
            };

            Plugins = new PluginRegistry(pluginOptions);

            // UI 门面要能从导航 tag 反查插件（判断该加载哪个 .pri），必须晚于注册表创建。
            PluginUiService.Instance.Configure(() => Plugins.Plugins);

            // 队列目录与 plugins 平级：plugin-updates（排队的新版本）、plugin-deletes（排队的删除）。
            var pluginsRoot = Path.Combine(Paths.Root, PluginPaths.PluginsFolderName);

            // 上次运行中插件的 DLL 被占用、没法就地覆盖时，新包会排在 plugin-updates 里。
            // 现在插件还没加载（Discover 之前），正是把队列落地的时机。
            if (!pluginOptions.SafeMode)
                PluginPackageInstaller.ApplyPendingUpdates(pluginsRoot);

            // 排队的删除是安全操作：安全模式下也照做，否则"删除不掉的插件"会永远留在目录里。
            PluginPackageInstaller.ApplyPendingDeletes(pluginsRoot);

            Plugins.Discover(pluginOptions);

            // 看门狗：连续两次"加载插件时进程没了"就自动进安全模式（用户仍可用启动器）。
            // Discover 内部已按 InitialSafeModeSuggested 一次性禁用全部插件，
            // 这里只把原因交给 UI —— 不要再二次 Discover，否则会重复扫描与重复日志。
            if (Plugins.InitialSafeModeSuggested && !options.SafeMode)
            {
                BootState.SafeModeReason = Plugins.InitialSafeModeReason
                                           ?? "上次启动在加载插件时崩溃了两次，已自动切到安全模式。";
                AppLog.Warn("plugins", "看门狗触发安全模式：" + BootState.SafeModeReason);
            }

            if (options.SafeMode && BootState.SafeModeReason is null)
                BootState.SafeModeReason = "你以安全模式启动了启动器。";

            Plugins.Watchdog?.BeginLoad();
            Plugins.LoadAll();

            // 预热实例快照：插件调用 Host.ListInstances() 时能拿到真实数据（不阻塞启动）。
            _ = LumenPluginHost.RefreshInstancesAsync();

            // 插件目录变化实时生效：用户在资源管理器里放/删插件、或插件自己写文件，
            // 都走一次防抖重扫（新增插件当场加载、消失的插件撤掉导航），列表 1 秒内反映。
            // 安全模式下不监听：本次启动本来就不加载任何插件，重扫只会白费力气。
            if (!pluginOptions.SafeMode) StartPluginsFolderWatcher(pluginsRoot);
        }
        catch (Exception ex)
        {
            // 插件系统的任何问题都只降级，不影响启动。
            AppLog.Error("plugins", "插件系统初始化失败，已按无插件启动", ex);
        }
    }

    /// <summary>开始监听插件目录。任何失败都只降级为"手动刷新"，绝不影响启动。</summary>
    private static void StartPluginsFolderWatcher(string pluginsRoot)
    {
        try
        {
            _pluginWatcher?.Dispose();
            _pluginWatcher = new PluginFolderWatcher(pluginsRoot);
            _pluginWatcher.Changed += (_, _) => OnPluginsFolderChanged();
            AppLog.Info("plugins", "已开始监听插件目录变化：" + pluginsRoot);
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugins", "监听插件目录失败（插件列表改为手动刷新）：" + ex.Message);
        }
    }

    /// <summary>
    /// 插件目录变化（后台线程）：重扫注册表 → 撤销消失插件的导航 → 通知设置页重建列表。
    /// 重入保护：解压一个插件包会触发多批事件，上一轮还没跑完就直接丢弃后面的通知。
    /// </summary>
    private static void OnPluginsFolderChanged()
    {
        if (Interlocked.Exchange(ref _pluginRefreshRunning, 1) == 1) return;

        try
        {
            var registry = Plugins;
            if (registry is null || !registry.IsEnabled) return;

            var result = registry.RefreshFromDisk();
            if (result.RemovedNavigationTags.Count > 0)
                PluginUiService.Instance.RemoveNavigations(result.RemovedNavigationTags);

            if (!result.HasChanges) return;

            PluginsFolderChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 一次目录变化没处理成功不算故障：下一次变化或用户手动刷新即可恢复。
            AppLog.Warn("plugins", "处理插件目录变化失败（已忽略）：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _pluginRefreshRunning, 0);
        }
    }

    /// <summary>
    /// 删除一个插件。返回 true 表示目录已经删掉；false 表示文件正被占用、已排进删除队列（重启后彻底移除）。
    ///
    /// 顺序有讲究：
    /// 1) 先写"禁用"偏好 —— 万一目录删不干净，下次启动也不会又被加载；
    /// 2) 从注册表摘掉并撤掉导航 —— 即使文件删不掉，界面也不该留一个点不开的入口；
    /// 3) 尽力直接删目录，失败就排队，下次启动在插件加载前把目录挪进回收站；
    /// 4) 插件数据（plugin-data\&lt;id&gt;）保留：用户可能只是暂时移除，重装后还能接着用。
    /// </summary>
    public static bool TryDeletePlugin(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || !PluginPaths.IsValidId(pluginId))
        {
            AppLog.Warn("plugins", "忽略非法插件 id 的删除请求：" + (pluginId ?? ""));
            return false;
        }

        var pluginsRoot = Path.Combine(Paths.Root, PluginPaths.PluginsFolderName);

        // 1) 禁用偏好：防止"目录没删掉"时下次启动又复活。
        try
        {
            SetPluginDisabledPreference(pluginId);
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugins", "写入插件禁用偏好失败（删除继续）：" + ex.Message);
        }

        // 2) 摘掉注册表 + 撤销导航（先摘导航再删文件，用户不会看到点不开的空条目）。
        try
        {
            var tags = Plugins?.Forget(pluginId) ?? Array.Empty<string>();
            if (tags.Count > 0) PluginUiService.Instance.RemoveNavigations(tags);
            else PluginUiService.Instance.RemoveNavigationOf(pluginId);
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugins", "删除插件时摘除注册表失败（继续删目录）：" + ex.Message);
        }

        // 3) 整体改名挪进回收站（不递归删除）：递归删到一半失败会留下"只剩 dll、没有 plugin.json"
        //    的半个插件，下次启动会报"缺少 plugin.json"，比删不掉更糟。挪不动说明 DLL 还被占用，排到下次启动。
        var deleted = PluginPackageInstaller.TryRemoveNow(pluginId, pluginsRoot);
        if (!deleted) AppLog.Info("plugins", "插件目录当前被占用，改为重启后移除：" + pluginId);

        if (deleted)
        {
            AppLog.Info("plugins", "已删除插件目录：" + pluginId);
        }
        else
        {
            PluginPackageInstaller.QueueDelete(pluginId, pluginsRoot);
            AppLog.Info("plugins", "插件删除已排队，重启启动器后彻底移除：" + pluginId);
        }

        // 4) 看门狗的隔离记录一并清掉：重装同一个插件时不该再被历史失败拦住。
        try { Plugins?.Watchdog?.ClearQuarantine(pluginId); } catch { }

        return deleted;
    }

    /// <summary>把插件写进 settings.json 的 disabledPlugins（删除插件前调用，防止目录残留时被重新加载）。</summary>
    private static void SetPluginDisabledPreference(string pluginId)
    {
        var settings = Settings?.Settings;
        if (settings is null) return;

        settings.Extra ??= new Dictionary<string, System.Text.Json.JsonElement>();

        var disabled = new List<string>();
        if (settings.Extra.TryGetValue("disabledPlugins", out var element) &&
            element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var value = item.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (disabled.Contains(value!, StringComparer.OrdinalIgnoreCase)) continue;
                disabled.Add(value!);
            }
        }

        if (!disabled.Contains(pluginId, StringComparer.OrdinalIgnoreCase)) disabled.Add(pluginId);
        settings.Extra["disabledPlugins"] = System.Text.Json.JsonSerializer.SerializeToElement(disabled);
        Settings!.Save();
    }

    /// <summary>用户级开关：settings.json 的扩展键 pluginsDisabled / disabledPlugins。</summary>
    private static bool? IsPluginEnabled(string id)
    {
        try
        {
            var extra = Settings?.Settings?.Extra;
            if (extra is null) return null;

            if (extra.TryGetValue("disabledPlugins", out var list) &&
                list.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String &&
                        string.Equals(item.GetString(), id, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>主窗口真正起来之后调用：看门狗归零，下次启动不再判"崩溃"。</summary>
    public static void MarkPluginsHealthy()
    {
        try
        {
            Plugins?.Watchdog?.MarkHealthy();
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugins", "看门狗标记健康失败：" + ex.Message);
        }
    }

    /// <summary>分发插件事件（启动、关闭、启动游戏等）。没有订阅者时几乎零开销。</summary>
    public static void DispatchPluginEvent(Core.Plugins.PluginEvent pluginEvent,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        try
        {
            Plugins?.Dispatch(pluginEvent, data);
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugins", "分发插件事件失败：" + pluginEvent + "：" + ex.Message);
        }
    }

    /// <summary>退出前：让插件收尾、把插件设置落盘。</summary>
    public static void ShutdownPlugins()
    {
        try
        {
            _pluginWatcher?.Dispose();
            _pluginWatcher = null;
            Plugins?.ShutdownAll();
            Plugins?.FlushSettings();
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugins", "关闭插件时出错：" + ex.Message);
        }
    }

    /// <summary>Switch the .minecraft root, re-read every instance, and keep a small history.</summary>
    public static async Task<int> SwitchGameRootAsync(string root, Action<string>? stage = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("游戏目录不能为空。");
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root.Trim()));
        stage?.Invoke("正在写入游戏目录…");

        var settings = Settings.Settings;
        settings.GameRoot = full;
        settings.SelectedInstanceId = null;
        var history = settings.GameRootHistory;
        var entry = history.FirstOrDefault(x => string.Equals(x.Path, full, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new Core.Models.GameRootHistory { Path = full };
            history.Add(entry);
        }
        entry.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        settings.GameRootHistory = history
            .OrderByDescending(x => x.LastUsed)
            .Take(8)
            .ToList();
        Settings.Save();

        stage?.Invoke("正在重新读取版本、模组与世界…");
        var instances = await Instances.ScanAsync(ct);
        LumenPluginHost.SetInstanceSnapshot(instances);
        stage?.Invoke("已切换到 " + Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) +
                       " · " + instances.Count + " 个实例");
        AppLog.Info("app", "游戏目录切换为 " + full + "，实例数 " + instances.Count);
        return instances.Count;
    }
}

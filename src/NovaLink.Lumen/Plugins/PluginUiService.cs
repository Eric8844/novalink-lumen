using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DynamicXaml.WinUI;
using Microsoft.UI.Xaml.Markup;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Plugins;

namespace NovaLink.Lumen.Plugins;

/// <summary>
/// 插件 UI 注入门面（<see cref="IPluginUi"/> 的唯一实现）。
///
/// 职责只有三件：
/// 1) 收集插件在 Initialize 阶段登记的导航项（插件加载在后台线程，UI 线程稍后才存在）；
/// 2) 主界面就绪后把导航项交给 <see cref="Views.ShellPage"/> 渲染（导航项按 order 排序）；
/// 3) 用户点开某个插件页面时，先 <c>DynamicLoader.TryLoadPri</c> 把插件自带的 .pri 挂到宿主，
///    再调用插件的 PageFactory（此时插件程序集里的 XAML 才能被解析）。
///
/// 失败一律翻译成人话：异常类型、HResult、堆栈只进日志（AppLog.Error），绝不进 UI。
/// </summary>
public sealed class PluginUiService : IPluginUi
{
    private const string LogArea = "plugin-ui";

    /// <summary>进程内唯一实例（插件加载器只认这一个门面）。</summary>
    public static PluginUiService Instance { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, PluginNavItem> _items = new(StringComparer.Ordinal);
    private readonly HashSet<string> _registeredProviderAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedPri = new(StringComparer.OrdinalIgnoreCase);

    private Func<IReadOnlyList<PluginDescriptor>>? _descriptors;
    private Views.ShellPage? _shell;

    /// <summary>宿主具备 UI 注入能力：本实现永远为 true（P0 的 NullPluginUi 才是 false）。</summary>
    public bool IsAvailable => true;

    private PluginUiService() { }

    /// <summary>由 LumenRuntime 在创建插件加载器之前接线，用于反查导航项属于哪个插件。</summary>
    public void Configure(Func<IReadOnlyList<PluginDescriptor>> descriptors) => _descriptors = descriptors;

    // ------------------------------------------------------------------
    // IPluginUi
    // ------------------------------------------------------------------

    /// <summary>插件登记导航项。可以在非 UI 线程调用：这里只入表，主界面就绪后统一发布。</summary>
    public void AddNavigation(PluginNavItem item)
    {
        try
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Tag)) return;

            if (!item.Tag.StartsWith("plugin:", StringComparison.Ordinal))
            {
                AppLog.Warn(LogArea, "忽略未以 plugin: 开头的插件导航项（避免顶掉内置导航）：" + item.Tag);
                return;
            }

            lock (_gate)
            {
                // 清单 ui.nav 与插件代码可能登记同一个 tag：清单那条没有页面工厂，
                // 不能把插件自己登记的工厂覆盖掉（否则页面会变成"没有提供可显示的页面"）。
                if (_items.TryGetValue(item.Tag, out var existing) &&
                    existing.PageFactory is not null &&
                    item.PageFactory is null)
                {
                    AppLog.Info(LogArea, "保留插件自己登记的页面工厂：" + item.Tag);
                    return;
                }

                _items[item.Tag] = item;
            }

            AppLog.Info(LogArea, "登记插件导航项：" + item.Tag + "（" + (item.Title ?? "") + "）");

            var shell = _shell;
            if (shell is not null) shell.DispatcherQueue.TryEnqueue(PublishToShell);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "登记插件导航项失败（已忽略）。", ex);
        }
    }

    // ------------------------------------------------------------------
    // 与 ShellPage 的接线
    // ------------------------------------------------------------------

    /// <summary>主界面创建后调用：把已登记的导航项补渲染出来。</summary>
    public void AttachShell(Views.ShellPage shell)
    {
        _shell = shell;
        if (shell.DispatcherQueue.HasThreadAccess) PublishToShell();
        else shell.DispatcherQueue.TryEnqueue(PublishToShell);
    }

    public void DetachShell(Views.ShellPage shell)
    {
        if (ReferenceEquals(_shell, shell)) _shell = null;
    }

    /// <summary>把当前导航项推给主界面（UI 线程）。</summary>
    public void PublishToShell()
    {
        var shell = _shell;
        if (shell is null) return;

        PluginNavItem[] items;
        lock (_gate) items = _items.Values.OrderBy(i => i.Order).ThenBy(i => i.Title, StringComparer.CurrentCulture).ToArray();

        try
        {
            shell.SetPluginNavigation(items);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "刷新插件导航失败。", ex);
        }
    }

    /// <summary>当前已登记的导航项快照（设置页/诊断用）。</summary>
    public IReadOnlyList<PluginNavItem> Snapshot()
    {
        lock (_gate) return _items.Values.OrderBy(i => i.Order).ToArray();
    }

    public bool HasNavigation(string tag)
    {
        lock (_gate) return _items.ContainsKey(tag);
    }

    /// <summary>导航项标题；找不到时退回 tag。</summary>
    public string TitleFor(string tag)
    {
        lock (_gate) return _items.TryGetValue(tag, out var item) && !string.IsNullOrWhiteSpace(item.Title)
            ? item.Title!
            : tag;
    }

    // ------------------------------------------------------------------
    // 页面创建
    // ------------------------------------------------------------------

    /// <summary>
    /// 创建插件页面实例。必须在 UI 线程调用（插件 UserControl 的构造要建 XAML 树）。
    /// 失败时返回 null，并通过 <paramref name="failureHint"/> 给出面向玩家的中文说明。
    /// </summary>
    public object? CreatePage(string tag, out string failureHint)
    {
        failureHint = "";

        if (string.IsNullOrWhiteSpace(tag))
        {
            failureHint = "没有找到要打开的插件页面。";
            return null;
        }

        PluginNavItem? item;
        lock (_gate) _items.TryGetValue(tag, out item);

        if (item is null)
        {
            failureHint = "这个插件页面现在不可用，插件可能已被停用。";
            return null;
        }

        var descriptor = FindDescriptor(tag);
        if (descriptor is not null && !EnsurePluginResources(descriptor))
        {
            failureHint = "插件的界面资源没能加载，可能需要更新这个插件。";
            return null;
        }

        try
        {
            var page = item.PageFactory?.Invoke();
            if (page is null)
            {
                failureHint = "这个插件没有提供可以显示的页面。";
                return null;
            }

            return page;
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "插件页面创建失败：" + tag, ex);
            failureHint = "插件页面加载失败，可以点【重试】再试一次；如果一直失败，请在设置里停用这个插件。";
            return null;
        }
    }

    private PluginDescriptor? FindDescriptor(string tag)
    {
        var descriptors = _descriptors;
        if (descriptors is null) return null;

        foreach (var descriptor in descriptors())
        {
            var nav = descriptor.Manifest.Ui?.Nav;
            if (nav is null) continue;
            foreach (var item in nav)
            {
                if (string.Equals(item.Tag, tag, StringComparison.Ordinal)) return descriptor;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // PRI / XAML 元数据
    // ------------------------------------------------------------------

    /// <summary>把插件自带的 .pri 注册进宿主（每个插件只做一次），并登记它的 XAML 元数据提供者。</summary>
    private bool EnsurePluginResources(PluginDescriptor descriptor)
    {
        var priPath = ResolvePriPath(descriptor);

        if (priPath is not null)
        {
            lock (_gate)
            {
                if (_loadedPri.Contains(priPath)) return true;
            }

            bool ok;
            try
            {
                ok = DynamicLoader.TryLoadPri(priPath);
            }
            catch (Exception ex)
            {
                AppLog.Error(LogArea, "插件 " + descriptor.Id + " 的资源加载抛出异常：" + priPath, ex);
                return false;
            }

            if (!ok)
            {
                AppLog.Warn(LogArea, "插件 " + descriptor.Id + " 的资源加载失败（PRI 可能已损坏或不是当前版本编译的）：" + priPath);
                return false;
            }

            lock (_gate) _loadedPri.Add(priPath);
            AppLog.Info(LogArea, "已加载插件资源：" + descriptor.Id + " -> " + Path.GetFileName(priPath));
        }

        RegisterMetadataProvider(descriptor);
        return true;
    }

    private static string? ResolvePriPath(PluginDescriptor descriptor)
    {
        var declared = descriptor.Manifest.Resources?.Pri;
        if (!string.IsNullOrWhiteSpace(declared))
        {
            var resolved = PluginManifestReader.ResolveInside(descriptor.Directory, declared!);
            if (resolved is not null && File.Exists(resolved)) return resolved;
            AppLog.Warn(LogArea, "插件 " + descriptor.Id + " 声明的 PRI 不存在：" + declared);
        }

        // 没声明就按程序集同名 .pri 猜一次（MSBuild 的默认产物名）。
        var entry = descriptor.Manifest.Entry?.Assembly;
        if (string.IsNullOrWhiteSpace(entry)) return null;

        var guessed = Path.Combine(descriptor.Directory, Path.GetFileNameWithoutExtension(entry) + ".pri");
        return File.Exists(guessed) ? guessed : null;
    }

    /// <summary>
    /// 登记插件程序集里的 IXamlMetadataProvider。
    /// 插件自带的编译型 XAML 需要它才能让宿主解析插件自己的类型（T1 档位）；
    /// 插件没生成 provider（纯代码构建 UI）时静默跳过。
    /// </summary>
    private void RegisterMetadataProvider(PluginDescriptor descriptor)
    {
        var entry = descriptor.Manifest.Entry?.Assembly;
        if (string.IsNullOrWhiteSpace(entry)) return;

        var assemblyName = Path.GetFileNameWithoutExtension(entry);
        if (string.IsNullOrWhiteSpace(assemblyName)) return;

        lock (_gate)
        {
            if (_registeredProviderAssemblies.Contains(assemblyName)) return;
        }

        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            if (assembly is null) return;

            var providers = SafeGetTypes(assembly)
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IXamlMetadataProvider).IsAssignableFrom(t))
                .ToArray();
            if (providers.Length == 0)
            {
                AppLog.Info(LogArea, "插件 " + descriptor.Id + " 没有 XAML 元数据提供者（纯代码 UI 插件属正常情况）。");
                lock (_gate) _registeredProviderAssemblies.Add(assemblyName);
                return;
            }

            foreach (var type in providers)
            {
                try
                {
                    if (Activator.CreateInstance(type) is not IXamlMetadataProvider provider) continue;
                    var token = DynamicLoader.RegisterXamlMetadataProvider(provider);
                    AppLog.Info(LogArea, "已登记插件 XAML 元数据提供者：" + descriptor.Id + " / " + type.Name + "（token=" + token + "）");
                }
                catch (Exception ex)
                {
                    AppLog.Warn(LogArea, "登记插件 XAML 元数据提供者失败：" + descriptor.Id + " / " + type.Name + "：" + ex.Message);
                }
            }

            lock (_gate) _registeredProviderAssemblies.Add(assemblyName);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "扫描插件 XAML 元数据提供者失败：" + descriptor.Id + "：" + ex.Message);
        }
    }

    private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!);
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    /// <summary>设置页停用插件后调用：把该插件登记过的导航项撤掉（不卸载程序集，重启后彻底干净）。</summary>
    public void RemoveNavigationOf(string pluginId)
    {
        var descriptors = _descriptors;
        if (descriptors is null) return;

        var descriptor = descriptors().FirstOrDefault(d =>
            string.Equals(d.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        var nav = descriptor?.Manifest.Ui?.Nav;
        if (nav is null || nav.Count == 0) return;

        lock (_gate)
        {
            foreach (var item in nav) _items.Remove(item.Tag);
        }

        PublishFromAnyThread();
    }

    /// <summary>
    /// 按导航 tag 直接撤销一项。插件目录在运行期被删掉时，描述符已经从注册表摘除，
    /// <see cref="RemoveNavigationOf(string)"/> 反查不到它，只能按 tag 清。
    /// </summary>
    public void RemoveNavigation(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;

        bool removed;
        lock (_gate) removed = _items.Remove(tag);
        if (removed) PublishFromAnyThread();
    }

    /// <summary>按 tag 批量撤销（插件被删除/目录消失时调用），一次刷新导航。</summary>
    public void RemoveNavigations(IEnumerable<string> tags)
    {
        if (tags is null) return;

        var removed = false;
        lock (_gate)
        {
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (_items.Remove(tag)) removed = true;
            }
        }

        if (removed) PublishFromAnyThread();
    }

    /// <summary>
    /// 把导航刷新投递到 UI 线程。目录监听在后台线程触发，直接调 <see cref="PublishToShell"/>
    /// 会在非 UI 线程碰可视树 —— 这里统一按 DispatcherQueue 的线程亲和性决定是否排队。
    /// </summary>
    private void PublishFromAnyThread()
    {
        var shell = _shell;
        if (shell is null) return;

        try
        {
            if (shell.DispatcherQueue.HasThreadAccess) PublishToShell();
            else shell.DispatcherQueue.TryEnqueue(PublishToShell);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "刷新插件导航失败。", ex);
        }
    }
}

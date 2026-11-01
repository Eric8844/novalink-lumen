using System.Reflection;
using System.Runtime.Loader;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 单插件加载体。设计要点（v1.4.1 设计文档 B.5.2）：
/// 1) 共享程序集必须回落到 default ALC —— 否则插件里 new 出来的 WinUI 控件/记录类型与宿主不是同一个类型，
///    实例化 UI 时会抛 InvalidCastException；NovaLink.Lumen.* 同理，插件必须复用宿主的插件契约程序集。
/// 2) 插件私有依赖先探插件目录（含 lib\ 子目录），再回落 default（宿主自带依赖）。
/// 3) 原生 DLL 无法真正隔离（Windows 进程级），只把 native\ 目录加入搜索路径。
/// 4) isCollectible: true 便于后续卸载；注意卸载后不能再使用该程序集任何类型。
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>必须走 default ALC 的程序集前缀（顺序无关）。</summary>
    private static readonly string[] SharedPrefixes =
    [
        "NovaLink.Lumen.",
        "Microsoft.WinUI",
        "Microsoft.Windows.",
        "WinRT.Runtime",
        "Microsoft.Graphics.Win2D",
        "DynamicXaml.WinUI",
        "System.",
        "Microsoft.Extensions."
    ];

    /// <summary>必须走 default ALC 的精确程序集名。</summary>
    private static readonly string[] SharedNames =
    [
        "NovaLink.Lumen",
        "NovaLink.Lumen.Core",
        "NovaLink.Lumen.Services",
        "Microsoft.WinUI",
        "WinRT.Runtime",
        "Microsoft.Windows.SDK.NET",
        "Microsoft.Graphics.Win2D",
        "DynamicXaml.WinUI",
        "System",
        "netstandard",
        "WindowsBase",
        "PresentationFramework",
        "PresentationCore"
    ];

    private readonly AssemblyDependencyResolver? _resolver;

    /// <summary>创建加载体。entryAssemblyPath 提供 .deps.json 解析能力（可为 null）。</summary>
    public PluginLoadContext(string pluginDirectory, string? entryAssemblyPath = null)
        : base("Plugin:" + Path.GetFileName(pluginDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), isCollectible: true)
    {
        PluginDirectory = Path.GetFullPath(pluginDirectory);
        _resolver = TryCreateResolver(entryAssemblyPath);
    }

    /// <summary>插件目录绝对路径。</summary>
    public string PluginDirectory { get; }

    /// <summary>判断程序集是否属于"必须共享"的宿主/框架程序集。</summary>
    public static bool IsSharedAssembly(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return false;
        foreach (var name in SharedNames)
        {
            if (string.Equals(assemblyName, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var prefix in SharedPrefixes)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>程序集解析：共享程序集交给 default ALC，其余先找插件私有目录。</summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name)) return null;

        // 共享名单：返回 null 即把解析权交回 default ALC（这是 ALC 的约定语义）。
        if (IsSharedAssembly(name)) return null;

        // 插件目录根 + lib\ 子目录（设计文档 B.2 的约定布局）。
        var local = Probe(PluginDirectory, name) ?? Probe(Path.Combine(PluginDirectory, "lib"), name);
        if (local is not null) return LoadFromAssemblyPath(local);

        // deps.json（插件带 runtimeconfig 时可用），最后回落 default ALC。
        var resolved = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) return LoadFromAssemblyPath(resolved);

        return null;
    }

    /// <summary>原生库解析：只探 native\ 子目录，找不到交给 default（进程级共享，无法隔离）。</summary>
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        if (!string.IsNullOrWhiteSpace(unmanagedDllName))
        {
            var fileName = Path.GetFileName(unmanagedDllName);
            var nativeDir = Path.Combine(PluginDirectory, "native");
            var candidate = PluginManifestReader.ResolveInside(nativeDir, fileName)
                            ?? PluginManifestReader.ResolveInside(nativeDir, fileName + ".dll");
            if (candidate is not null && File.Exists(candidate)) return LoadUnmanagedDllFromPath(candidate);
        }

        try
        {
            var resolved = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) return LoadUnmanagedDllFromPath(resolved);
        }
        catch
        {
            // 解析器异常不影响回落。
        }

        return 0;
    }

    /// <summary>在目录内查找 name.dll / name.exe / name（无扩展名）；不存在返回 null。</summary>
    private static string? Probe(string directory, string assemblyName)
    {
        if (!Directory.Exists(directory)) return null;

        string[] candidates = [assemblyName + ".dll", assemblyName + ".exe", assemblyName];
        foreach (var candidate in candidates)
        {
            var path = PluginManifestReader.ResolveInside(directory, candidate);
            if (path is not null && File.Exists(path)) return path;
        }
        return null;
    }

    private static AssemblyDependencyResolver? TryCreateResolver(string? entryAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(entryAssemblyPath) || !File.Exists(entryAssemblyPath)) return null;
        try
        {
            return new AssemblyDependencyResolver(entryAssemblyPath);
        }
        catch
        {
            // 插件没带 .deps.json 或格式不对：不致命，退回目录探测。
            return null;
        }
    }
}

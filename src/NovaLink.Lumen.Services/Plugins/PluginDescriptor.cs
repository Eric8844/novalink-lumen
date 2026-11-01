using NovaLink.Lumen.Core.Plugins;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>插件在宿主内的生命周期状态。</summary>
public enum PluginState
{
    /// <summary>已发现并通过清单校验，尚未加载。</summary>
    Discovered,

    /// <summary>程序集已加载（仅限 assembly 类插件）。</summary>
    Loaded,

    /// <summary>已实例化并完成 Initialize，正在运行。</summary>
    Initialized,

    /// <summary>被禁用（用户关闭 / 安全模式 / 连续失败被隔离 / P0 不支持 script）。</summary>
    Disabled,

    /// <summary>加载或初始化失败（失败后不再重试，直到下次启动）。</summary>
    Failed
}

/// <summary>
/// 单个插件的运行时描述。Id 与目录名一致，Manifest 一定非空（连清单都读不出来的目录根本不会登记）。
/// 宿主只通过本对象读写插件状态，不直接碰加载体。
/// </summary>
public sealed class PluginDescriptor
{
    public PluginDescriptor(string id, PluginManifest manifest, string directory)
    {
        Id = id;
        Manifest = manifest;
        Directory = directory;
    }

    /// <summary>插件 id（== 目录名）。</summary>
    public string Id { get; }

    /// <summary>已通过结构校验的清单。</summary>
    public PluginManifest Manifest { get; }

    /// <summary>插件目录绝对路径。</summary>
    public string Directory { get; }

    /// <summary>当前状态。</summary>
    public PluginState State { get; set; } = PluginState.Discovered;

    /// <summary>最近一次错误（面向用户的一句话）；无错误为 null。</summary>
    public string? LastError { get; set; }

    /// <summary>累计失败次数（来自 plugin-boot.json，跨启动累计）。</summary>
    public int FailCount { get; set; }

    /// <summary>是否已被隔离（连续失败达到阈值后不再加载）。</summary>
    public bool Quarantined { get; set; }

    /// <summary>是否已被用户/安全模式禁用。</summary>
    public bool DisabledByUser { get; set; }

    /// <summary>插件实例（Initialize 成功后才非空）。</summary>
    public ILumenPlugin? Instance { get; internal set; }

    /// <summary>插件专用加载体（assembly 类插件才有；collectible，便于后续卸载）。</summary>
    internal PluginLoadContext? LoadContext { get; set; }

    /// <summary>宿主代管的设置存储（可用于关闭时统一 Flush）。</summary>
    public PluginSettingsStore? Settings { get; internal set; }

    /// <summary>面向 UI 的显示名：清单 name 为空时退回 id。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Manifest.Name) ? Id : Manifest.Name!;

    /// <summary>是否正在运行（可以接收事件）。</summary>
    public bool IsActive => State == PluginState.Initialized;
}

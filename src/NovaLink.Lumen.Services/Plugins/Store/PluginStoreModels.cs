using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Services.Plugins.Store;

/// <summary>
/// 插件商店（发现页「启动器插件」栏）的数据模型。
/// 上架约定见 docs/STORE.md：GitHub 仓库打 topic <see cref="PluginStoreTopics.Topic"/>，
/// 仓库根目录放 plugin.json（插件清单本体）与可选的 store.json（商店元数据）。
/// 本文件只放数据契约，不含网络与 IO 逻辑（见 PluginStoreClient）。
/// </summary>
public enum PluginStoreSort
{
    /// <summary>按 star 数排序（默认，接近“推荐”）。</summary>
    Stars = 0,

    /// <summary>按最近更新时间排序。</summary>
    Updated = 1,

    /// <summary>按名称排序。</summary>
    Name = 2
}

/// <summary>一次商店查询的参数。</summary>
public sealed class PluginStoreQuery
{
    /// <summary>关键词（可为空；空表示浏览全部上架插件）。</summary>
    public string? Search { get; init; }

    /// <summary>排序方式。</summary>
    public PluginStoreSort Sort { get; init; } = PluginStoreSort.Stars;

    /// <summary>页码，从 1 开始。</summary>
    public int Page { get; init; } = 1;

    /// <summary>每页数量，1~50。</summary>
    public int PageSize { get; init; } = 18;

    /// <summary>忽略缓存强制刷新。</summary>
    public bool ForceRefresh { get; init; }
}

/// <summary>商店里的一个插件条目（已解析完仓库清单，可直接展示与安装）。</summary>
public sealed class PluginStoreEntry
{
    /// <summary>插件 id（来自仓库 plugin.json）。</summary>
    public string Id { get; init; } = "";

    /// <summary>显示名称。</summary>
    public string Name { get; init; } = "";

    /// <summary>插件版本。</summary>
    public string Version { get; init; } = "";

    /// <summary>作者（清单 author，缺失时回退到仓库 owner）。</summary>
    public string Author { get; init; } = "";

    /// <summary>简介。</summary>
    public string Description { get; init; } = "";

    /// <summary>assembly / script。</summary>
    public string Kind { get; init; } = "";

    /// <summary>插件 API 版本。</summary>
    public int ApiVersion { get; init; }

    /// <summary>最低启动器版本。</summary>
    public string? MinLauncher { get; init; }

    /// <summary>最高启动器版本。</summary>
    public string? MaxLauncher { get; init; }

    /// <summary>仓库所有者。</summary>
    public string RepoOwner { get; init; } = "";

    /// <summary>仓库名。</summary>
    public string RepoName { get; init; } = "";

    /// <summary>仓库网页地址。</summary>
    public string RepoUrl { get; init; } = "";

    /// <summary>star 数。</summary>
    public int Stars { get; init; }

    /// <summary>仓库最近更新时间（UTC）。</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>安装包下载地址（.lumenplugin）。</summary>
    public string DownloadUrl { get; init; } = "";

    /// <summary>安装包文件名（展示与诊断用）。</summary>
    public string PackageName { get; init; } = "";

    /// <summary>本机是否已安装该 id。</summary>
    public bool Installed { get; init; }

    /// <summary>本机已安装的版本（未安装时为 null）。</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>商店版本是否比本机新。</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>与本机启动器不兼容的原因（中文，null 表示兼容）。</summary>
    public string? Incompatible { get; init; }

    /// <summary>标签（store.json 可选，用于展示）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

/// <summary>一次商店查询的结果。</summary>
public sealed class PluginStorePage
{
    /// <summary>当前页条目。</summary>
    public IReadOnlyList<PluginStoreEntry> Items { get; init; } = Array.Empty<PluginStoreEntry>();

    /// <summary>GitHub 报告的命中仓库总数。</summary>
    public int TotalCount { get; init; }

    /// <summary>本次真正读到清单的仓库数。</summary>
    public int Scanned { get; init; }

    /// <summary>因缺少/损坏清单被跳过的仓库数。</summary>
    public int Skipped { get; init; }

    /// <summary>结果是否来自本地缓存。</summary>
    public bool FromCache { get; init; }

    /// <summary>给用户看的提示（限流、离线、缓存等）；null 表示无异常。</summary>
    public string? Notice { get; init; }

    /// <summary>数据时间。</summary>
    public DateTimeOffset FetchedAt { get; init; }
}

/// <summary>仓库根目录可选的 store.json：只影响商店展示与下载地址，不参与安装校验。</summary>
public sealed class PluginStoreRepoManifest
{
    /// <summary>Release 里的安装包文件名；缺省时按 &lt;插件 id&gt;.lumenplugin 推断。</summary>
    [JsonPropertyName("asset")]
    public string? Asset { get; set; }

    /// <summary>安装包完整地址（https）。给了就优先于 asset 推断。</summary>
    [JsonPropertyName("download")]
    public string? Download { get; set; }

    /// <summary>展示用标签。</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>项目主页（可选）。</summary>
    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }
}

/// <summary>上架约定的常量，UI 与文档共用同一份字面量。</summary>
public static class PluginStoreTopics
{
    /// <summary>商店扫描用的 GitHub topic。</summary>
    public const string Topic = "novalink-lumen-plugin";

    /// <summary>仓库根的插件清单文件名。</summary>
    public const string ManifestFileName = "plugin.json";

    /// <summary>仓库根的商店元数据文件名。</summary>
    public const string StoreFileName = "store.json";

    /// <summary>安装包扩展名。</summary>
    public const string PackageExtension = ".lumenplugin";

    /// <summary>缓存有效期。</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>缓存文件名（位于 DataRoot 下）。</summary>
    public const string CacheFileName = "plugin-store-cache.json";
}

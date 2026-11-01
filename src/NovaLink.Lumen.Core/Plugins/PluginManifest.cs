using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Core.Plugins;

/// <summary>
/// plugin.json 的强类型模型。字段名用 [JsonPropertyName] 显式固定为 camelCase，
/// 不依赖序列化器的命名策略；读取时配合 JsonDefaults.Options（大小写不敏感）。
/// 校验规则不在这里，见 Services/Plugins/PluginManifestReader。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>当前支持的 manifest schema 版本。</summary>
    public const int CurrentSchema = 1;

    /// <summary>导航项默认排序值，与内置导航项保持距离。</summary>
    public const int DefaultNavOrder = 500;

    /// <summary>manifest 格式版本，必须等于 <see cref="CurrentSchema"/>。</summary>
    [JsonPropertyName("schema")]
    public int Schema { get; set; }

    /// <summary>插件唯一 id，必须匹配 ^[a-z0-9][a-z0-9.\-]{2,63}$ 且等于插件目录名。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>显示名称。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>插件版本（显示与诊断用，不参与兼容判断）。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>作者。</summary>
    [JsonPropertyName("author")]
    public string? Author { get; set; }

    /// <summary>简介。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>插件 API 版本，必须等于 <see cref="PluginApiVersion.Value"/>。</summary>
    [JsonPropertyName("apiVersion")]
    public int ApiVersion { get; set; }

    /// <summary>最低启动器版本（如 1.4.1）；null 表示不限制。</summary>
    [JsonPropertyName("minLauncher")]
    public string? MinLauncher { get; set; }

    /// <summary>最高启动器版本；null 表示不限制。</summary>
    [JsonPropertyName("maxLauncher")]
    public string? MaxLauncher { get; set; }

    /// <summary>插件类型：assembly / script，见 <see cref="PluginKinds"/>。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>类型 A 的入口（assembly + type）。</summary>
    [JsonPropertyName("entry")]
    public PluginEntryManifest? Entry { get; set; }

    /// <summary>类型 B 的脚本列表（P2 使用）。</summary>
    [JsonPropertyName("scripts")]
    public List<PluginScriptManifest> Scripts { get; set; } = [];

    /// <summary>声明的能力（未声明的能力在运行时应不可用）。</summary>
    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    /// <summary>UI 声明；为 null 表示没有界面。</summary>
    [JsonPropertyName("ui")]
    public PluginUiManifest? Ui { get; set; }

    /// <summary>资源声明（PRI 等，P1 使用）。</summary>
    [JsonPropertyName("resources")]
    public PluginResourcesManifest? Resources { get; set; }

    /// <summary>加载上下文声明（私有程序集、原生 DLL）。</summary>
    [JsonPropertyName("loadContext")]
    public PluginLoadContextManifest? LoadContext { get; set; }

    /// <summary>可选的包校验和（P0 不校验，仅透传）。</summary>
    [JsonPropertyName("checksum")]
    public string? Checksum { get; set; }

    /// <summary>是否 assembly 类插件。</summary>
    [JsonIgnore]
    public bool IsAssembly => string.Equals(Kind?.Trim(), PluginKinds.Assembly, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否 script 类插件。</summary>
    [JsonIgnore]
    public bool IsScript => string.Equals(Kind?.Trim(), PluginKinds.Script, StringComparison.OrdinalIgnoreCase);

    /// <summary>导航项列表（永不返回 null）。</summary>
    [JsonIgnore]
    public IReadOnlyList<PluginNavManifest> Nav => Ui?.Nav ?? (IReadOnlyList<PluginNavManifest>)Array.Empty<PluginNavManifest>();
}

/// <summary>类型 A 的入口声明。</summary>
public sealed class PluginEntryManifest
{
    /// <summary>相对插件目录的程序集路径（不得跳出插件目录）。</summary>
    [JsonPropertyName("assembly")]
    public string Assembly { get; set; } = "";

    /// <summary>入口类型全名，必须实现 <see cref="ILumenPlugin"/>。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

/// <summary>类型 B 的脚本声明（P2 才真正执行）。</summary>
public sealed class PluginScriptManifest
{
    /// <summary>相对插件目录的脚本路径（不得跳出插件目录）。</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    /// <summary>脚本引擎，默认 jint。</summary>
    [JsonPropertyName("engine")]
    public string? Engine { get; set; } = "jint";

    /// <summary>执行时机，默认 startup。</summary>
    [JsonPropertyName("when")]
    public string? When { get; set; } = "startup";
}

/// <summary>UI 声明。</summary>
public sealed class PluginUiManifest
{
    /// <summary>UI 档位提示：auto / compiled / loose / code / none。P1 会按 T1→T2→T3 尝试。</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; } = "auto";

    /// <summary>导航项列表。</summary>
    [JsonPropertyName("nav")]
    public List<PluginNavManifest> Nav { get; set; } = [];
}

/// <summary>导航项声明。</summary>
public sealed class PluginNavManifest
{
    /// <summary>导航 tag，必须以 plugin: 开头，避免与内置 tag 冲突。</summary>
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    /// <summary>显示标题。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>排序值（越小越靠前，默认 500）。</summary>
    [JsonPropertyName("order")]
    public int Order { get; set; } = PluginManifest.DefaultNavOrder;

    /// <summary>图标 Path 数据（可选）。</summary>
    [JsonPropertyName("glyph")]
    public string? Glyph { get; set; }

    /// <summary>页面类型全名（P1 反射构造，可选）。</summary>
    [JsonPropertyName("page")]
    public string? Page { get; set; }
}

/// <summary>资源声明。</summary>
public sealed class PluginResourcesManifest
{
    /// <summary>插件自带 PRI 资源包文件名。</summary>
    [JsonPropertyName("pri")]
    public string? Pri { get; set; }
}

/// <summary>加载上下文声明。</summary>
public sealed class PluginLoadContextManifest
{
    /// <summary>私有托管程序集（相对 plugin.json 的 lib\ 目录）。</summary>
    [JsonPropertyName("privateAssemblies")]
    public List<string> PrivateAssemblies { get; set; } = [];

    /// <summary>私有原生 DLL（P4 做冲突预检；P0 仅记录）。</summary>
    [JsonPropertyName("native")]
    public List<string> Native { get; set; } = [];
}

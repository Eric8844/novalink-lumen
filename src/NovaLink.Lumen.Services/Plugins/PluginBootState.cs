using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>plugin-boot.json 的 state 取值（设计文档 B.9.3）。</summary>
public static class PluginBootStatus
{
    /// <summary>上次启动正常结束。</summary>
    public const string Healthy = "healthy";

    /// <summary>正在启动（如果下次启动仍读到 starting，说明上次启动中途崩溃）。</summary>
    public const string Starting = "starting";

    /// <summary>已判定启动崩溃循环。</summary>
    public const string Crashed = "crashed";
}

/// <summary>
/// 单个插件在本次启动中的加载阶段（plugin-boot.json 的 lastPhase 取值）。
/// 仅用于崩溃归因与诊断展示，不参与加载决策。
/// </summary>
public static class PluginBootPhases
{
    /// <summary>已标记开始加载（尚未创建实例）。</summary>
    public const string Loading = "loading";

    /// <summary>程序集/脚本已就绪，正在执行 Initialize。</summary>
    public const string Initializing = "initializing";

    /// <summary>Initialize 成功返回。</summary>
    public const string Loaded = "loaded";

    /// <summary>本次启动判定该插件此前崩溃（上次退出时仍处于加载中）。</summary>
    public const string Crashed = "crashed";
}

/// <summary>
/// 启动状态机文件模型：&lt;DataRoot&gt;\plugin-boot.json。
/// 用途：识别"启动即崩"的循环 —— 连续两次 starting 未变 healthy 就进入安全模式。
/// </summary>
public sealed class PluginBootState
{
    /// <summary>文件格式版本。</summary>
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    /// <summary>状态：healthy / starting / crashed。</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = PluginBootStatus.Healthy;

    /// <summary>连续启动尝试次数（成功启动后清零）。</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    /// <summary>上次开始加载的时间（UTC）。</summary>
    [JsonPropertyName("lastStartUtc")]
    public DateTimeOffset? LastStartUtc { get; set; }

    /// <summary>上次健康启动完成的时间（UTC），同时充当心跳。</summary>
    [JsonPropertyName("lastHealthyUtc")]
    public DateTimeOffset? LastHealthyUtc { get; set; }

    /// <summary>进入安全模式的原因（供 UI 说明）；未进入为 null。</summary>
    [JsonPropertyName("safeModeReason")]
    public string? SafeModeReason { get; set; }

    /// <summary>每个插件的累计失败信息。</summary>
    [JsonPropertyName("plugins")]
    public List<PluginBootEntry> Plugins { get; set; } = [];
}

/// <summary>plugin-boot.json 里单个插件的记录。</summary>
public sealed class PluginBootEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>累计加载尝试次数。</summary>
    [JsonPropertyName("loadAttempts")]
    public int LoadAttempts { get; set; }

    /// <summary>累计失败次数（成功健康启动后清零）。</summary>
    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    /// <summary>最近一次失败原因（截断后写入，避免文件无限膨胀）。</summary>
    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    /// <summary>是否已隔离（不再自动加载）。</summary>
    [JsonPropertyName("quarantined")]
    public bool Quarantined { get; set; }

    /// <summary>上次 UI 注入层级（P0 恒为 null，P1 记录 t1/t2/t3）。</summary>
    [JsonPropertyName("lastUiTier")]
    public string? LastUiTier { get; set; }

    /// <summary>
    /// 是否正在加载（已标记 loading、尚未成功或失败）。
    /// 下次启动如果仍读到 true，说明上次进程在插件加载阶段崩溃，用于归因到具体插件。
    /// 旧版本文件缺该字段时为 false（向后兼容）。
    /// </summary>
    [JsonPropertyName("loading")]
    public bool Loading { get; set; }

    /// <summary>最近一次记录的加载阶段（见 <see cref="PluginBootPhases"/>）；旧文件为 null。</summary>
    [JsonPropertyName("lastPhase")]
    public string? LastPhase { get; set; }
}

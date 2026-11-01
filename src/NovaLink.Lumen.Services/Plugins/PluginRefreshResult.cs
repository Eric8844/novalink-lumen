namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 一次"插件目录变化 → 重扫注册表"的差值结果。
/// App 层用它撤销消失插件的导航项，并决定给用户的提示文案。
/// </summary>
/// <param name="Added">新登记且已尝试加载的插件数。</param>
/// <param name="Removed">目录已消失、已从注册表摘除的插件数。</param>
/// <param name="Failed">新插件里加载失败的个数（失败只禁用自己，不影响列表）。</param>
/// <param name="AddedIds">新增插件的 id。</param>
/// <param name="RemovedIds">被摘除插件的 id。</param>
/// <param name="RemovedNavigationTags">被摘除插件登记过的导航 tag，需要从 UI 门面撤销。</param>
public sealed record PluginRefreshResult(
    int Added,
    int Removed,
    int Failed,
    IReadOnlyList<string> AddedIds,
    IReadOnlyList<string> RemovedIds,
    IReadOnlyList<string> RemovedNavigationTags)
{
    /// <summary>本次重扫是否真的有变化（用于决定要不要刷新界面与写日志）。</summary>
    public bool HasChanges => Added + Removed + Failed > 0;

    /// <summary>没有变化的空结果。</summary>
    public static PluginRefreshResult Empty { get; } = new(0, 0, 0, [], [], []);
}

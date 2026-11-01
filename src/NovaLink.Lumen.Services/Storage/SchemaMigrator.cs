using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;

namespace NovaLink.Lumen.Services.Storage;

/// <summary>
/// 数据 schema 迁移器。v1.4.1 只有 1 → 2 一步，且严格遵守"只加不减"：
/// 不删除、不重命名任何既有字段，保证用户回滚到 v1.4.0 时旧版读到的是
/// "多了一些它不认识的键"（被 AppSettings.cs:53 的 [JsonExtensionData] 忽略），而不是坏数据。
///
/// 失败语义（调用方依赖它）：要么全部步骤成功返回已应用步骤名，要么抛异常且
/// 不留下"改了一半"的内存对象 —— 调用方拿到异常后应放弃 Save() 并提示从备份恢复。
/// </summary>
public static class SchemaMigrator
{
    /// <summary>本版本的目标 schema。AppSettings.Schema 的默认值仍是 1（见 AppSettings.cs:8）。</summary>
    public static int CurrentSchema => 2;

    private sealed record Step(int From, int To, string Name, Action<AppSettings, string?> Apply);

    /// <summary>迁移步骤表：逐级执行，缺一级就报错而不是跳级。</summary>
    private static readonly Step[] Steps =
    [
        new(1, 2, "1→2 插件相关默认值 + schema=2", ApplyV1ToV2)
    ];

    /// <summary>
    /// 就地迁移 settings。返回值是**已应用**的步骤名列表，供调用方写日志/UI 展示。
    /// 已经是最新 schema 时返回空列表（只做版本戳补写的情况除外，见方法末尾）。
    /// </summary>
    /// <param name="settings">反序列化出来的配置对象（由 App 层负责随后 Settings.Save()）。</param>
    /// <param name="fromSchema">UpgradePlan.SourceSchema（原文件里的 schema）。</param>
    /// <param name="sourceVersion">UpgradePlan.SourceVersion，只用于日志。</param>
    public static IReadOnlyList<string> Migrate(AppSettings settings, int fromSchema, string? sourceVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var applied = new List<string>();
        // 旧 lumen.json 没写 schema 键时（v1.4.0 的实际形态），其隐含 schema 就是 1。
        var current = Math.Max(fromSchema, 1);

        if (current > CurrentSchema)
        {
            // 用户先跑过更新的版本再回退到这里：绝不回退 schema，也一个字都不改。
            AppLog.Warn("schema", "配置 schema=" + current + " 高于本版本 " + CurrentSchema + "，跳过迁移");
            return applied;
        }

        // 回滚快照：本迁移器只写 Schema / LastSeenVersion / Extra 三处，其余字段它没碰过。
        var schemaBefore = settings.Schema;
        var versionBefore = settings.LastSeenVersion;
        var extraBefore = settings.Extra is null ? null : new Dictionary<string, JsonElement>(settings.Extra);

        try
        {
            while (current < CurrentSchema)
            {
                var step = Array.Find(Steps, item => item.From == current)
                           ?? throw new InvalidOperationException(
                               "缺少 schema " + current + " → " + CurrentSchema + " 的迁移步骤");
                step.Apply(settings, sourceVersion);
                settings.Schema = step.To;
                current = step.To;
                applied.Add(step.Name);
                AppLog.Info("schema", "已应用迁移：" + step.Name + "（源版本 " + (sourceVersion ?? "unknown") + "）");
            }
        }
        catch
        {
            settings.Schema = schemaBefore;
            settings.LastSeenVersion = versionBefore;
            settings.Extra = extraBefore;
            throw;
        }

        // 收尾：schema 已到位但版本戳落后（例如上次迁移写完 schema 就被强杀）——
        // 补写版本戳，否则每次启动都会重复判定"需要升级"，白做一次备份检查。
        if (!string.Equals(settings.LastSeenVersion, VersionSourceDetector.TargetVersion, StringComparison.Ordinal))
        {
            settings.LastSeenVersion = VersionSourceDetector.TargetVersion;
            applied.Add("版本戳 → " + VersionSourceDetector.TargetVersion);
            AppLog.Info("schema", "版本戳已更新为 " + VersionSourceDetector.TargetVersion);
        }

        return applied;
    }

    /// <summary>1 → 2：只加不减。插件数据落在 plugins\ 与 plugin-boot.json（设计 B.2/B.5），配置里只补默认值。</summary>
    private static void ApplyV1ToV2(AppSettings settings, string? sourceVersion)
    {
        EnsurePluginDefaults(settings);
        settings.Schema = 2;
        settings.LastSeenVersion = VersionSourceDetector.TargetVersion;
    }

    /// <summary>
    /// 插件相关默认值通过 [JsonExtensionData] Extra 写入：
    /// 旧版读到会忽略（不会报错），v1.4.1 的插件引导可以直接拿到显式默认值。
    /// 已经存在的键不覆盖 —— 用户/更新的版本写过的值必须保留（"只加不减"）。
    /// </summary>
    private static void EnsurePluginDefaults(AppSettings settings)
    {
        settings.Extra ??= [];
        SetBoolDefault(settings.Extra, "pluginsEnabled", true);
        SetBoolDefault(settings.Extra, "pluginSafetyMode", false);
    }

    private static void SetBoolDefault(Dictionary<string, JsonElement> extra, string key, bool value)
    {
        if (extra.ContainsKey(key)) return;
        extra[key] = JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
    }
}

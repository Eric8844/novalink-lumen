using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NovaLink.Lumen.Services.Storage;

/// <summary>
/// 数据源版本探测：直接读 lumen.json 的原始 JSON，绝不经过 SettingsService。
///
/// 为什么不能用 SettingsService：它在构造函数里就 Reload()，解析失败时会把配置改名成
/// lumen.broken-&lt;ts&gt;.json 并回落到默认值 —— 那样 UpgradeGuard 就拿不到"原始现场"了。
/// 而升级前备份的全部意义就是"坏配置也要原样留证"。
///
/// 为什么用 lastSeenVersion 判定源版本：
/// - Electron 1.3.0 在 launcher/src/js/app.js:1937 写 state.app.version = "1.3.0"，所以从
///   Electron 迁移过来的配置会永久保留 "1.3.0"；C# 版从未读写这个字段。
/// - 纯 v1.4.0 原生用户该字段为 ""（AppSettings.cs:51 的默认值），因此 "空" = "来自 1.4.0"。
/// - schema 在 v1.4.0 里恒为 1，只能判断"要不要迁移"，无法区分 1.3.0 与 1.4.0 的来源。
/// </summary>
public static class VersionSourceDetector
{
    /// <summary>本版本号。与 UpdateService.CurrentVersion（LauncherChannelInfo.BaseVersion）保持一致。</summary>
    public const string TargetVersion = "1.5.0";

    /// <summary>lumen.json 里没有 lastSeenVersion 时的源版本推断值：纯 v1.4.0 原生安装。</summary>
    public const string FallbackSourceVersion = "1.4.0";

    /// <summary>lumen.json 存在但读不出来时的源版本占位值（只用于备份目录命名，避免与已知版本混叠）。</summary>
    public const string UnknownSourceVersion = "unknown";

    /// <summary>一次探测的结果。Exists=false 表示 lumen.json 根本不存在（全新安装）。</summary>
    public readonly record struct Probe(bool Exists, bool Unreadable, string? LastSeenVersion, int Schema);

    /// <summary>
    /// 读取配置文件里的版本信号。本方法吞掉所有异常：它在启动主路径上，探测失败绝不能让启动失败。
    /// </summary>
    public static Probe Inspect(string settingsFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsFile) || !File.Exists(settingsFile))
                return new Probe(false, false, null, 0);

            // FileShare.ReadWrite | Delete：日志/自动保存正在写盘时也能读到；
            // StreamReader 带 BOM 探测，避免用户用记事本另存后带 BOM 被误判成坏 JSON。
            using var stream = new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new Probe(true, true, null, 0);

            string? lastSeen = null;
            var schema = 0;
            if (document.RootElement.TryGetProperty("lastSeenVersion", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.String)
            {
                lastSeen = versionElement.GetString();
            }
            if (document.RootElement.TryGetProperty("schema", out var schemaElement) &&
                schemaElement.ValueKind == JsonValueKind.Number &&
                schemaElement.TryGetInt32(out var parsedSchema))
            {
                schema = parsedSchema;
            }
            return new Probe(true, false, lastSeen, schema);
        }
        catch (Exception ex)
        {
            // 坏 JSON 也算"存在但不可读"：这种文件更要原样备份（它是唯一的现场证据）。
            AppLog.Warn("upgrade", "读取配置失败，按不可读处理：" + ex.Message);
            return new Probe(true, true, null, 0);
        }
    }

    /// <summary>
    /// 把 lastSeenVersion 归一成 x.y.z。空 → 1.4.0（纯 v1.4.0 用户）；无法解析 → unknown。
    /// </summary>
    public static string NormalizeSourceVersion(string? lastSeenVersion)
    {
        if (string.IsNullOrWhiteSpace(lastSeenVersion))
            return FallbackSourceVersion;
        var parts = ParseParts(lastSeenVersion);
        return parts is null
            ? UnknownSourceVersion
            : parts[0].ToString(CultureInfo.InvariantCulture) + "." +
              parts[1].ToString(CultureInfo.InvariantCulture) + "." +
              parts[2].ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>四段以内版本比较。无法解析的一方按 1.4.0 兜底，保证 unknown 永远"小于"本版本。</summary>
    public static int Compare(string? left, string? right)
    {
        var a = ParseParts(left) ?? ParseParts(FallbackSourceVersion)!;
        var b = ParseParts(right) ?? ParseParts(FallbackSourceVersion)!;
        for (var i = 0; i < 3; i++)
        {
            var diff = a[i].CompareTo(b[i]);
            if (diff != 0) return diff;
        }
        return 0;
    }

    /// <summary>源版本是否已不小于本版本（版本戳判定的唯一入口）。</summary>
    public static bool IsAtLeastTarget(string? version) => Compare(version, TargetVersion) >= 0;

    /// <summary>解析 "1.3.0"、"v1.4"、"1.4.1.0"、甚至 "1.4.1-beta.2"（只取前三段数字）。</summary>
    private static int[]? ParseParts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V'))
            value = value[1..];

        var parts = value.Split('.', '-', '+');
        var numbers = new int[3];
        var parsed = 0;
        for (var i = 0; i < parts.Length && parsed < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                break;
            numbers[parsed++] = number;
        }
        return parsed == 0 ? null : numbers;
    }
}

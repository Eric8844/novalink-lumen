using System.Text.RegularExpressions;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 插件相关路径的唯一计算入口。DataRoot 由调用方传入（即 AppPaths.Root，通常是
/// %LOCALAPPDATA%\NovaLink Lumen\LauncherData），本类不读全局状态，便于测试。
/// 插件目录布局：&lt;DataRoot&gt;\plugins\&lt;id&gt;\plugin.json。
/// </summary>
public sealed class PluginPaths
{
    /// <summary>插件根目录名。</summary>
    public const string PluginsFolderName = "plugins";

    /// <summary>插件清单文件名。</summary>
    public const string ManifestFileName = "plugin.json";

    /// <summary>宿主代管的每插件设置文件名。</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>启动状态机（看门狗）文件名。</summary>
    public const string BootFileName = "plugin-boot.json";

    /// <summary>每插件独立数据目录名（脚本插件读写的 plugin-data）。</summary>
    public const string PluginDataFolderName = "plugin-data";

    /// <summary>插件 id 规则：3~64 字符，小写字母/数字/点/连字符，首字符必须是字母或数字。</summary>
    private static readonly Regex IdRegex = new(
        "^[a-z0-9][a-z0-9.\\-]{2,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public PluginPaths(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("启动器数据根目录不能为空。", nameof(dataRoot));
        DataRoot = Path.GetFullPath(dataRoot);
    }

    /// <summary>启动器数据根目录（已规范化）。</summary>
    public string DataRoot { get; }

    /// <summary>插件根目录：&lt;DataRoot&gt;\plugins。</summary>
    public string PluginsRoot => Path.Combine(DataRoot, PluginsFolderName);

    /// <summary>启动状态机文件：&lt;DataRoot&gt;\plugin-boot.json。</summary>
    public string BootFile => Path.Combine(DataRoot, BootFileName);

    /// <summary>校验插件 id 是否符合清单规则（与目录名一致由 Reader 再校验）。</summary>
    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && IdRegex.IsMatch(id);

    /// <summary>取单个插件目录；id 非法时抛异常（不静默拼出可疑路径）。</summary>
    public string PluginDirectory(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException("非法的插件 id：" + id, nameof(id));
        return Path.Combine(PluginsRoot, id!);
    }

    /// <summary>取插件清单文件路径。</summary>
    public string ManifestFile(string id) => Path.Combine(PluginDirectory(id), ManifestFileName);

    /// <summary>取插件设置文件路径。</summary>
    public string SettingsFile(string id) => Path.Combine(PluginDirectory(id), SettingsFileName);

    /// <summary>每插件独立数据根目录：&lt;DataRoot&gt;\plugin-data。</summary>
    public string PluginDataRoot => Path.Combine(DataRoot, PluginDataFolderName);

    /// <summary>取单个插件的数据目录；只计算不创建（非法 id 抛异常）。</summary>
    public string PluginDataDirectory(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException("非法的插件 id：" + id, nameof(id));
        return Path.Combine(PluginDataRoot, id!);
    }

    /// <summary>确保某插件的数据目录存在并返回其路径（脚本插件的 dataDir）。</summary>
    public string EnsurePluginDataDirectory(string id)
    {
        var directory = PluginDataDirectory(id);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>确保插件根目录存在（无插件时也建，便于用户在设置页打开目录）。</summary>
    public void EnsureRoot() => Directory.CreateDirectory(PluginsRoot);
}

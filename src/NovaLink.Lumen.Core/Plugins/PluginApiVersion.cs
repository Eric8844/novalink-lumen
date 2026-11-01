namespace NovaLink.Lumen.Core.Plugins;

/// <summary>
/// 插件 API 版本。宿主只加载 manifest 里 apiVersion 与本常量完全相等的插件；
/// 每次修改契约（增删改接口成员）都必须递增，旧插件会被明确禁用，而不是在运行时随机抛异常。
/// </summary>
public static class PluginApiVersion
{
    /// <summary>当前插件 API 版本。</summary>
    public const int Value = 1;

    /// <summary>宿主仍接受的最低插件 API 版本（目前与 Value 相同，为后续兼容留位）。</summary>
    public const int Minimum = 1;
}

/// <summary>
/// plugin.json 里 kind 字段的合法取值。
/// </summary>
public static class PluginKinds
{
    /// <summary>类型 A：托管程序集 + 入口类型（实现 <see cref="ILumenPlugin"/>）。</summary>
    public const string Assembly = "assembly";

    /// <summary>类型 B：免编译脚本（P2 阶段接入 Jint；P0 只解析、不加载）。</summary>
    public const string Script = "script";
}

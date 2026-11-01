namespace NovaLink.Lumen.Core.Plugins;

/// <summary>
/// 暴露给插件的最小账户信息：只有显示名、UUID 与头像地址。
/// 令牌（Microsoft / NovaLink / 论坛）、刷新令牌、皮肤文件路径一律不出现在这里——
/// 插件没有任何渠道通过宿主 API 拿到凭据。
/// </summary>
public sealed record PluginAccount(string DisplayName, string Uuid, string AvatarUrl);

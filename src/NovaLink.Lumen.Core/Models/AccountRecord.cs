using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Core.Models;

public sealed class AccountRecord
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "offline";
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public long ExpiresAt { get; set; }
    public string? SkinPath { get; set; }
    public string? NovalinkId { get; set; }
    public string? NovalinkCode { get; set; }
    public string? NovalinkName { get; set; }
    public string? NovalinkToken { get; set; }
    public string? NovalinkAvatar { get; set; }
    public string? NovalinkSkinUrl { get; set; }
    public string? MicrosoftAccountId { get; set; }
    // --- 外置皮肤站（LittleSkin / Yggdrasil）：令牌仍在 AccessToken 里用 SecretProtector 加密 ---
    /// <summary>登录用的邮箱或用户名，用于重新登录时复用同一条账户记录与 clientToken。</summary>
    public string? LittleSkinEmail { get; set; }
    /// <summary>Yggdrasil clientToken，刷新令牌时必须有，明文保存（它本身不是访问凭据）。</summary>
    public string? LittleSkinClientToken { get; set; }
    /// <summary>皮肤站主域名，例如 https://littleskin.cn 。</summary>
    public string? LittleSkinServer { get; set; }
    public string? ForumToken { get; set; }
    public string? Xuid { get; set; }
    public long CreatedAt { get; set; }
    public long LastUsed { get; set; }
    public MicrosoftProfile? Microsoft { get; set; }
    public List<SkinEntry>? Skins { get; set; }
    public List<SkinEntry>? Capes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MicrosoftProfile
{
    public string? Name { get; set; }
    public string? Uuid { get; set; }
    public string? Avatar { get; set; }
    public string? Skin { get; set; }
    public string? Cape { get; set; }
    public long? BoundAt { get; set; }
}

public sealed class SkinEntry
{
    public string? Id { get; set; }
    public string? State { get; set; }
    public string? Url { get; set; }
    public string? Variant { get; set; }
}

public sealed record AccountPublic(
    string Id,
    string Type,
    string Name,
    string Uuid,
    string Subtitle,
    string AvatarUrl,
    string Initial,
    bool HasMicrosoft,
    bool IsSelected);

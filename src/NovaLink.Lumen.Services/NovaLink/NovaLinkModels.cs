using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Services.NovaLink;

public sealed class NovaLinkResponse
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public string? Token { get; set; }
    public NovaLinkUser? User { get; set; }
    public LumenProfile? Lumen { get; set; }
}

public sealed class NovaLinkUser
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    public string Avatar { get; set; } = "";
    public long Created { get; set; }
    public LumenProfile? Lumen { get; set; }
}

public sealed class LumenProfile
{
    public MinecraftProfile? Minecraft { get; set; }
    public MicrosoftProfileLink? Microsoft { get; set; }
}

public sealed class MinecraftProfile
{
    public string? Skin { get; set; }
    public string? Cape { get; set; }
}

public sealed class MicrosoftProfileLink
{
    public string? Name { get; set; }
    public string? Uuid { get; set; }
    public string? Avatar { get; set; }
    public string? Skin { get; set; }
    [JsonPropertyName("bound_at")] public long BoundAt { get; set; }
}

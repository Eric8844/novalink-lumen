using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Accounts;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.Microsoft;

/// <summary>Microsoft / Xbox Live / Minecraft 登录与令牌刷新。</summary>
public sealed class MicrosoftAuthService
{
    public const string ClientId = "00000000402b5328";
    public const string Scope = "XboxLive.signin offline_access";
    public const string Redirect = "https://login.live.com/oauth20_desktop.srf";

    private readonly HttpService _http;
    private readonly AccountService _accounts;

    public MicrosoftAuthService(HttpService http, AccountService accounts)
    {
        _http = http;
        _accounts = accounts;
    }

    public string AuthorizeUrl =>
        "https://login.live.com/oauth20_authorize.srf?client_id=" + ClientId +
        "&response_type=code&scope=" + Uri.EscapeDataString(Scope) +
        "&redirect_uri=" + Uri.EscapeDataString(Redirect) + "&prompt=select_account";

    public async Task<AccountPublic> CompleteLoginAsync(string code, CancellationToken ct = default)
    {
        var tokens = await ExchangeCodeAsync(code, ct);
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new InvalidOperationException("Microsoft 没有返回有效的令牌");
        var profile = await CompleteXboxAsync(tokens.AccessToken, ct);
        return _accounts.UpsertMicrosoft(
            profile.Name,
            profile.Uuid,
            profile.MinecraftToken,
            tokens.RefreshToken,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(3600, profile.ExpiresIn) * 1000L,
            profile.Skin);
    }

    public async Task RefreshAsync(AccountRecord account, CancellationToken ct = default)
    {
        var refresh = _accounts.MicrosoftRefreshTokenFor(account);
        if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("Microsoft 登录已过期，请重新登录");
        var tokens = await RefreshTokenAsync(refresh, ct);
        var profile = await CompleteXboxAsync(tokens.AccessToken, ct);
        _accounts.UpdateMicrosoftTokens(account, profile.MinecraftToken, tokens.RefreshToken,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(3600, profile.ExpiresIn) * 1000L);
    }

    private async Task<TokenSet> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = Redirect,
            ["grant_type"] = "authorization_code",
            ["code"] = code
        };
        return await PostTokenAsync(body, ct);
    }

    private async Task<TokenSet> RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = Redirect,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope
        };
        return await PostTokenAsync(body, ct);
    }

    private async Task<TokenSet> PostTokenAsync(Dictionary<string, string> body, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(body);
        using var res = await _http.Client.PostAsync("https://login.live.com/oauth20_token.srf", content, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = JsonNode.Parse(text); } catch { }
        if (!res.IsSuccessStatusCode)
        {
            var error = node?["error_description"]?.GetValue<string>() ?? node?["error"]?.GetValue<string>() ?? text;
            throw new InvalidOperationException("Microsoft 登录失败：" + Trim(error));
        }
        return new TokenSet(
            node?["access_token"]?.GetValue<string>() ?? "",
            node?["refresh_token"]?.GetValue<string>() ?? "",
            node?["expires_in"]?.GetValue<int>() ?? 3600);
    }

    private async Task<MinecraftProfile> CompleteXboxAsync(string msAccessToken, CancellationToken ct)
    {
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + msAccessToken },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        }, ct);
        var xblToken = xbl?["Token"]?.GetValue<string>() ?? throw new InvalidOperationException("Xbox Live 登录失败");
        var uhs = xbl?["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>() ?? throw new InvalidOperationException("Xbox Live 没有返回用户标识");

        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        }, ct);
        var xstsToken = xsts?["Token"]?.GetValue<string>() ?? throw new InvalidOperationException("Xbox 安全令牌获取失败");

        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new
        {
            identityToken = "XBL3.0 x=" + uhs + ";" + xstsToken
        }, ct);
        var mcToken = mc?["access_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Minecraft 登录失败");
        var expires = mc?["expires_in"]?.GetValue<int>() ?? 86400;

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
        using var res = await _http.Client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("该 Microsoft 账户没有 Minecraft Java 版档案");
        var profile = JsonNode.Parse(text);
        var name = profile?["name"]?.GetValue<string>() ?? throw new InvalidOperationException("Minecraft 档案缺少用户名");
        var uuid = profile?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Minecraft 档案缺少 UUID");
        var skin = profile?["skins"]?[0]?["url"]?.GetValue<string>() ?? "";
        return new MinecraftProfile(name, uuid, mcToken, expires, skin);
    }

    private async Task<JsonNode?> PostJsonAsync(string url, object body, CancellationToken ct)
    {
        using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var res = await _http.Client.PostAsync(url, content, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = JsonNode.Parse(text); } catch { }
        if (!res.IsSuccessStatusCode)
        {
            var error = node?["XErr"]?.ToString() ?? node?["error"]?.ToString() ?? text;
            throw new InvalidOperationException("Xbox 登录失败：" + Trim(error));
        }
        return node;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index < 0) continue;
            result[Uri.UnescapeDataString(pair[..index])] = Uri.UnescapeDataString(pair[(index + 1)..].Replace("+", " "));
        }
        return result;
    }

    private static string Trim(string value) => value.Length > 220 ? value[..220] : value;

    private sealed record TokenSet(string AccessToken, string RefreshToken, int ExpiresIn);
    private sealed record MinecraftProfile(string Name, string Uuid, string MinecraftToken, int ExpiresIn, string Skin);
}
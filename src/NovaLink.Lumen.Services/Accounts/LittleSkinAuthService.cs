using System.Text;
using System.Text.Json;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.Accounts;

/// <summary>Yggdrasil 登录会话（LittleSkin 等外置皮肤站）。</summary>
public sealed record LittleSkinSession(string AccessToken, string ClientToken, string Uuid, string Name, long ExpiresAt);

/// <summary>
/// LittleSkin（Yggdrasil 协议）认证客户端：只负责 HTTP 与协议解析，
/// 账户写入、令牌加密与皮肤落盘都交给 AccountService。
/// 失败统一抛 <see cref="InvalidOperationException"/>，消息已经是给玩家看的中文。
/// </summary>
public sealed class LittleSkinAuthService
{
    private const string LogArea = "littleskin";

    /// <summary>LittleSkin 皮肤站主域名，登录成功后写进账户，方便以后支持更多皮肤站。</summary>
    public const string DefaultServer = "https://littleskin.cn";

    /// <summary>Yggdrasil API 根地址（authserver / sessionserver 都在下面）。</summary>
    public const string DefaultApiBase = DefaultServer + "/api/yggdrasil";

    /// <summary>Yggdrasil 令牌默认有效期 24 小时。</summary>
    private const long SessionMilliseconds = 24L * 60 * 60 * 1000;

    private readonly HttpService _http;

    public LittleSkinAuthService(HttpService http) => _http = http;

    /// <summary>生成一个随机的 clientToken（Yggdrasil 用它标识同一次登录会话）。</summary>
    public static string NewClientToken() => Guid.NewGuid().ToString("N");

    /// <summary>uuid 去掉横线并转小写；不是 32 位十六进制就返回空串。</summary>
    public static string NormalizeUuid(string? uuid)
    {
        var hex = new string((uuid ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 32 ? hex : "";
    }

    /// <summary>把 32 位 uuid 写成带横线的标准形式（Minecraft 参数和显示都用这个）。</summary>
    public static string FormatUuid(string? uuid)
    {
        var hex = NormalizeUuid(uuid);
        return hex.Length != 32
            ? (uuid ?? "")
            : hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..];
    }

    /// <summary>用邮箱/用户名 + 密码登录，返回新的访问令牌。</summary>
    public async Task<LittleSkinSession> AuthenticateAsync(string username, string password, string clientToken, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["username"] = username,
            ["password"] = password,
            ["clientToken"] = clientToken,
            ["agent"] = new Dictionary<string, object?> { ["name"] = "Minecraft", ["version"] = 1 },
            ["requestUser"] = false
        };
        using var doc = await PostAsync(DefaultApiBase + "/authserver/authenticate", body, ct).ConfigureAwait(false);
        return ReadSession(doc.RootElement, clientToken);
    }

    /// <summary>用 accessToken + clientToken 续期；令牌失效时抛出中文错误，由调用方引导重新登录。</summary>
    public async Task<LittleSkinSession> RefreshAsync(string accessToken, string clientToken, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["accessToken"] = accessToken,
            ["clientToken"] = clientToken,
            ["requestUser"] = false
        };
        using var doc = await PostAsync(DefaultApiBase + "/authserver/refresh", body, ct).ConfigureAwait(false);
        var session = ReadSession(doc.RootElement, clientToken);
        return session with { AccessToken = string.IsNullOrWhiteSpace(session.AccessToken) ? accessToken : session.AccessToken };
    }

    /// <summary>
    /// 从会话服务器读取角色的皮肤地址：properties 里的 textures 是 base64 的 JSON，
    /// 再取 textures.SKIN.url。任何一步失败都返回 null（角色可能没设皮肤）。
    /// </summary>
    public async Task<string?> ResolveSkinUrlAsync(string uuid, CancellationToken ct = default)
    {
        var id = NormalizeUuid(uuid);
        if (id.Length == 0) return null;

        try
        {
            using var res = await _http.Client.GetAsync(DefaultApiBase + "/sessionserver/session/minecraft/profile/" + id, ct).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                AppLog.Warn(LogArea, "读取角色档案失败：HTTP " + (int)res.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var entry in properties.EnumerateArray())
            {
                if (!string.Equals(GetString(entry, "name"), "textures", StringComparison.OrdinalIgnoreCase)) continue;
                var value = GetString(entry, "value");
                if (string.IsNullOrWhiteSpace(value)) continue;

                var skinUrl = ReadSkinUrlFromTextures(value!);
                if (!string.IsNullOrWhiteSpace(skinUrl)) return skinUrl;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 皮肤拿不到不影响登录：记日志，账户照常可用，只是显示默认皮肤。
            AppLog.Warn(LogArea, "解析皮肤地址失败：" + ex.Message);
        }
        return null;
    }

    /// <summary>下载皮肤原图；失败返回 null，不抛异常。</summary>
    public async Task<byte[]?> DownloadSkinAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var bytes = await _http.GetBytesAsync(url, ct).ConfigureAwait(false);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "下载皮肤失败：" + ex.Message);
            return null;
        }
    }

    private static string? ReadSkinUrlFromTextures(string base64)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var key in new[] { "SKIN", "skin" })
            {
                if (!textures.TryGetProperty(key, out var skin) || skin.ValueKind != JsonValueKind.Object) continue;
                var url = GetString(skin, "url");
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "解析皮肤数据失败：" + ex.Message);
        }
        return null;
    }

    private static LittleSkinSession ReadSession(JsonElement root, string clientToken)
    {
        var accessToken = GetString(root, "accessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("LittleSkin 没有返回登录令牌，请稍后再试。");

        var token = GetString(root, "clientToken");
        if (string.IsNullOrWhiteSpace(token)) token = clientToken;

        if (!root.TryGetProperty("selectedProfile", out var profile) || profile.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("这个账号还没有 Minecraft 角色。请先到 littleskin.cn 网站创建一个角色，再回来登录。");

        var uuid = GetString(profile, "id");
        var name = GetString(profile, "name");
        if (string.IsNullOrWhiteSpace(uuid) || NormalizeUuid(uuid).Length == 0)
            throw new InvalidOperationException("LittleSkin 返回的角色信息不完整，请稍后再试。");

        return new LittleSkinSession(
            accessToken!,
            token!,
            uuid!,
            string.IsNullOrWhiteSpace(name) ? "" : name!,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + SessionMilliseconds);
    }

    private static string? GetString(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<JsonDocument> PostAsync(string url, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await _http.Client.PostAsync(url, content, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw FriendlyError(text, (int)res.StatusCode);

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("LittleSkin 返回了无法识别的数据，请稍后再试。");
        }
    }

    /// <summary>把 Yggdrasil 的错误体翻译成中文，绝不把异常类型或堆栈带给玩家；原始错误只写日志。</summary>
    private static Exception FriendlyError(string text, int status)
    {
        string? code = null;
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            code = GetString(doc.RootElement, "error");
            message = GetString(doc.RootElement, "errorMessage");
        }
        catch (JsonException)
        {
            // 不是 JSON 的错误页（网关、CDN）就按状态码处理。
        }

        AppLog.Warn(LogArea, "认证请求失败：HTTP " + status + "，错误码 " + (string.IsNullOrWhiteSpace(code) ? "-" : code) +
                             "，后端信息 " + (string.IsNullOrWhiteSpace(message) ? "-" : message));
        return new InvalidOperationException(TranslateError(message, code, status));
    }

    private static string TranslateError(string? message, string? code, int status)
    {
        var text = (message ?? "").Trim();
        var lower = text.ToLowerInvariant();

        // 令牌失效要排在密码错误前面：两者都用 ForbiddenOperationException 返回。
        if (lower.Contains("invalid token") || lower.Contains("token expired") || lower.Contains("expired token") ||
            lower.Contains("access token") || lower.Contains("invalid session"))
            return "登录状态已过期，请重新登录。";

        // 皮肤站实际返回的是「用户名或密码错误。Incorrect username or password.」，故意放在中文提取之前。
        if (lower.Contains("incorrect") || lower.Contains("invalid credential") || lower.Contains("invalid username") ||
            lower.Contains("invalid password") || lower.Contains("forbiddenoperation"))
            return "邮箱或密码不正确，请重新输入。";

        if (lower.Contains("rate limit") || lower.Contains("too many") || status == 429)
            return "请求太频繁，请等一分钟再试。";

        if (lower.Contains("no profile") || lower.Contains("selectedprofile") || lower.Contains("character"))
            return "这个账号还没有 Minecraft 角色，请先到 littleskin.cn 创建一个角色。";

        if (lower.Contains("banned") || lower.Contains("locked"))
            return "这个账号已被 LittleSkin 暂时限制登录，请到皮肤站查看账号状态。";

        if (string.Equals(code, "IllegalArgumentException", StringComparison.OrdinalIgnoreCase))
            return "登录信息格式不正确，请检查邮箱或用户名是否填完整。";

        // 后端有时返回「中文。English.」的中英双语，只留中文部分，玩家看不到半句英文。
        var chinese = ChineseOnly(text);
        if (chinese.Length > 0) return chinese;

        if (status >= 500) return "LittleSkin 服务暂时不可用，请稍后再试。";
        if (status == 401 || status == 403) return "邮箱或密码不正确，请重新输入。";
        if (status == 400) return "登录信息不完整，请检查后重试。";
        return "LittleSkin 登录失败，请稍后再试。";
    }

    /// <summary>截取后端消息里的中文段落（中英双语消息只显示中文），没有中文返回空串。</summary>
    private static string ChineseOnly(string text)
    {
        var end = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if ((ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3000 && ch <= 0x303F) || (ch >= 0xFF00 && ch <= 0xFFEF))
                end = i;
        }
        if (end < 0) return "";
        var trimmed = text[..(end + 1)].Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }
}

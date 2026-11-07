using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.Skins;

/// <summary>
/// Mojang 官方皮肤接口：按正版玩家名解析并下载整张皮肤，或把本地 PNG 上传到正版账号。
/// 只管网络与协议，落盘、裁剪头像由 AccountService 负责；失败一律抛中文提示，不暴露 HTTP 状态码与异常类型。
/// </summary>
public sealed class MojangSkinService
{
    private const string LogArea = "skin";
    private const string ProfileByName = "https://api.mojang.com/users/profiles/minecraft/";
    private const string ProfileByUuid = "https://sessionserver.mojang.com/session/minecraft/profile/";
    private const string SkinUpload = "https://api.minecraftservices.com/minecraft/profile/skins";

    /// <summary>皮肤文件一般小于 5 KB，超过这个大小基本是下到了错误页面。</summary>
    private const int MaxSkinBytes = 512 * 1024;

    private readonly HttpService _http;

    public MojangSkinService(HttpService http)
    {
        _http = http;
    }

    /// <summary>
    /// 按正版用户名取整张皮肤 PNG：用户名 → UUID → 皮肤档案 → textures.SKIN.url → 下载。
    /// </summary>
    public async Task<byte[]> FetchSkinAsync(string rawName, CancellationToken ct = default)
    {
        var name = (rawName ?? "").Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
            throw new InvalidOperationException("正版用户名只能是 3–16 位字母、数字或下划线。");

        var uuid = await ResolveUuidAsync(name, ct);
        var skinUrl = await ResolveSkinUrlAsync(uuid, name, ct);
        return await DownloadAsync(skinUrl, name, ct);
    }

    /// <summary>把本地皮肤上传到正版账号；皮肤类型（经典/纤细）由调用方选择。</summary>
    public async Task UploadAsync(string accessToken, byte[] png, bool slim, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new SkinAuthExpiredException();

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(slim ? "slim" : "classic", Encoding.UTF8), "variant");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "skin.png");

        using var request = new HttpRequestMessage(HttpMethod.Put, SkinUpload) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.Client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        // 只记状态码和后端原文，绝不写令牌。
        AppLog.Warn(LogArea, "上传正版皮肤失败：HTTP " + (int)response.StatusCode + " " + Trim(body));
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new SkinAuthExpiredException(),
            HttpStatusCode.Forbidden => new InvalidOperationException("这个正版账号没有上传皮肤的权限，请确认账号已购买 Java 版 Minecraft。"),
            HttpStatusCode.BadRequest => new InvalidOperationException("正版服务器没有接受这张皮肤，请换一张 64×64 或 64×32 的 PNG 再试。"),
            HttpStatusCode.TooManyRequests => new InvalidOperationException("上传太频繁，请等一分钟再试。"),
            _ => new InvalidOperationException("上传皮肤失败，请稍后再试。")
        };
    }

    /// <summary>用户名 → 32 位 UUID。找不到玩家、限流、网络异常都翻译成中文。</summary>
    private async Task<string> ResolveUuidAsync(string name, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileByName + Uri.EscapeDataString(name));
        using var response = await _http.Client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("找不到名为「" + name + "」的正版玩家，请检查拼写。");
        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("查询太频繁，请等一分钟再试。");
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warn(LogArea, "查询正版玩家失败：HTTP " + (int)response.StatusCode);
            throw new InvalidOperationException("查询正版玩家信息失败，请稍后再试。");
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(text);
            var id = document.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && NormalizeUuid(id).Length == 32) return NormalizeUuid(id);
        }
        catch (JsonException)
        {
        }
        throw new InvalidOperationException("正版服务器返回的玩家信息看不懂，请稍后再试。");
    }

    /// <summary>UUID → 皮肤档案里的 textures.SKIN.url。</summary>
    private async Task<string> ResolveSkinUrlAsync(string uuid, string name, CancellationToken ct)
    {
        using var response = await _http.Client.GetAsync(ProfileByUuid + uuid, ct);
        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("「" + name + "」还没有皮肤档案，可能从未登录过游戏。");
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warn(LogArea, "读取正版皮肤档案失败：HTTP " + (int)response.StatusCode);
            throw new InvalidOperationException("读取正版皮肤档案失败，请稍后再试。");
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Array)
            {
                foreach (var property in properties.EnumerateArray())
                {
                    if (!property.TryGetProperty("name", out var nameElement) ||
                        !string.Equals(nameElement.GetString(), "textures", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var raw = property.TryGetProperty("value", out var valueElement) ? valueElement.GetString() : null;
                    var url = ReadSkinUrl(raw);
                    if (url.Length > 0) return url;
                }
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException("「" + name + "」用的是默认皮肤，没有可下载的自定义皮肤。");
    }

    /// <summary>textures 是 base64 的 JSON，里面 SKIN.url 才是整张皮肤。</summary>
    private static string ReadSkinUrl(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return "";
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("textures", out var textures)) return "";
            if (!textures.TryGetProperty("SKIN", out var skin)) return "";
            var url = skin.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            return (url ?? "").Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private async Task<byte[]> DownloadAsync(string url, string name, CancellationToken ct)
    {
        // Mojang 的贴图地址偶尔还是 http，统一走 https。
        var target = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url[7..]
            : url;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("「" + name + "」的皮肤地址不可用，请稍后再试。");

        using var response = await _http.Client.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warn(LogArea, "下载正版皮肤失败：HTTP " + (int)response.StatusCode);
            throw new InvalidOperationException("下载皮肤图片失败，请稍后再试。");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0 || bytes.Length > MaxSkinBytes || !IsSkinPng(bytes))
            throw new InvalidOperationException("下载到的皮肤不是标准的 64×64 或 64×32 PNG，已放弃。");
        return bytes;
    }

    /// <summary>PNG 签名 + IHDR 尺寸校验：只接受 64×64 或 64×32。</summary>
    public static bool IsSkinPng(byte[] data)
    {
        if (data is null || data.Length < 64) return false;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!data.AsSpan(0, 8).SequenceEqual(signature)) return false;
        if (data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R') return false;
        var width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
        return width == 64 && (height == 64 || height == 32);
    }

    /// <summary>UUID 只留十六进制字符并转小写；不是 32 位就返回空串。</summary>
    private static string NormalizeUuid(string uuid)
    {
        var hex = new string((uuid ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 32 ? hex : "";
    }

    private static string Trim(string value) => value.Length > 220 ? value[..220] : value;
}

/// <summary>正版令牌失效（需要重新登录 Microsoft 账户）。单独成类型，方便调用方刷新一次后重试。</summary>
public sealed class SkinAuthExpiredException : Exception
{
    public SkinAuthExpiredException()
        : base("正版登录状态已过期，请重新登录 Microsoft 账户后再试。")
    {
    }
}

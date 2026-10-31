using System.Net;
using System.Text;
using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.NovaLink;

public sealed class NovaLinkClient
{
    public static readonly string[] Endpoints = ["http://121.41.174.153:3210", "https://novalink.de5.net"];
    private readonly HttpService _http;

    public NovaLinkClient(HttpService http) => _http = http;

    public string Absolute(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        return Endpoints[0] + (url.StartsWith('/') ? url : "/" + url);
    }

    public async Task<NovaLinkResponse> LoginAsync(string name, string password, CancellationToken ct = default)
        => await RequestAsync<NovaLinkResponse>("/api/login", HttpMethod.Post, new { name, password }, null, ct);

    public async Task<NovaLinkResponse> RegisterAsync(string name, string password, CancellationToken ct = default)
        => await RequestAsync<NovaLinkResponse>("/api/register", HttpMethod.Post, new { name, password }, null, ct);

    public async Task<NovaLinkResponse> MeAsync(string token, CancellationToken ct = default)
        => await RequestAsync<NovaLinkResponse>("/api/me", HttpMethod.Get, null, token, ct);

    public async Task<NovaLinkResponse> BindMicrosoftAsync(string token, string name, string uuid, string avatar, string skin, CancellationToken ct = default)
        => await RequestAsync<NovaLinkResponse>("/api/lumen/microsoft", HttpMethod.Post, new { name, uuid, avatar, skin }, token, ct);

    public async Task<NovaLinkResponse> UnbindMicrosoftAsync(string token, CancellationToken ct = default)
        => await RequestAsync<NovaLinkResponse>("/api/lumen/microsoft", HttpMethod.Delete, null, token, ct);

    public async Task<NovaLinkResponse> UploadSkinAsync(string token, byte[] png, CancellationToken ct = default)
        => await RequestAsync<NovaLinkResponse>("/api/lumen/skin", HttpMethod.Post, new { skin = "data:image/png;base64," + Convert.ToBase64String(png) }, token, ct);

    public async Task<T> RequestAsync<T>(string route, HttpMethod method, object? body, string? token, CancellationToken ct = default, int timeoutSeconds = 20) where T : class, new()
    {
        Exception? last = null;
        foreach (var baseUrl in Endpoints)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                using var req = new HttpRequestMessage(method, baseUrl + route);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("User-Agent", HttpService.UserAgent);
                if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                if (body is not null)
                    req.Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json");
                using var res = await _http.Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var text = await res.Content.ReadAsStringAsync(timeout.Token);
                if (!res.IsSuccessStatusCode)
                {
                    var message = ErrorMessage(text) ?? ("HTTP " + (int)res.StatusCode);
                    if ((int)res.StatusCode < 500 && res.StatusCode != HttpStatusCode.RequestTimeout) throw new ApiException((int)res.StatusCode, message);
                    last = new ApiException((int)res.StatusCode, message);
                    continue;
                }
                return JsonSerializer.Deserialize<T>(text, JsonDefaults.Options) ?? new T();
            }
            catch (ApiException) { throw; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { last = new TimeoutException("连接超时"); }
            catch (Exception ex) { last = ex; }
        }
        throw new ApiException(0, "无法连接 NovaLink 账户服务：" + (last?.Message ?? "未知错误"));
    }

    private static string? ErrorMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var key in new[] { "msg", "error", "message" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch { }
        return null;
    }
}

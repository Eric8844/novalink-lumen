using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;

namespace NovaLink.Lumen.Services.Http;

public sealed class HttpService
{
    public HttpClient Client { get; }
    public const string UserAgent = "NovaLinkLumen/1.4.0 (Windows NT 10.0; Win64; x64)";

    public HttpService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 32
        };
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var res = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, "HTTP " + (int)res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }

    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        using var res = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, ExtractError(text, (int)res.StatusCode));
        return JsonSerializer.Deserialize<T>(text, JsonDefaults.Options);
    }

    public async Task DownloadFileAsync(string url, string destination, IProgress<DownloadProgress>? progress, string stage, CancellationToken ct = default)
    {
        using var res = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, "HTTP " + (int)res.StatusCode);
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var source = await res.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
        var buffer = new byte[131072];
        long received = 0;
        var started = DateTime.UtcNow;
        var lastReport = DateTime.UtcNow;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 180 || received == total)
            {
                lastReport = now;
                var seconds = Math.Max(0.2, (now - started).TotalSeconds);
                var percent = total > 0 ? received * 100.0 / total : 0;
                progress?.Report(new DownloadProgress(stage, percent, received, total, received / seconds,
                    total > 0 ? $"{received / 1048576.0:F1} / {total / 1048576.0:F1} MB" : $"{received / 1048576.0:F1} MB"));
            }
        }
    }

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        using var res = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, "HTTP " + (int)res.StatusCode);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<T?> PostJsonAsync<T>(string url, object body, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json");
        using var res = await Client.PostAsync(url, content, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, ExtractError(text, (int)res.StatusCode));
        return JsonSerializer.Deserialize<T>(text, JsonDefaults.Options);
    }

    private static string ExtractError(string text, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            foreach (var key in new[] { "msg", "error", "message", "error_description" })
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString()!;
        }
        catch { }
        return "HTTP " + status;
    }
}

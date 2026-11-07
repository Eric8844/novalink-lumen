using System.Buffers.Binary;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.Skins;

/// <summary>
/// 账号皮肤贴图解析：把账号解析成本地 64x64（或 64x32）皮肤 PNG 的绝对路径，供主页 3D 人物模型渲染。
/// 解析顺序：账号自带的 SkinPath → 本地缓存 → 按账号类型下载 → 校验 PNG 与尺寸 → 原子落盘。
/// 本类不抛异常：网络/IO/数据异常一律 catch 掉并返回 null，调用方退化成内置默认皮肤。
/// </summary>
public sealed class SkinTextureService
{
    private const string LogArea = "skin";

    /// <summary>皮肤文件一般小于 5 KB，512 KB 足够，超过必然是下载到了错误页面。</summary>
    private const long MaxSkinBytes = 512 * 1024;

    /// <summary>缓存超过这个小时数就在返回旧文件的同时后台刷新一次。</summary>
    private const int CacheRefreshHours = 12;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly HttpService _http;
    private readonly string _cacheRoot;

    // 正在后台刷新的缓存文件，避免同一次刷新被重复排队。
    private readonly object _refreshGate = new();
    private readonly HashSet<string> _refreshing = new(StringComparer.OrdinalIgnoreCase);

    public SkinTextureService(HttpService http, string dataRoot)
    {
        _http = http;
        var root = string.IsNullOrWhiteSpace(dataRoot) ? AppContext.BaseDirectory : dataRoot;
        _cacheRoot = Path.Combine(root, "skins");
    }

    /// <summary>
    /// 返回账号皮肤 PNG 的本地绝对路径，拿不到就返回 null（调用方用内置默认皮肤兜底）。
    /// 缓存命中但已过期时，先返回旧缓存，再在后台悄悄刷新，不阻塞调用方。
    /// </summary>
    public async Task<string?> EnsureAsync(AccountRecord? account, CancellationToken ct = default)
    {
        if (account is null) return null;

        try
        {
            var cachePath = GetCachePath(account);

            // 1) 账号自带的皮肤文件（微软/NovaLink 登录时下载过的那种）优先，合法就直接用。
            foreach (var candidate in EnumerateLocalCandidates(account))
            {
                if (await IsValidSkinFileAsync(candidate, ct).ConfigureAwait(false)) return Path.GetFullPath(candidate);
            }

            // 2) 本地缓存命中：过期的话先把旧文件还给调用方，再后台刷新。
            if (await IsValidSkinFileAsync(cachePath, ct).ConfigureAwait(false))
            {
                var refreshUrl = ResolveDownloadUrl(account);
                if (refreshUrl is not null && IsCacheStale(cachePath)) StartBackgroundRefresh(cachePath, refreshUrl);
                return cachePath;
            }

            // 3) 缓存没有或不合法：按账号类型下载一次。
            var url = ResolveDownloadUrl(account);
            if (url is null) return null;

            var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
            if (bytes is null) return null;

            return await WriteCacheAsync(cachePath, bytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 调用方取消（切换账号/关页面），安静返回即可。
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "解析账号皮肤失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>缓存文件路径：&lt;dataRoot&gt;\skins\&lt;账号 Id&gt;.png（账号 Id 里不安全的字符会剔除）。</summary>
    private string GetCachePath(AccountRecord account)
    {
        var safe = new string((account.Id ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(_cacheRoot, safe + ".png");
    }

    /// <summary>
    /// 账号自带的皮肤路径候选：优先 SkinPath 原路径，其次 &lt;dataRoot&gt;\skins\ 下的同名文件
    /// （旧版本把 SkinPath 记成别的数据根时，AccountService 也是这样迁移的）。
    /// </summary>
    private IEnumerable<string> EnumerateLocalCandidates(AccountRecord account)
    {
        var skinPath = account.SkinPath;
        if (string.IsNullOrWhiteSpace(skinPath)) yield break;

        yield return skinPath;

        var fileName = Path.GetFileName(skinPath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var migrated = Path.Combine(_cacheRoot, fileName);
            if (!string.Equals(Path.GetFullPath(migrated), Path.GetFullPath(skinPath), StringComparison.OrdinalIgnoreCase))
                yield return migrated;
        }
    }

    /// <summary>按账号类型给出皮肤下载地址；离线账号或不安全的地址返回 null。</summary>
    private static string? ResolveDownloadUrl(AccountRecord account)
    {
        var type = (account.Type ?? "").Trim().ToLowerInvariant();
        if (type == "microsoft")
        {
            var uuid = NormalizeUuid(account.Uuid);
            if (uuid.Length == 0) uuid = NormalizeUuid(account.Microsoft?.Uuid);
            // mc-heads 的 /skin/ 返回整张皮肤；没拿到 uuid 就没法按 uuid 取皮肤。
            return uuid.Length == 0 ? null : "https://mc-heads.net/skin/" + uuid;
        }

        if (type == "novalink")
        {
            var raw = (account.NovalinkSkinUrl ?? "").Trim();
            if (raw.Length == 0 || raw.StartsWith("/", StringComparison.Ordinal)) return null; // 相对路径跳过
            return Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? raw
                : null;
        }

        // offline 等其它类型没有服务端皮肤。
        return null;
    }

    /// <summary>uuid 去掉横线并转小写；不是 32 位十六进制就返回空串。</summary>
    private static string NormalizeUuid(string? uuid)
    {
        var hex = new string((uuid ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 32 ? hex : "";
    }

    private static bool IsCacheStale(string cachePath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(cachePath) < DateTime.UtcNow.AddHours(-CacheRefreshHours);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>后台刷新缓存：不阻塞调用方，异常只记日志；同一文件同时只会有一个刷新在跑。</summary>
    private void StartBackgroundRefresh(string cachePath, string url)
    {
        lock (_refreshGate)
        {
            if (!_refreshing.Add(cachePath)) return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await DownloadAsync(url, CancellationToken.None).ConfigureAwait(false);
                if (bytes is not null) await WriteCacheAsync(cachePath, bytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogArea, "后台刷新皮肤缓存失败：" + ex.Message);
            }
            finally
            {
                lock (_refreshGate) _refreshing.Remove(cachePath);
            }
        });
    }

    /// <summary>下载并校验；不合法（非 PNG、尺寸不对、过大）返回 null。</summary>
    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        var bytes = await _http.GetBytesAsync(url, ct).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            AppLog.Warn(LogArea, "皮肤下载返回空内容：" + url);
            return null;
        }

        if (bytes.Length > MaxSkinBytes)
        {
            AppLog.Warn(LogArea, "皮肤文件过大（" + bytes.Length + " 字节，上限 " + MaxSkinBytes + "），已丢弃：" + url);
            return null;
        }

        if (!TryGetSkinSize(bytes, out var width, out var height))
        {
            AppLog.Warn(LogArea, "下载到的内容不是 64x64/64x32 的合法 PNG，已丢弃：" + url);
            return null;
        }

        AppLog.Info(LogArea, "皮肤已下载：" + width + "x" + height + "，" + bytes.Length + " 字节，" + url);
        return bytes;
    }

    /// <summary>原子落盘：先写同目录临时文件，再 File.Move 覆盖，避免半截文件被读到。</summary>
    private async Task<string?> WriteCacheAsync(string cachePath, byte[] bytes, CancellationToken ct)
    {
        var tmp = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, cachePath, true);
            return cachePath;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "写入皮肤缓存失败：" + ex.Message);
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
                // 临时文件清理失败不影响结果。
            }
        }
    }

    private static async Task<bool> IsValidSkinFileAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return false;

            var header = new byte[24];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            var read = 0;
            while (read < header.Length)
            {
                var got = await stream.ReadAsync(header.AsMemory(read), ct).ConfigureAwait(false);
                if (got == 0) break;
                read += got;
            }

            return read == header.Length && TryGetSkinSize(header, out _, out _);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "检查皮肤文件失败（" + path + "）：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 只用文件头判断是不是合法皮肤：8 字节 PNG 签名 + IHDR 里的宽高为 64x64 或 64x32。
    /// 不引入 System.Drawing（Windows 专用且对损坏文件容易抛原生异常）。
    /// </summary>
    private static bool TryGetSkinSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 24) return false;
        if (!data[..8].SequenceEqual(PngSignature)) return false;
        if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R') return false;

        width = BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
        height = BinaryPrimitives.ReadInt32BigEndian(data[20..24]);
        return width == 64 && (height == 64 || height == 32);
    }
}

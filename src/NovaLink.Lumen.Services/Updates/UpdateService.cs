using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.Updates;

public sealed record LauncherUpdate(bool Available, string Current, string Latest, string Notes, string DownloadUrl, long Size, string AssetName);

public sealed class UpdateService
{
    private const string LogArea = "updates";

    /// <summary>数值版本号，只用于比较；界面显示一律用 LauncherChannelInfo.DisplayVersion。</summary>
    public const string CurrentVersion = LauncherChannelInfo.BaseVersion;

    /// <summary>启动器主程序集的 InformationalVersion（形如 1.5.0-preview.1），通道自检与预览版序号比较都要用。
    /// 注意必须取入口程序集：本类住在 Services 里，而带 -preview.N 后缀的版本号只写在 App 工程，
    /// 取 typeof(UpdateService).Assembly 会永远得到纯数字 1.5.0，导致每个 preview 版本都被当成"有新版"。</summary>
    private static readonly string BuildInformationalVersion =
        (Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>更新源：按通道拼仓库地址。测试版指向测试版仓库，正式版指向正式版仓库，互不交叉。</summary>
    private static readonly string LatestReleaseApi =
        "https://api.github.com/repos/" + LauncherChannelInfo.Repository + "/releases/latest";

    private readonly AppPaths _paths;
    private readonly HttpService _http;

    public UpdateService(AppPaths paths, HttpService http)
    {
        _paths = paths;
        _http = http;
        VerifyChannelConsistency();
    }

    /// <summary>
    /// 通道一致性自检：程序集 InformationalVersion 里的 "preview" 必须与编译常量的通道一致。
    /// 有人手工 -p:Version=… 编了个标签对不上的包时，这里只告警不抛异常 —— 更新器坏掉不该拖垮启动。
    /// </summary>
    private static void VerifyChannelConsistency()
    {
        var versionSaysPreview = BuildInformationalVersion.Contains("preview", StringComparison.OrdinalIgnoreCase);
        if (versionSaysPreview != LauncherChannelInfo.IsPreview)
        {
            AppLog.Warn(LogArea, "通道与程序集版本不一致：InformationalVersion=" + BuildInformationalVersion +
                "，编译通道=" + LauncherChannelInfo.ChannelLabel +
                "。检查是否漏了 -p:NovaLinkChannel=Stable（或误加）。");
        }
    }

    public async Task<LauncherUpdate> CheckAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(LatestReleaseApi, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        // 防串台的第一道门：通道不匹配的 Release 直接忽略。
        // 测试版客户端不会把正式版当成"新版本"，正式版客户端也永远不会被 pre-release 覆盖。
        if (LauncherChannelInfo.IsPreview != IsPreviewVersion(tag))
        {
            AppLog.Info(LogArea, "忽略跨通道更新：当前通道=" + LauncherChannelInfo.ChannelLabel +
                "，Release tag=" + tag + "（只接受同通道的版本）");
            return new LauncherUpdate(false, LauncherChannelInfo.DisplayVersion, "", notes, "", 0, "");
        }

        var version = ExtractVersion(tag) ?? CurrentVersion;
        var available = Compare(version, CurrentVersion) > 0;
        // 同一数值版本内的预览版推进（preview.1 -> preview.2）：数值比较相等，靠 preview 序号判断。
        if (!available && Compare(version, CurrentVersion) == 0 && LauncherChannelInfo.IsPreview)
            available = PreviewOrdinal(tag) > PreviewOrdinal(BuildInformationalVersion);

        var latestDisplay = string.IsNullOrWhiteSpace(tag) ? version : tag.TrimStart('v', 'V');

        string url = "", name = "";
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var entries = assets.EnumerateArray().Select(a => new
            {
                Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
                Size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0
            }).ToList();
            // 资产名过滤：只认带 WinUI 或 -Setup-x.y.z 的 exe，防止同仓库里的 Electron 安装包
            // 被当成本机更新。安装包命名约定 NovaLinkLumen-WinUI-Setup-1.5.0.exe。
            var asset = entries.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                (a.Name.Contains("WinUI", StringComparison.OrdinalIgnoreCase) ||
                 Regex.IsMatch(a.Name, @"-Setup-\d+\.\d+\.\d+", RegexOptions.IgnoreCase)));
            if (asset is not null && available)
            {
                url = asset.Url;
                name = asset.Name;
                size = asset.Size;
            }
        }

        // Available 只表示"通道内确实有更新的版本"。安装包可能还没传上来（url 为空），
        // 那种情况交给 UpdateDialog 提示"暂未上传安装包"，不要伪装成"已是最新"。
        return new LauncherUpdate(available, LauncherChannelInfo.DisplayVersion, latestDisplay, notes, url, size, name);
    }

    public async Task<string> DownloadAsync(LauncherUpdate update, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (!update.Available || string.IsNullOrWhiteSpace(update.DownloadUrl))
            throw new InvalidOperationException("当前没有可用的启动器更新。");
        var dir = Path.Combine(_paths.Cache, "updates");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, string.IsNullOrWhiteSpace(update.AssetName) ? "NovaLinkLumen-WinUI-Setup-" + update.Latest + ".exe" : update.AssetName);
        if (File.Exists(target)) File.Delete(target);
        await _http.DownloadFileAsync(update.DownloadUrl, target, progress, "update", ct);
        return target;
    }

    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "/S",
            UseShellExecute = true
        });
    }

    /// <summary>Release tag 是否属于测试版通道（-preview / -beta / -rc 后缀）。</summary>
    public static bool IsPreviewVersion(string tag)
    {
        return Regex.IsMatch(tag ?? "", @"-(preview|beta|rc)", RegexOptions.IgnoreCase);
    }

    /// <summary>取预览版序号：1.5.0-preview.2 -> 2；只写 -preview 没序号 -> 1；不是预览版 -> 0。</summary>
    private static int PreviewOrdinal(string versionLike)
    {
        var match = Regex.Match(versionLike ?? "", @"-(?:preview|beta|rc)(?:[.-](\d+))?", RegexOptions.IgnoreCase);
        if (!match.Success) return 0;
        return match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 1;
    }

    private static string? ExtractVersion(string tag)
    {
        var match = Regex.Match(tag ?? "", @"(\d+)\.(\d+)\.(\d+)");
        return match.Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}" : null;
    }

    private static int Compare(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pa = Parts(a);
        var pb = Parts(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}

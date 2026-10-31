using NovaLink.Lumen.Core;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Accounts;
using NovaLink.Lumen.Services.Forum;
using NovaLink.Lumen.Services.Home;
using NovaLink.Lumen.Services.Http;
using NovaLink.Lumen.Services.Minecraft;
using NovaLink.Lumen.Services.Microsoft;
using NovaLink.Lumen.Services.Java;
using NovaLink.Lumen.Services.Modrinth;
using NovaLink.Lumen.Services.News;
using NovaLink.Lumen.Services.NovaLink;
using NovaLink.Lumen.Services.Storage;
using NovaLink.Lumen.Services.Updates;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NovaLink.Lumen;

public static class LumenRuntime
{
    public static AppPaths Paths { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static SecretProtector Secrets { get; private set; } = null!;
    public static HttpService Http { get; private set; } = null!;
    public static NovaLinkClient NovaLink { get; private set; } = null!;
    public static AccountService Accounts { get; private set; } = null!;
    public static MicrosoftAuthService Microsoft { get; private set; } = null!;
    public static VersionManifestService Manifest { get; private set; } = null!;
    public static InstanceService Instances { get; private set; } = null!;
    public static NewsService News { get; private set; } = null!;
    public static JavaService Java { get; private set; } = null!;
    public static JavaInstallerService JavaInstaller { get; private set; } = null!;
    public static LaunchService Launch { get; private set; } = null!;
    public static LoaderService Loaders { get; private set; } = null!;
    public static VersionInstallService Installer { get; private set; } = null!;
    public static ModrinthService Modrinth { get; private set; } = null!;
    public static ModpackService Modpacks { get; private set; } = null!;
    public static ForumService Forum { get; private set; } = null!;
    public static HeroTaglineService HeroTaglines { get; private set; } = null!;
    public static HeroArtService HeroArt { get; private set; } = null!;
    public static LibraryService Library { get; private set; } = null!;
    public static UpdateService Updates { get; private set; } = null!;

    public static void Initialize()
    {
        Paths = new AppPaths();
        Paths.EnsureDirectories();
        AppLog.Initialize(Paths.Logs);
        Settings = new SettingsService(Paths);
        Secrets = new SecretProtector();
        Http = new HttpService();
        NovaLink = new NovaLinkClient(Http);
        Accounts = new AccountService(Settings, Secrets, NovaLink);
        Microsoft = new MicrosoftAuthService(Http, Accounts);
        Manifest = new VersionManifestService(Paths, Http);
        Instances = new InstanceService(Settings, Manifest);
        News = new NewsService(Paths, Http);
        Java = new JavaService(Paths, Settings);
        JavaInstaller = new JavaInstallerService(Paths, Settings, Java, Http);
        Launch = new LaunchService(Paths, Settings, Java, Accounts, Secrets, Microsoft, JavaInstaller);
        Loaders = new LoaderService(Paths, Settings, Http, Manifest, Instances, Launch, Java);
        Installer = new VersionInstallService(Paths, Settings, Http, Manifest, Instances, Launch);
        Modrinth = new ModrinthService(Http)
        {
            MaxParallelChunks = Math.Clamp(Settings.Settings.DownloadThreads, 2, 8)
        };
        Modpacks = new ModpackService(Paths, Settings, Http, Manifest, Instances, Launch);
        Forum = new ForumService(NovaLink, Accounts);
        HeroTaglines = new HeroTaglineService(Paths, Http);
        HeroArt = new HeroArtService(Paths, Http, News);
        Library = new LibraryService(Paths);
        Updates = new UpdateService(Paths, Http);
        AppLog.Info("app", "NovaLink Lumen 启动，数据目录：" + Paths.Root);
    }

    /// <summary>Switch the .minecraft root, re-read every instance, and keep a small history.</summary>
    public static async Task<int> SwitchGameRootAsync(string root, Action<string>? stage = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("游戏目录不能为空。");
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root.Trim()));
        stage?.Invoke("正在写入游戏目录…");

        var settings = Settings.Settings;
        settings.GameRoot = full;
        settings.SelectedInstanceId = null;
        var history = settings.GameRootHistory;
        var entry = history.FirstOrDefault(x => string.Equals(x.Path, full, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new Core.Models.GameRootHistory { Path = full };
            history.Add(entry);
        }
        entry.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        settings.GameRootHistory = history
            .OrderByDescending(x => x.LastUsed)
            .Take(8)
            .ToList();
        Settings.Save();

        stage?.Invoke("正在重新读取版本、模组与世界…");
        var instances = await Instances.ScanAsync(ct);
        stage?.Invoke("已切换到 " + Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) +
                       " · " + instances.Count + " 个实例");
        AppLog.Info("app", "游戏目录切换为 " + full + "，实例数 " + instances.Count);
        return instances.Count;
    }
}
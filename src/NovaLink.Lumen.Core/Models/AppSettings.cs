using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Core.Models;

public sealed class AppSettings
{
    public int Schema { get; set; } = 1;
    public string GameRoot { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public string JavaArgs { get; set; } = "";
    public int Memory { get; set; }
    public string MemoryMode { get; set; } = "auto";
    public bool MemoryOptimize { get; set; } = true;
    public int MinMemory { get; set; } = 512;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public bool MinimizeOnLaunch { get; set; } = true;
    public bool ShowConsole { get; set; } = true;
    public string Language { get; set; } = "zh_cn";
    public bool AutoConfigGame { get; set; } = true;
    public bool DefaultIsolation { get; set; } = true;
    public string Theme { get; set; } = "dark";
    public string Accent { get; set; } = "aurora";
    public string BackgroundKind { get; set; } = "default";
    public string BackgroundPath { get; set; } = "";
    public double BackgroundScale { get; set; } = 1.0;
    public double BackgroundOffsetX { get; set; }
    public double BackgroundOffsetY { get; set; }
    public bool BackgroundVideoSound { get; set; }
    public double BackgroundDim { get; set; } = 0.42;
    public List<string> MusicTracks { get; set; } = [];
    public string MusicMode { get; set; } = "list";
    public double MusicVolume { get; set; } = 0.65;
    public bool MusicAutoplay { get; set; } = true;
    /// <summary>记住上次播放的曲目与进度；下次启动时直接接着放/接着停。</summary>
    public bool MusicRememberPosition { get; set; } = true;
    /// <summary>上次退出时选中的曲目序号（-1 表示没有）。</summary>
    public int MusicLastIndex { get; set; } = -1;
    /// <summary>上次退出时的播放位置（秒）。</summary>
    public double MusicLastPositionSeconds { get; set; }
    /// <summary>上次退出时是否正在播放；为 false 时下次启动只恢复位置、不自动播放。</summary>
    public bool MusicLastWasPlaying { get; set; }
    public string GlassQuality { get; set; } = "auto";
    public bool GlassMotion { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public bool KeepLauncherOpen { get; set; } = true;
    public List<AccountRecord> Accounts { get; set; } = [];
    public string? SelectedAccountId { get; set; }
    public string? SelectedInstanceId { get; set; }
    public bool OnboardingDone { get; set; }
    public int DownloadConcurrency { get; set; } = 10;
    public int DownloadThreads { get; set; } = 6;
    public string DownloadSource { get; set; } = "auto";
    public Dictionary<string, InstanceMeta> InstanceMeta { get; set; } = [];
    public List<GameRootHistory> GameRootHistory { get; set; } = [];
    public List<string> RecentProjects { get; set; } = [];
    public string LastSeenVersion { get; set; } = "";

    /// <summary>实验性功能：启动器插件（默认关闭）。开启后才会加载插件、显示插件商店与插件设置页。</summary>
    public bool ExperimentalPlugins { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Accounts;
using NovaLink.Lumen.Services.Java;
using NovaLink.Lumen.Services.Microsoft;
using NovaLink.Lumen.Services.Storage;
using NovaLink.Lumen.Services.Updates;

namespace NovaLink.Lumen.Services.Minecraft;

public enum GamePhase { Idle, Preparing, Starting, Running, Exited, Crashed }

public sealed record GameState(
    GamePhase Phase,
    string InstanceId,
    string InstanceName,
    int Pid,
    DateTimeOffset StartedAt,
    string Message,
    int ExitCode = 0,
    CrashDiagnosis? Reason = null);

public sealed class LaunchService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly JavaService _java;
    private readonly AccountService _accounts;
    private readonly SecretProtector _secret;
    private readonly MicrosoftAuthService? _microsoft;
    private readonly JavaInstallerService? _javaInstaller;
    private Process? _process;
    private DateTimeOffset _startedAt;
    private string? _playingInstanceId;

    public event Action<string>? Output;
    public event Action<int>? Exited;
    public event Action<GameState>? StateChanged;
    public event Action<string, double, string>? Stage;

    public GameState State { get; private set; } =
        new(GamePhase.Idle, "", "", 0, default, "");

    public LaunchService(AppPaths paths, SettingsService settings, JavaService java, AccountService accounts, SecretProtector secret,
        MicrosoftAuthService? microsoft = null, JavaInstallerService? javaInstaller = null)
    {
        _paths = paths;
        _settings = settings;
        _java = java;
        _accounts = accounts;
        _secret = secret;
        _microsoft = microsoft;
        _javaInstaller = javaInstaller;
    }

    public bool IsRunning => _process is { HasExited: false };

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                State = State with { Message = "正在关闭游戏…" };
                StateChanged?.Invoke(State);
                _process.Kill(true);
            }
        }
        catch (Exception ex) { AppLog.Warn("launch", "结束进程失败：" + ex.Message); }
    }

    public async Task<int> LaunchAsync(InstanceSummary instance, AccountRecord account, CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("游戏已经在运行");
        var startedAt = DateTimeOffset.Now;
        _startedAt = startedAt;
        SetState(GamePhase.Preparing, instance.Id, instance.Name, 0, startedAt, "正在检查 Java 与启动参数");
        Stage?.Invoke("准备启动", 6, "正在检查 Java 与启动参数");

        var root = _settings.Settings.GameRoot;
        var gameDir = string.IsNullOrWhiteSpace(instance.GameDir) ? root : instance.GameDir;
        Directory.CreateDirectory(gameDir);
        // Skip the first-run accessibility onboarding and start in Chinese. This is written
        // into the instance's own game directory, so isolated and shared instances both work.
        EnsureClientOptions(gameDir);

        var version = await ResolveVersionAsync(instance.Id, ct);
        if (version is null) throw new InvalidOperationException("找不到版本配置：" + instance.Id);
        if (string.IsNullOrWhiteSpace(version.MainClass)) throw new InvalidOperationException("版本配置缺少主类，无法启动");
        Stage?.Invoke("读取版本", 12, "已读取 " + instance.Name + " 的版本配置");

        var java = await ResolveJavaAsync(version, ct);
        Emit("[Lumen] Java " + java.Major + " · " + java.Path);
        Emit("[Lumen] 版本 " + instance.Name + "（内核 " + version.Kernel + "）");
        Stage?.Invoke("准备 Java", 20, "使用 Java " + java.Major + " 运行时");

        var natives = Path.Combine(root, "versions", instance.Id, "natives");
        Directory.CreateDirectory(natives);
        Stage?.Invoke("校验游戏文件", 30, "正在检查 libraries、客户端与资源文件");
        var installProgress = new Progress<DownloadProgress>(report =>
            // 启动前补全文件也走同一条总进度，不再按 30/44/58 这种分段百分比跳。
            Stage?.Invoke("校验游戏文件", 30 + Math.Clamp(report.Percent, 0, 100) * 0.28, report.Message));
        await EnsureInstallAsync(instance.Id, installProgress, ct);

        var classpath = BuildClasspath(version, root);
        if (classpath.Count == 0) throw new InvalidOperationException("类路径为空，请检查版本文件是否完整");
        Stage?.Invoke("构建参数", 58, "正在生成 JVM 与游戏参数");

        var memory = _settings.Settings.Memory > 0 ? _settings.Settings.Memory : 4096;
        memory = Math.Clamp(memory, 1024, 65536);
        var identity = await IdentityOfAsync(account, ct);
        var args = BuildJvmArguments(version, java, root, natives, classpath, memory, identity);
        var gameArguments = BuildGameArguments(version, gameDir, root, classpath, natives, identity);
        args.AddRange(gameArguments);

        var logDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, "lumen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

        var start = new ProcessStartInfo
        {
            FileName = java.Path,
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) WriteLog(logFile, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) WriteLog(logFile, e.Data); };
        process.Exited += (_, _) =>
        {
            if (!ReferenceEquals(_process, process)) return;
            var code = -1;
            try { code = process.ExitCode; } catch { }
            Emit("[Lumen] 进程结束，退出码 " + code);
            CrashDiagnosis? diagnosis = null;
            if (code != 0)
            {
                try
                {
                    diagnosis = CrashAnalyzer.Analyze(File.ReadAllText(logFile), code);
                    if (diagnosis is not null) Emit("[Lumen] " + diagnosis.Title + "：" + diagnosis.Detail);
                }
                catch { }
            }
            RecordPlaytime(instance.Id, startedAt);
            SetState(code == 0 ? GamePhase.Exited : GamePhase.Crashed, instance.Id, instance.Name,
                State.Pid, _startedAt,
                code == 0 ? "游戏进程已退出" : "游戏崩溃 · " + (diagnosis?.Title ?? "异常退出"),
                code, diagnosis);
            Exited?.Invoke(code);
        };

        _process = process;
        Emit("[Lumen] 启动命令：" + java.Path);
        SetState(GamePhase.Starting, instance.Id, instance.Name, 0, startedAt, "正在启动 Java 进程");
        Stage?.Invoke("启动进程", 76, "正在创建 Java 进程");
        if (!process.Start()) throw new InvalidOperationException("无法创建游戏进程");
        // 进程真的起来了才记录「上次启动」：启动失败（缺 Java、类路径坏）不该算一次游玩。
        _playingInstanceId = instance.Id;
        RecordLastPlayed(instance.Id, startedAt);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        SetState(GamePhase.Running, instance.Id, instance.Name, process.Id, startedAt, "游戏运行中");
        Stage?.Invoke("运行中", 100, "游戏进程已启动 · PID " + process.Id);
        return process.Id;
    }

    /// <summary>写入实例的「上次启动」时间。InstanceMeta 是实例页/首页展示的同一个数据源，
    /// 之前从来没有人写过 LastPlayed，所以两处永远显示"尚未启动"。</summary>
    private void RecordLastPlayed(string instanceId, DateTimeOffset startedAt)
    {
        try
        {
            var meta = GetOrCreateMeta(instanceId);
            meta.LastPlayed = startedAt.ToUnixTimeMilliseconds();
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warn("launch", "记录上次启动时间失败：" + ex.Message);
        }
    }

    /// <summary>进程退出时累加游玩时长（单次封顶 24 小时，避免休眠或系统时钟跳变把时长算爆）。</summary>
    private void RecordPlaytime(string instanceId, DateTimeOffset startedAt)
    {
        try
        {
            var elapsed = (long)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            if (elapsed <= 0) return;
            var meta = GetOrCreateMeta(instanceId);
            meta.PlaytimeMs += Math.Min(elapsed, 24L * 60 * 60 * 1000);
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warn("launch", "记录游玩时长失败：" + ex.Message);
        }
        finally
        {
            if (string.Equals(_playingInstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                _playingInstanceId = null;
        }
    }

    private InstanceMeta GetOrCreateMeta(string instanceId)
    {
        if (!_settings.Settings.InstanceMeta.TryGetValue(instanceId, out var meta))
        {
            meta = new InstanceMeta();
            _settings.Settings.InstanceMeta[instanceId] = meta;
        }
        return meta;
    }

    private void SetState(GamePhase phase, string instanceId, string instanceName, int pid,
        DateTimeOffset startedAt, string message, int code = 0, CrashDiagnosis? reason = null)
    {
        State = new GameState(phase, instanceId, instanceName, pid, startedAt, message, code, reason);
        StateChanged?.Invoke(State);
    }

    private void WriteLog(string file, string line)
    {
        try { File.AppendAllText(file, line + Environment.NewLine); } catch { }
        Emit(line);
    }

    private void Emit(string line) => Output?.Invoke(line);

    /// <summary>Minecraft writes options.txt in the game directory. Merging instead of
    /// overwriting preserves the player's controls, resource packs and video settings,
    /// while forcing the two first-run flags that would otherwise block the main menu.</summary>
    private static void EnsureClientOptions(string gameDir)
    {
        try
        {
            var path = Path.Combine(gameDir, "options.txt");
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            SetOption(lines, "lang", "zh_cn");
            SetOption(lines, "onboardAccessibility", "false");
            SetOption(lines, "skipMultiplayerWarning", "true");
            SetOption(lines, "tutorialStep", "none");
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn("launch", "写入 options.txt 失败：" + ex.Message);
        }
    }

    private static void SetOption(List<string> lines, string key, string value)
    {
        var index = lines.FindIndex(line => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) lines[index] = key + ":" + value;
        else lines.Add(key + ":" + value);
    }

    private async Task<JavaRuntime> ResolveJavaAsync(ResolvedVersion version, CancellationToken ct)
    {
        var required = version.JavaMajor > 0 ? version.JavaMajor : InferJavaMajor(version.Kernel);
        var all = await _java.DetectAsync(false, ct);
        var configured = _settings.Settings.JavaPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var exact = all.FirstOrDefault(j => string.Equals(j.Path, configured, StringComparison.OrdinalIgnoreCase));
            if (exact is not null && IsJavaSuitable(exact.Major, required)) return exact;
        }

        // Old versions (1.12.2, 1.7.10, ...) need Java 8 specifically. Newer Java breaks
        // Forge/legacy mods, so never substitute Java 21/25 for an 8-only version.
        var sameMajor = all.FirstOrDefault(j => j.Major == required);
        if (sameMajor is not null) return sameMajor;

        // Modern versions tolerate a newer LTS (for example Java 25 for a Java 21 game),
        // but only after checking for an exact major first.
        if (required > 16)
        {
            var newer = all.Where(j => j.Major > required).OrderBy(j => j.Major).FirstOrDefault();
            if (newer is not null) return newer;
        }

        if (_javaInstaller is not null)
        {
            Emit("[Lumen] 未找到合适的 Java " + required + "，正在自动下载并注册…");
            var reporter = new Progress<DownloadProgress>(p =>
                Stage?.Invoke("下载 Java " + required, 16,
                    string.IsNullOrWhiteSpace(p.Message) ? "正在下载 Java " + required : p.Message));
            var installed = await _javaInstaller.EnsureAsync(required, reporter, ct);
            if (installed is not null)
            {
                Emit("[Lumen] Java " + installed.Major + " 已下载并加入运行时列表：" + installed.Path);
                return installed;
            }
        }
        throw new InvalidOperationException("该版本需要 Java " + required + "，当前只找到 "
            + string.Join("、", all.Select(j => "Java " + j.Major)) + "。请在设置中安装或指定 Java " + required + "。");
    }

    /// <summary>Java 8 is a hard requirement for legacy Minecraft/Forge; newer runtimes
    /// cannot load many old mods. From 1.17 onward a newer LTS is generally safe.</summary>
    private static bool IsJavaSuitable(int actual, int required)
        => required <= 16 ? actual == required : actual >= required;

    private static int InferJavaMajor(string kernel)
    {
        var match = Regex.Match(kernel ?? "", @"^(?<major>\d+)\.(?<minor>\d+)");
        if (!match.Success) return 8;
        var major = int.Parse(match.Groups["major"].Value);
        if (major >= 26) return 25;
        if (major == 1)
        {
            var minor = int.Parse(match.Groups["minor"].Value);
            if (minor <= 16) return 8;
            if (minor <= 20) return 17;
            return 21;
        }
        return 21;
    }

    private sealed class ResolvedVersion
    {
        public string Id = "";
        public string Kernel = "";
        public string MainClass = "";
        public string Assets = "legacy";
        public AssetIndex? AssetIndex;
        public string? MinecraftArguments;
        public int JavaMajor;
        public string Type = "release";
        public string? JarId;
        public string ClientJar = "";
        public string? ClientUrl;
        public long ClientSize;
        public List<Library> Libraries = [];
        public List<JsonElement> Jvm = [];
        public List<JsonElement> Game = [];
    }

    private async Task<ResolvedVersion?> ResolveVersionAsync(string id, CancellationToken ct)
    {
        var root = _settings.Settings.GameRoot;
        var chain = new List<VersionJson>();
        var current = id;
        for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var path = Path.Combine(root, "versions", current, current + ".json");
            if (!File.Exists(path)) break;
            VersionJson? json;
            try { json = JsonSerializer.Deserialize<VersionJson>(await File.ReadAllTextAsync(path, ct), JsonDefaults.Options); }
            catch { json = null; }
            if (json is null) break;
            chain.Add(json);
            current = json.InheritsFrom;
        }
        if (chain.Count == 0) return null;

        var result = new ResolvedVersion { Id = id };
        var libraries = new Dictionary<string, Library>(StringComparer.OrdinalIgnoreCase);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var v = chain[i];
            if (!string.IsNullOrWhiteSpace(v.MainClass)) result.MainClass = v.MainClass!;
            if (!string.IsNullOrWhiteSpace(v.Assets)) result.Assets = v.Assets!;
            if (v.AssetIndex is not null) result.AssetIndex = v.AssetIndex;
            if (!string.IsNullOrWhiteSpace(v.MinecraftArguments)) result.MinecraftArguments = v.MinecraftArguments;
            if (v.JavaVersion is { MajorVersion: > 0 }) result.JavaMajor = v.JavaVersion.MajorVersion;
            if (!string.IsNullOrWhiteSpace(v.Type)) result.Type = v.Type!;
            if (!string.IsNullOrWhiteSpace(v.Jar)) result.JarId = v.Jar;
            if (v.Downloads is not null && v.Downloads.TryGetValue("client", out var clientDownload) && !string.IsNullOrWhiteSpace(clientDownload.Url))
            {
                result.ClientUrl = clientDownload.Url;
                result.ClientSize = clientDownload.Size;
            }
            if (v.Arguments?.Jvm is { Count: > 0 }) result.Jvm.AddRange(v.Arguments.Jvm);
            if (v.Arguments?.Game is { Count: > 0 }) result.Game.AddRange(v.Arguments.Game);
            foreach (var lib in v.Libraries)
                if (!string.IsNullOrWhiteSpace(lib.Name)) libraries[lib.Name] = lib;
            if (i == chain.Count - 1) result.Kernel = KernelOf(v, id);
        }
        result.Libraries = libraries.Values.ToList();
        result.ClientJar = ResolveClientJar(result, root, id);
        return result;
    }

    private static string KernelOf(VersionJson v, string id)
    {
        foreach (var raw in new[] { v.ClientVersion, v.InheritsFrom, v.Jar, v.Id, id })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var text = Regex.Replace(raw, "^minecraft[-_ ]?", "", RegexOptions.IgnoreCase);
            var m = Regex.Match(text, "^(\\d+\\.\\d+(?:\\.\\d+)?)");
            if (!m.Success) m = Regex.Match(text, "(?:^|[-_])(\\d+\\.\\d+(?:\\.\\d+)?)$");
            if (m.Success) return m.Groups[1].Value;
        }
        return v.Id ?? id;
    }

    private static string ResolveClientJar(ResolvedVersion version, string root, string id)
    {
        var jarId = string.IsNullOrWhiteSpace(version.JarId) ? id : version.JarId!;
        var direct = Path.Combine(root, "versions", jarId, jarId + ".jar");
        if (File.Exists(direct)) return direct;
        foreach (var candidate in Directory.EnumerateDirectories(Path.Combine(root, "versions")))
        {
            var name = Path.GetFileName(candidate);
            if (!name.Contains(version.Kernel, StringComparison.OrdinalIgnoreCase)) continue;
            var jar = Path.Combine(candidate, name + ".jar");
            if (File.Exists(jar)) return jar;
        }
        return direct;
    }

    private sealed record Identity(string Name, string Uuid, string Token, string UserType, string Xuid);

    private async Task<Identity> IdentityOfAsync(AccountRecord account, CancellationToken ct)
    {
        var bound = account.Type == "novalink" && account.Microsoft is not null;
        var name = bound ? (account.Microsoft!.Name ?? account.Name) : account.Name;
        var uuid = bound ? (account.Microsoft!.Uuid ?? account.Uuid) : account.Uuid;
        if (string.IsNullOrWhiteSpace(uuid)) uuid = OfflineUuid.ForName(name ?? "Player");

        var token = "0";
        var userType = "legacy";
        var xuid = account.Xuid ?? "";
        var microsoft = account.Type == "microsoft"
            ? account
            : bound ? _accounts.LocalMicrosoftFor(account) : null;
        if (microsoft is not null)
        {
            var stored = _accounts.MicrosoftTokenFor(microsoft);
            var expired = microsoft.ExpiresAt > 0 &&
                          microsoft.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120_000;
            if ((string.IsNullOrWhiteSpace(stored) || expired) && _microsoft is not null &&
                !string.IsNullOrWhiteSpace(_accounts.MicrosoftRefreshTokenFor(microsoft)))
            {
                Emit("[Lumen] 正在刷新 Microsoft 登录…");
                await _microsoft.RefreshAsync(microsoft, ct);
                stored = _accounts.MicrosoftTokenFor(microsoft);
            }
            if (!string.IsNullOrWhiteSpace(stored))
            {
                token = stored;
                userType = "msa";
                xuid = microsoft.Xuid ?? xuid;
            }
            else if (bound)
            {
                throw new InvalidOperationException("NovaLink 账户已绑定 Microsoft，但本机登录已失效，请在设置中重新绑定");
            }
        }
        // LittleSkin（Yggdrasil 兼容）：令牌直接当 accessToken 交给游戏，userType 用 mojang，
        // 这样客户端会按 Yggdrasil 会话校验皮肤与身份；过期时由 AccountService 自动 refresh 一次。
        if (account.Type == "littleskin")
        {
            var littleSkin = await _accounts.EnsureLittleSkinTokenAsync(account, ct);
            if (!string.IsNullOrWhiteSpace(littleSkin))
            {
                token = littleSkin;
                userType = "mojang";
            }
        }
        return new Identity(name ?? "Player", uuid.Replace("-", ""), token, userType, xuid);
    }

    private sealed record DownloadItem(string Url, string Target, long Size);

    /// <summary>
    /// 一次安装只对外报一条总进度：先把库文件、客户端、资源索引、资源对象全部算进分母，
    /// 再按累计下载字节推进，不再出现「库 55% → 客户端 78% → 资源 100%」那种分段跳变。
    /// </summary>
    public async Task EnsureInstallAsync(string id, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var root = _settings.Settings.GameRoot;
        var version = await ResolveVersionAsync(id, ct) ?? throw new InvalidOperationException("找不到版本配置：" + id);
        var natives = Path.Combine(root, "versions", id, "natives");
        Directory.CreateDirectory(natives);

        var plan = new List<DownloadItem>();
        var nativeJars = new List<string>();
        PlanLibraries(version, root, plan, nativeJars);
        PlanClientJar(version, plan);
        PlanAssetIndex(version, root, plan);
        plan = plan.GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        // 资源对象要等索引下完才知道，先用索引里的 totalSize 估一个分母，避免进度条从 100% 倒退。
        var plannedBytes = plan.Sum(item => Math.Max(0, item.Size));
        var total = plannedBytes + Math.Max(0, version.AssetIndex?.TotalSize ?? 0);
        long done = 0;
        var clock = Stopwatch.StartNew();
        var lastSent = 0L;
        var lastAt = 0L;
        var lastBytes = 0L;
        double speed = 0;

        void Report(string message, bool force = false)
        {
            var elapsed = clock.ElapsedMilliseconds;
            if (!force && elapsed - lastSent < 120) return;
            lastSent = elapsed;
            if (done > lastBytes && elapsed > lastAt)
            {
                var sample = (done - lastBytes) * 1000.0 / (elapsed - lastAt);
                if (sample > 0 && double.IsFinite(sample))
                    speed = speed > 0 ? speed * 0.5 + sample * 0.5 : sample;
                lastAt = elapsed;
                lastBytes = done;
            }
            else if (lastAt == 0) lastAt = elapsed;
            var percent = total > 0 ? Math.Clamp(done * 100.0 / (double)total, 0, 100) : 0;
            progress?.Report(new DownloadProgress("下载中", percent, done, total, speed, message));
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        async Task DownloadAllAsync(List<DownloadItem> items)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(item.Url)) continue;
                var existing = File.Exists(item.Target) ? new FileInfo(item.Target).Length : 0;
                if (existing > 0)
                {
                    done += item.Size > 0 ? item.Size : existing;
                    Report("已存在 " + Path.GetFileName(item.Target));
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                Emit("[Lumen] 下载 " + Path.GetFileName(item.Target));
                try
                {
                    await DownloadFileAsync(http, item, count =>
                    {
                        done += count;
                        Report("下载 " + Path.GetFileName(item.Target));
                    }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 单个库或资源失败不该让整个安装停摆，缺文件会在启动日志里体现。
                    AppLog.Warn("launch", "下载失败 " + Path.GetFileName(item.Target) + "：" + ex.Message);
                }
            }
        }

        Report("正在准备下载文件", true);
        await DownloadAllAsync(plan);

        var objects = PlanAssetObjects(version, root);
        if (objects.Count > 0)
        {
            total = plannedBytes + objects.Sum(item => Math.Max(0, item.Size));
            Report("正在下载资源文件（" + objects.Count + " 个）", true);
            await DownloadAllAsync(objects);
        }

        ExtractNatives(nativeJars, natives);
        done = Math.Max(done, plannedBytes);
        Report("游戏文件已全部就绪", true);
        progress?.Report(new DownloadProgress("完成", 100, Math.Max(done, total), total, 0, "游戏文件已全部就绪"));
    }

    private static async Task DownloadFileAsync(HttpClient http, DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        using var response = await http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var buffer = new byte[81920];
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(item.Target, FileMode.Create, FileAccess.Write, FileShare.None,
            buffer.Length, FileOptions.Asynchronous);
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            onBytes(read);
        }
    }

    private void PlanClientJar(ResolvedVersion version, List<DownloadItem> plan)
    {
        if (File.Exists(version.ClientJar) && new FileInfo(version.ClientJar).Length > 0) return;
        if (string.IsNullOrWhiteSpace(version.ClientUrl))
        {
            Emit("[Lumen] 缺少客户端文件，且版本配置未提供下载地址");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(version.ClientJar)!);
        plan.Add(new DownloadItem(version.ClientUrl!, version.ClientJar, version.ClientSize));
    }

    private void PlanLibraries(ResolvedVersion version, string root, List<DownloadItem> plan, List<string> nativeJars)
    {
        foreach (var library in version.Libraries)
        {
            if (!Allowed(library.Rules)) continue;
            var artifact = library.Downloads;
            var rel = artifact?.Path;
            if (string.IsNullOrWhiteSpace(rel)) rel = MavenPath(library.Name);
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var target = Path.Combine(root, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(artifact?.Url))
                plan.Add(new DownloadItem(artifact!.Url!, target, artifact.Size));
            if (IsNative(library, rel)) nativeJars.Add(target);
        }

        foreach (var library in version.Libraries)
        {
            if (!Allowed(library.Rules) || library.Natives is null) continue;
            foreach (var (os, classifier) in library.Natives)
            {
                if (!os.Equals("windows", StringComparison.OrdinalIgnoreCase)) continue;
                if (library.Classifiers is null || !library.Classifiers.TryGetValue(classifier, out var info)) continue;
                var rel = info.Path ?? MavenPath(library.Name, classifier);
                if (string.IsNullOrWhiteSpace(rel)) continue;
                var target = Path.Combine(root, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(info.Url))
                    plan.Add(new DownloadItem(info.Url!, target, info.Size));
                nativeJars.Add(target);
            }
        }
    }

    private static void PlanAssetIndex(ResolvedVersion version, string root, List<DownloadItem> plan)
    {
        var index = version.AssetIndex;
        if (index is null || string.IsNullOrWhiteSpace(index.Id)) return;
        var file = Path.Combine(root, "assets", "indexes", index.Id + ".json");
        if (File.Exists(file)) return;
        if (string.IsNullOrWhiteSpace(index.Url)) return;
        plan.Add(new DownloadItem(index.Url!, file, index.Size));
    }

    private static List<DownloadItem> PlanAssetObjects(ResolvedVersion version, string root)
    {
        var result = new List<DownloadItem>();
        var index = version.AssetIndex;
        if (index is null || string.IsNullOrWhiteSpace(index.Id)) return result;
        var indexFile = Path.Combine(root, "assets", "indexes", index.Id + ".json");
        if (!File.Exists(indexFile)) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(indexFile));
            if (!document.RootElement.TryGetProperty("objects", out var objects) ||
                objects.ValueKind != JsonValueKind.Object) return result;
            foreach (var entry in objects.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("hash", out var hashElement)) continue;
                var hash = hashElement.GetString();
                if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2) continue;
                var size = entry.Value.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value)
                    ? value
                    : 0;
                var target = Path.Combine(root, "assets", "objects", hash[..2], hash);
                if (File.Exists(target) && new FileInfo(target).Length > 0) continue;
                result.Add(new DownloadItem("https://resources.download.minecraft.net/" + hash[..2] + "/" + hash,
                    target, size));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("launch", "读取资源索引失败：" + ex.Message);
        }
        return result;
    }

    private void ExtractNatives(List<string> nativeJars, string natives)
    {
        foreach (var jar in nativeJars.Distinct())
        {
            if (!File.Exists(jar)) continue;
            try { ZipFile.ExtractToDirectory(jar, natives, true); }
            catch (Exception ex) { AppLog.Warn("launch", "解压原生库失败 " + Path.GetFileName(jar) + "：" + ex.Message); }
        }
        FlattenNatives(natives);
    }

    /// <summary>
    /// LWJGL 3.4 packages natives under windows/x64 (or lwjgl/3.4.x/x64) inside the jar,
    /// but the JVM only searches the root java.library.path. Copy every DLL to the root
    /// so lwjgl.dll, jemalloc.dll and OpenAL.dll resolve at startup.
    /// </summary>
    private static void FlattenNatives(string natives)
    {
        try
        {
            var dlls = Directory.EnumerateFiles(natives, "*.dll", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Contains(Path.Combine("windows", "x64"), StringComparison.OrdinalIgnoreCase))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            // Minecraft 26.3's JVM arguments point at natives/java, natives/jna, natives/lwjgl
            // and natives/netty. Keep the root copies for older versions as well.
            foreach (var source in dlls)
            {
                var name = Path.GetFileName(source);
                try { File.Copy(source, Path.Combine(natives, name), true); }
                catch (Exception ex) { AppLog.Warn("launch", "复制原生库失败 " + name + "：" + ex.Message); }
                foreach (var folder in new[] { "java", "jna", "lwjgl", "netty" })
                {
                    var dir = Path.Combine(natives, folder);
                    Directory.CreateDirectory(dir);
                    try { File.Copy(source, Path.Combine(dir, name), true); }
                    catch (Exception ex) { AppLog.Warn("launch", "复制原生库失败 " + folder + "/" + name + "：" + ex.Message); }
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("launch", "整理原生库失败：" + ex.Message); }
    }

    private static bool IsNative(Library library, string rel)
    {
        if (library.Natives is not null) return true;
        return rel.Contains("natives-windows", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("natives-win", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Allowed(List<Rule>? rules)
    {
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Os is not null)
            {
                if (!string.Equals(rule.Os.Name, "windows", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(rule.Os.Arch) && rule.Os.Arch != "x86" && rule.Os.Arch != "x64") continue;
            }
            if (rule.Features is { Count: > 0 }) continue;
            allowed = rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static string? MavenPath(string name, string? classifier = null)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return null;
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        if (string.IsNullOrWhiteSpace(classifier) && parts.Length >= 4)
            classifier = parts[3];
        var file = artifact + "-" + version + (string.IsNullOrWhiteSpace(classifier) ? "" : "-" + classifier) + ".jar";
        return group + "/" + artifact + "/" + version + "/" + file;
    }

    private List<string> BuildClasspath(ResolvedVersion version, string root)
    {
        var result = new List<string>();
        foreach (var library in version.Libraries)
        {
            if (!Allowed(library.Rules)) continue;
            var rel = library.Downloads?.Path ?? MavenPath(library.Name);
            if (string.IsNullOrWhiteSpace(rel)) continue;
            if (IsNative(library, rel)) continue;
            var path = Path.Combine(root, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) result.Add(path);
        }
        if (File.Exists(version.ClientJar)) result.Add(version.ClientJar);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> BuildJvmArguments(ResolvedVersion version, JavaRuntime java, string root, string natives, List<string> classpath, int memory, Identity identity)
    {
        var args = new List<string>
        {
            "-Xmx" + memory + "M",
            "-Xms" + Math.Min(memory, 1024) + "M",
            "-Dfile.encoding=UTF-8",
            "-Djava.library.path=" + natives,
            "-Djna.tmpdir=" + natives,
            "-Dorg.lwjgl.system.SharedLibraryExtractPath=" + natives,
            "-Dio.netty.native.workdir=" + natives
        };

        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["natives_directory"] = natives,
            ["launcher_name"] = "NovaLinkLumen",
            ["launcher_version"] = LauncherChannelInfo.BaseVersion,
            ["classpath"] = string.Join(';', classpath),
            ["classpath_separator"] = ";",
            ["version_name"] = version.Id,
            ["library_directory"] = Path.Combine(root, "libraries"),
            ["auth_player_name"] = identity.Name
        };

        if (version.Jvm.Count > 0)
        {
            foreach (var element in Expand(version.Jvm, context))
                if (!string.IsNullOrWhiteSpace(element)) args.Add(element);
        }
        else
        {
            args.Add("-cp");
            args.Add(context["classpath"]);
        }
        if (!args.Contains("-cp") && !args.Any(a => a == "-classpath"))
        {
            args.Add("-cp");
            args.Add(context["classpath"]);
        }
        args.Add(version.MainClass);
        return args;
    }

    private List<string> BuildGameArguments(ResolvedVersion version, string gameDir, string root, List<string> classpath, string natives, Identity identity)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = identity.Name,
            ["version_name"] = version.Id,
            ["game_directory"] = gameDir,
            ["assets_root"] = Path.Combine(root, "assets"),
            ["assets_index_name"] = version.AssetIndex?.Id ?? version.Assets,
            ["auth_uuid"] = identity.Uuid,
            ["auth_access_token"] = identity.Token,
            ["clientid"] = "",
            ["auth_xuid"] = identity.Xuid,
            ["user_type"] = identity.UserType,
            ["version_type"] = version.Type,
            ["user_properties"] = "{}",
            ["natives_directory"] = natives,
            ["launcher_name"] = "NovaLinkLumen",
            ["launcher_version"] = LauncherChannelInfo.BaseVersion,
            ["classpath"] = string.Join(';', classpath),
            ["classpath_separator"] = ";",
            ["resolution_width"] = "",
            ["resolution_height"] = "",
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = "",
            ["quickPlayRealms"] = ""
        };

        if (version.Game.Count > 0)
            return Expand(version.Game, context);
        if (!string.IsNullOrWhiteSpace(version.MinecraftArguments))
            return ExpandTokens(version.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), context);
        return [];
    }

    private static List<string> Expand(List<JsonElement> elements, Dictionary<string, string> context)
    {
        var result = new List<string>();
        foreach (var element in elements)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                result.Add(Replace(element.GetString() ?? "", context));
                continue;
            }
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (element.TryGetProperty("rules", out var rules) && !AllowedRules(rules)) continue;
            if (!element.TryGetProperty("value", out var value)) continue;
            if (value.ValueKind == JsonValueKind.String)
                result.Add(Replace(value.GetString() ?? "", context));
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        result.Add(Replace(item.GetString() ?? "", context));
        }
        return result.Where(x => x.Length > 0).ToList();
    }

    private static bool AllowedRules(JsonElement rules)
    {
        if (rules.ValueKind != JsonValueKind.Array) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("os", out var os))
            {
                var name = os.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.Equals(name, "windows", StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (rule.TryGetProperty("features", out _)) continue;
            var action = rule.TryGetProperty("action", out var a) ? a.GetString() : "allow";
            allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static List<string> ExpandTokens(IEnumerable<string> tokens, Dictionary<string, string> context)
        => tokens.Select(t => Replace(t, context)).Where(t => t.Length > 0).ToList();

    private static string Replace(string value, Dictionary<string, string> context)
    {
        return Regex.Replace(value, "\\$\\{([^}]+)\\}", m =>
            context.TryGetValue(m.Groups[1].Value, out var v) ? v : "");
    }
}
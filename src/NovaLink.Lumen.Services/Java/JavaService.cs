using System.Diagnostics;
using System.Text.RegularExpressions;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;

namespace NovaLink.Lumen.Services.Java;

public sealed class JavaService
{
    private readonly AppPaths _paths;
    private readonly Storage.SettingsService _settings;
    private IReadOnlyList<JavaRuntime>? _cache;
    private DateTime _cachedAt;

    public JavaService(AppPaths paths, Storage.SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public async Task<IReadOnlyList<JavaRuntime>> DetectAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(5)) return _cache;
        var found = new Dictionary<string, JavaRuntime>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Candidates())
        {
            ct.ThrowIfCancellationRequested();
            if (found.ContainsKey(candidate)) continue;
            var runtime = await ProbeAsync(candidate, ct);
            if (runtime is not null) found[candidate] = runtime;
        }

        _cache = found.Values.OrderByDescending(j => j.Major).ThenByDescending(j => j.Version).ToList();
        _cachedAt = DateTime.UtcNow;
        return _cache;
    }

    public void Invalidate()
    {
        _cache = null;
        _cachedAt = default;
    }

    /// <summary>Make an auto-downloaded runtime visible immediately, so the same launch
    /// and the next settings page scan never download it twice.</summary>
    public void Remember(JavaRuntime runtime)
    {
        var list = _cache?.ToList() ?? new List<JavaRuntime>();
        list.RemoveAll(j => string.Equals(j.Path, runtime.Path, StringComparison.OrdinalIgnoreCase));
        list.Add(runtime);
        _cache = list.OrderByDescending(j => j.Major).ThenByDescending(j => j.Version).ToList();
        _cachedAt = DateTime.UtcNow;
    }

    public async Task<JavaRuntime?> BestAsync(int required = 21, CancellationToken ct = default)
    {
        var list = await DetectAsync(false, ct);
        return list.Where(j => j.Major >= required).OrderBy(j => j.Major).FirstOrDefault()
               ?? list.FirstOrDefault();
    }

    private IEnumerable<string> Candidates()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { if (File.Exists(p)) set.Add(Path.GetFullPath(p)); } catch { }
        }

        Add(_settings.Settings.JavaPath);
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home)) Add(Path.Combine(home, "bin", "java.exe"));
        Add(FindOnPath("java.exe"));

        foreach (var root in new[]
                 {
                     _paths.JavaDir,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium")
                 })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var exe in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories).Take(24))
                    Add(exe);
            }
            catch { }
        }
        return set;
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), name);
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }
        return null;
    }

    private static async Task<JavaRuntime?> ProbeAsync(string exe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var errTask = process.StandardError.ReadToEndAsync(ct);
            var outTask = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch { } return null; }
            var text = (await errTask) + "\n" + (await outTask);
            var version = Regex.Match(text, "version \"(?<v>[^\"]+)\"").Groups["v"].Value;
            var major = ParseMajor(version);
            if (major <= 0) return null;
            var vendor = text.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ? "Temurin"
                : text.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? "Microsoft"
                : text.Contains("GraalVM", StringComparison.OrdinalIgnoreCase) ? "GraalVM"
                : text.Contains("Zulu", StringComparison.OrdinalIgnoreCase) ? "Zulu"
                : text.Contains("Java(TM)", StringComparison.OrdinalIgnoreCase) ? "Oracle" : "Java";
            return new JavaRuntime(exe, major, version, vendor);
        }
        catch { return null; }
    }

    private static int ParseMajor(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var parts = version.Split('.');
        if (parts.Length > 0 && int.TryParse(parts[0], out var first))
        {
            if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy)) return legacy;
            return first;
        }
        return 0;
    }
}

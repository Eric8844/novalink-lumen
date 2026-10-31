using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;

namespace NovaLink.Lumen.Core;

public sealed class AppPaths
{
    public const string ProductName = "NovaLink Lumen";

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "lumen.json");
    public string Logs => Path.Combine(Root, "logs");
    public string Cache => Path.Combine(Root, "cache");
    public string Skins => Path.Combine(Root, "skins");
    public string Icons => Path.Combine(Root, "icons");
    public string JavaDir => Path.Combine(Root, "java");

    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string DefaultRoot => Path.Combine(LocalAppData, "NovaLink Lumen", "LauncherData");
    public static string LegacyRoot => Path.Combine(LocalAppData, "NovaLink Lumen Preview", "Data");
    public static string ElectronRoot => Path.Combine(LocalAppData, "NovaLink Lumen", "Data");

    public static string DefaultGameRoot
    {
        get
        {
            var mc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            return Directory.Exists(Path.Combine(mc, "versions")) ? mc : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".lumen");
        }
    }

    public AppPaths(string? root = null)
    {
        var custom = root
            ?? Environment.GetEnvironmentVariable("NOVALINK_DATA")
            ?? Environment.GetEnvironmentVariable("NOVALINK_PREVIEW_DATA");
        Root = string.IsNullOrWhiteSpace(custom) ? DefaultRoot : custom;
        if (string.IsNullOrWhiteSpace(custom))
            TryMigrateLegacyData();
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Skins);
        Directory.CreateDirectory(Icons);
        Directory.CreateDirectory(JavaDir);
    }

    public string InstancesMetaFile(string gameRoot)
    {
        var normalized = Path.GetFullPath(gameRoot).ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..12];
        return Path.Combine(Root, "instances-" + hash + ".json");
    }

    /// <summary>
    /// Copies the previous launcher data on first official run. Both the WinUI preview
    /// and the Electron stable build are accepted; the newest config wins. The old
    /// folder is left untouched so a failed upgrade can always fall back.
    /// </summary>
    private void TryMigrateLegacyData()
    {
        try
        {
            if (File.Exists(SettingsFile)) return;
            var source = FindLegacySource();
            if (source is null) return;

            Directory.CreateDirectory(Root);
            CopyDirectory(Path.Combine(source, "skins"), Skins);
            CopyDirectory(Path.Combine(source, "icons"), Icons);
            CopyDirectory(Path.Combine(source, "java"), JavaDir);

            foreach (var meta in Directory.EnumerateFiles(source, "instances-*.json"))
                CopyFile(meta, Path.Combine(Root, Path.GetFileName(meta)));

            // Settings are copied last: their presence marks a completed migration.
            CopyFile(Path.Combine(source, "lumen.json"), SettingsFile);
            RewriteMigratedSettings(source);
            System.Diagnostics.Debug.WriteLine("已从旧版数据目录迁移账户、皮肤、图标与实例配置：" + source);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("旧版数据迁移未完成，将使用新目录：" + ex.Message);
        }
    }

    private static string? FindLegacySource()
    {
        return new[] { LegacyRoot, ElectronRoot }
            .Select(root => Path.Combine(root, "lumen.json"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault();
    }

    private void RewriteMigratedSettings(string source)
    {
        if (!File.Exists(SettingsFile)) return;
        var text = File.ReadAllText(SettingsFile);
        if (JsonNode.Parse(text) is not JsonObject root) return;
        var fromElectron = string.Equals(Path.GetFullPath(source), Path.GetFullPath(ElectronRoot), StringComparison.OrdinalIgnoreCase);

        if (root["accounts"] is JsonArray accounts)
        {
            foreach (var account in accounts.OfType<JsonObject>())
            {
                if (fromElectron)
                {
                    // Electron safeStorage tokens use a different DPAPI envelope. Keep the
                    // account and skin, but require a fresh sign-in instead of failing later.
                    foreach (var key in new[] { "accessToken", "refreshToken", "novalinkToken", "forumToken" })
                    {
                        if (account[key] is JsonValue value && value.TryGetValue<string>(out var token) &&
                            token.StartsWith("enc:", StringComparison.Ordinal))
                            account[key] = null;
                    }
                }

                if (account["skinPath"] is JsonValue skinValue && skinValue.TryGetValue<string>(out var skinPath))
                {
                    var migratedSkin = Path.Combine(Skins, Path.GetFileName(skinPath));
                    if (File.Exists(migratedSkin)) account["skinPath"] = migratedSkin;
                }
            }
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        File.WriteAllText(SettingsFile, root.ToJsonString(options), new System.Text.UTF8Encoding(false));
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            CopyFile(file, target);
        }
    }

    private static void CopyFile(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, false);
    }
}
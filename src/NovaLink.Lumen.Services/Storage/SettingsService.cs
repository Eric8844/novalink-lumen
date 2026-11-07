using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;

namespace NovaLink.Lumen.Services.Storage;

public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _options = JsonDefaults.Options;
    private DateTime _lastSave;

    public AppSettings Settings { get; private set; } = new();

    public string DataRoot => _paths.Root;

    public event EventHandler? Changed;

    public SettingsService(AppPaths paths)
    {
        _paths = paths;
        _paths.EnsureDirectories();
        Reload();
    }

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_paths.SettingsFile))
                {
                    var json = File.ReadAllText(_paths.SettingsFile);
                    Settings = JsonSerializer.Deserialize<AppSettings>(json, _options) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                TryBackupBrokenSettings();
                Settings = new AppSettings();
                AppLog.Warn("settings", "读取配置失败，已使用默认配置：" + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(Settings.GameRoot))
                Settings.GameRoot = AppPaths.DefaultGameRoot;
            Settings.Memory = Math.Max(0, Settings.Memory);
        }
    }

    /// <summary>
    /// 写盘并广播 Changed。音乐播放位置/音量这类「只有外壳自己在乎」的字段用 notify:false 保存，
    /// 否则每次点播放按钮都会把首页、论坛页整体重载一遍。
    /// </summary>
    public void Save(bool notify = true)
    {
        lock (_gate)
        {
            try
            {
                var tmp = _paths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, _options));
                File.Move(tmp, _paths.SettingsFile, true);
                _lastSave = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                AppLog.Error("settings", "保存配置失败", ex);
            }
        }
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveSoon()
    {
        if ((DateTime.UtcNow - _lastSave).TotalMilliseconds < 700) return;
        Save();
    }

    public AccountRecord? SelectedAccount =>
        Settings.Accounts.FirstOrDefault(a => a.Id == Settings.SelectedAccountId)
        ?? Settings.Accounts.FirstOrDefault();

    public AccountRecord? Find(string id) => Settings.Accounts.FirstOrDefault(a => a.Id == id);

    private void TryBackupBrokenSettings()
    {
        try
        {
            if (!File.Exists(_paths.SettingsFile)) return;
            var dest = Path.Combine(_paths.Root, "lumen.broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.Copy(_paths.SettingsFile, dest, true);
        }
        catch { }
    }
}

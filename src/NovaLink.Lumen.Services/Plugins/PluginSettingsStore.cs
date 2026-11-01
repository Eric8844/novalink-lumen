using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Plugins;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 宿主代管的单插件设置：&lt;插件目录&gt;\settings.json，扁平字符串键值（与设计文档 B.2 一致）。
/// 读写全部吞异常（插件设置写不进去不能拖垮宿主），原子写用 tmp + File.Move。
/// </summary>
public sealed class PluginSettingsStore : IPluginSettings
{
    private readonly string _file;
    private readonly Action<string>? _warn;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _dirty;

    /// <summary>创建并立即读取设置文件（不存在则为空）。warn 用于记录读取失败，可为 null。</summary>
    public PluginSettingsStore(string file, Action<string>? warn = null)
    {
        _file = file;
        _warn = warn;
        Load();
    }

    /// <summary>设置文件绝对路径。</summary>
    public string FilePath => _file;

    /// <summary>是否存在未落盘的修改。</summary>
    public bool IsDirty
    {
        get { lock (_gate) return _dirty; }
    }

    /// <summary>读取一个值；不存在返回 null。</summary>
    public string? Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_gate) return _values.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>写入一个值（不立即落盘，等 Flush）。</summary>
    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_gate)
        {
            _values[key] = value ?? "";
            _dirty = true;
        }
    }

    /// <summary>原子落盘；失败只记日志不抛。目录不存在时自动创建。</summary>
    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(_values, JsonDefaults.Options);
        }

        try
        {
            var directory = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);

            lock (_gate) _dirty = false;
        }
        catch (Exception ex)
        {
            _warn?.Invoke("写入插件设置失败：" + _file + " —— " + ex.Message);
        }
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            var json = File.ReadAllText(_file);
            if (string.IsNullOrWhiteSpace(json)) return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Options);
            if (loaded is null) return;

            lock (_gate)
            {
                _values.Clear();
                foreach (var pair in loaded)
                {
                    if (!string.IsNullOrEmpty(pair.Key)) _values[pair.Key] = pair.Value ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            _warn?.Invoke("读取插件设置失败（已忽略，使用空设置）：" + _file + " —— " + ex.Message);
        }
    }
}

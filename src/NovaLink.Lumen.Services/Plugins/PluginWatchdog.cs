using System.Text.Json;
using NovaLink.Lumen.Core;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 启动看门狗：用 plugin-boot.json 记录"启动中/健康"状态，识别崩溃循环并给出安全模式建议。
/// 写入一律原子（tmp + File.Move）；任何 IO 异常都吞掉并记日志 —— 看门狗坏了也不能拦住启动。
/// </summary>
public sealed class PluginWatchdog
{
    /// <summary>连续多少次 starting 未转 healthy 就判定崩溃循环。</summary>
    public const int MaxAttemptsBeforeSafeMode = 2;

    /// <summary>单插件累计失败多少次后隔离。</summary>
    public const int MaxPluginFailures = 3;

    /// <summary>lastError 落盘的最大长度。</summary>
    public const int MaxErrorLength = 400;

    private readonly string _file;
    private readonly Action<string>? _log;
    private readonly Action<string, Exception?>? _logError;
    private readonly object _gate = new();
    private readonly bool _initialRequiresSafeMode;
    private readonly string? _initialSafeModeReason;
    private PluginBootState _state;
    private bool _begun;

    /// <summary>
    /// 打开看门狗并读取上次的启动状态。log 记录一般信息，logError 记录读写失败（都可为 null）。
    /// </summary>
    public PluginWatchdog(string bootFile, Action<string>? log = null, Action<string, Exception?>? logError = null)
    {
        _file = bootFile;
        _log = log;
        _logError = logError;
        _state = Read();

        // 先做崩溃归因：上次进程若在插件加载阶段退出，对应条目仍是 loading。
        // 必须在安全模式判定之前完成，这样隔离状态与 SafeModeReason 都基于最新归因结果。
        RecoverInterruptedLoadsLocked();

        // 安全模式判定必须基于"打开时的持久化状态"。之后本次启动的 BeginLoad 会改写 attempts，
        // 若每次都按实时状态判断，第一次崩溃后的第二次启动就会被误判成崩溃循环。
        _initialRequiresSafeMode = _state.Attempts >= MaxAttemptsBeforeSafeMode
                                   && !string.Equals(_state.State, PluginBootStatus.Healthy, StringComparison.OrdinalIgnoreCase);
        _initialSafeModeReason = _state.SafeModeReason;
    }

    /// <summary>plugin-boot.json 绝对路径。</summary>
    public string FilePath => _file;

    /// <summary>
    /// 本次启动是否应进入安全模式：只看打开时的持久化状态（上次崩在启动中且已经连试 N 次）。
    /// 本次 BeginLoad 带来的 attempts 自增不会让本属性在运行中翻脸。
    /// </summary>
    public bool RequiresSafeMode => _initialRequiresSafeMode;

    /// <summary>安全模式原因（可直接展示给用户；取打开时的值，避免被后续写入覆盖）。</summary>
    public string? SafeModeReason => _initialSafeModeReason;

    /// <summary>上次健康启动时间（心跳）。</summary>
    public DateTimeOffset? LastHealthyUtc
    {
        get { lock (_gate) return _state.LastHealthyUtc; }
    }

    /// <summary>当前持久化的状态快照（只读副本，日志/诊断用）。</summary>
    public PluginBootState Snapshot()
    {
        lock (_gate)
        {
            return new PluginBootState
            {
                Schema = _state.Schema,
                State = _state.State,
                Attempts = _state.Attempts,
                LastStartUtc = _state.LastStartUtc,
                LastHealthyUtc = _state.LastHealthyUtc,
                SafeModeReason = _state.SafeModeReason,
                Plugins = _state.Plugins
                    .Select(p => new PluginBootEntry
                    {
                        Id = p.Id,
                        LoadAttempts = p.LoadAttempts,
                        FailCount = p.FailCount,
                        LastError = p.LastError,
                        Quarantined = p.Quarantined,
                        LastUiTier = p.LastUiTier,
                        Loading = p.Loading,
                        LastPhase = p.LastPhase
                    })
                    .ToList()
            };
        }
    }

    /// <summary>
    /// 加载前调用：状态置 starting、尝试次数 +1，并原子落盘。
    /// 幂等：同一次启动里重复调用不会把 attempts 加上去（调用方多调一次不该让安全模式提前触发）。
    /// </summary>
    public void BeginLoad()
    {
        lock (_gate)
        {
            if (_begun) return;
            _begun = true;

            _state.State = PluginBootStatus.Starting;
            _state.Attempts++;
            _state.LastStartUtc = DateTimeOffset.UtcNow;
            SaveLocked();
        }
    }

    /// <summary>
    /// 启动完全成功后调用：state=healthy、attempts 清零、安全模式原因清空。
    /// 注意：**不清空**各插件的 FailCount / Quarantined / LastError —— 一次健康启动不代表历史失败消失了，
    /// 否则「崩溃一次→下次碰巧成功→失败历史被洗白」会让看门狗永远学不到东西。
    /// 隔离与累计失败只能由 <see cref="ClearQuarantine"/> 或用户手动重置。
    /// </summary>
    public void MarkHealthy()
    {
        lock (_gate)
        {
            _begun = false;
            _state.State = PluginBootStatus.Healthy;
            _state.Attempts = 0;
            _state.LastHealthyUtc = DateTimeOffset.UtcNow;
            _state.SafeModeReason = null;
            SaveLocked();
        }
    }

    /// <summary>明确记录一次启动崩溃（供 App 层在捕获到未处理异常时调用）。</summary>
    public void MarkCrashed(string reason)
    {
        lock (_gate)
        {
            _state.State = PluginBootStatus.Crashed;
            _state.SafeModeReason = Clip(reason);
            SaveLocked();
        }
    }

    /// <summary>
    /// 开始加载某个插件之前调用：把该条目标记为 loading 并记录阶段。
    /// 如果下次启动仍读到 loading，说明本次进程在加载该插件时崩溃，会归因到它（failCount+1）。
    /// 成功走 <see cref="MarkPluginLoaded"/>、失败走 <see cref="MarkPluginFailure"/> 都会清掉该标记。
    /// </summary>
    public void MarkPluginLoading(string id, string? phase = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            var entry = GetOrAddEntryLocked(id);
            entry.Loading = true;
            entry.LastPhase = phase ?? PluginBootPhases.Loading;
            SaveLocked();
        }
    }

    /// <summary>更新加载阶段（只写 lastPhase，不改计数）；用于把崩溃点定位到 Initialize 等步骤。</summary>
    public void MarkPluginPhase(string id, string phase)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(phase)) return;
        lock (_gate)
        {
            var entry = GetOrAddEntryLocked(id);
            entry.LastPhase = phase;
            SaveLocked();
        }
    }

    /// <summary>记录单插件失败：failCount +1，达到阈值打隔离标记。</summary>
    public void MarkPluginFailure(string id, string? error)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            var entry = GetOrAddEntryLocked(id);
            entry.LoadAttempts++;
            entry.FailCount++;
            entry.LastError = Clip(error);
            // 失败也是一次"结束加载"：清掉 loading，避免下次启动把它误判成崩溃。
            entry.Loading = false;
            if (entry.FailCount >= MaxPluginFailures) entry.Quarantined = true;
            SaveLocked();
        }
    }

    /// <summary>记录单插件成功加载（loadAttempts +1、清掉上次错误；不重置 failCount 累计）。</summary>
    public void MarkPluginLoaded(string id, string? uiTier = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            var entry = GetOrAddEntryLocked(id);
            entry.LoadAttempts++;
            entry.LastError = null;
            entry.Loading = false;
            entry.LastPhase = PluginBootPhases.Loaded;
            if (uiTier is not null) entry.LastUiTier = uiTier;
            SaveLocked();
        }
    }

    /// <summary>该插件是否已被隔离（上次运行记录）。</summary>
    public bool IsQuarantined(string id)
    {
        lock (_gate)
        {
            var entry = _state.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            return entry is not null && entry.Quarantined;
        }
    }

    /// <summary>该插件累计失败次数（上次运行记录）。</summary>
    public int GetFailCount(string id)
    {
        lock (_gate)
        {
            var entry = _state.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            return entry?.FailCount ?? 0;
        }
    }

    /// <summary>取消隔离（用户在设置里"重新启用"插件时调用）。</summary>
    public void ClearQuarantine(string id)
    {
        lock (_gate)
        {
            var entry = _state.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;
            entry.Quarantined = false;
            entry.FailCount = 0;
            entry.LastError = null;
            SaveLocked();
        }
    }

    /// <summary>
    /// 一次清除全部插件的历史失败与隔离记录（设置页"重新启用插件"用）。
    /// 只动持久化状态，不影响本次运行已加载的插件。返回被清理的条目数。
    /// </summary>
    public int ClearAllQuarantines()
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var entry in _state.Plugins)
            {
                if (entry.Quarantined || entry.FailCount != 0 || entry.LastError is not null) count++;
                entry.Quarantined = false;
                entry.FailCount = 0;
                entry.LastError = null;
            }

            if (count > 0) SaveLocked();
            return count;
        }
    }

    /// <summary>实时安全模式原因（含启动时的崩溃归因），供设置页展示；未进入为 null。</summary>
    public string? GetBootReason()
    {
        lock (_gate) return _state.SafeModeReason;
    }

    /// <summary>
    /// 崩溃归因：上次进程退出时仍处于 loading 的插件，判定为"加载该插件时崩溃"。
    /// 对该插件 failCount+1 并写 LastError，达到阈值直接隔离；同时把插件 id 写进 SafeModeReason。
    /// </summary>
    private void RecoverInterruptedLoadsLocked()
    {
        var crashedIds = _state.Plugins
            .Where(p => p.Loading)
            .Select(p => p.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        if (crashedIds.Count == 0) return;

        var quarantinedNow = new List<string>();
        foreach (var entry in _state.Plugins)
        {
            if (!entry.Loading) continue;

            entry.Loading = false;
            entry.LastPhase = PluginBootPhases.Crashed;
            entry.FailCount++;
            entry.LastError = "上次启动时插件崩溃";
            if (entry.FailCount >= MaxPluginFailures)
            {
                entry.Quarantined = true;
                quarantinedNow.Add(entry.Id);
            }

            _log?.Invoke("插件看门狗：上次启动在加载插件 " + entry.Id + " 时异常退出（failCount=" +
                         entry.FailCount + (entry.Quarantined ? "，已隔离）。" : "）。"));
        }

        _state.SafeModeReason = Clip(
            "上次启动在加载插件 " + string.Join("、", crashedIds) + " 时异常退出（疑似崩溃）" +
            (quarantinedNow.Count > 0 ? "；已隔离：" + string.Join("、", quarantinedNow) : "") + "。");
        SaveLocked();
    }

    private PluginBootEntry GetOrAddEntryLocked(string id)
    {
        var entry = _state.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) return entry;

        entry = new PluginBootEntry { Id = id };
        _state.Plugins.Add(entry);
        return entry;
    }

    /// <summary>读取状态文件；缺失/损坏一律返回"健康 + 0 次"的新状态并记日志。</summary>
    private PluginBootState Read()
    {
        try
        {
            if (!File.Exists(_file))
            {
                _log?.Invoke("插件看门狗：未找到 " + _file + "，按首次启动处理。");
                return new PluginBootState();
            }

            var json = File.ReadAllText(_file);
            var state = JsonSerializer.Deserialize<PluginBootState>(json, JsonDefaults.Options);
            if (state is null)
            {
                _log?.Invoke("插件看门狗：状态文件内容为空，按首次启动处理。");
                return new PluginBootState();
            }

            state.Plugins ??= [];
            if (string.IsNullOrWhiteSpace(state.State)) state.State = PluginBootStatus.Healthy;
            return state;
        }
        catch (Exception ex)
        {
            _logError?.Invoke("插件看门狗：读取 " + _file + " 失败，按首次启动处理。", ex);
            return new PluginBootState();
        }
    }

    /// <summary>原子写（tmp + File.Move）；失败只记日志。</summary>
    private void SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_state, JsonDefaults.Options);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            _logError?.Invoke("插件看门狗：写入 " + _file + " 失败。", ex);
        }
    }

    private static string? Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength] + "…";
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Services;

namespace NovaLink.Lumen.Controls;

public enum ProgressStatus { Active, Stopping, Done, Failed, Cancelled }

public sealed class ProgressTask
{
    public string Id { get; init; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Kind { get; set; } = "download";
    public string Phase { get; set; } = "准备中";
    public double? Percent { get; set; }
    public bool Indeterminate { get; set; } = true;
    public string Message { get; set; } = "";
    public ProgressStatus Status { get; set; } = ProgressStatus.Active;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public long Bytes { get; set; }
    public long Total { get; set; }
    public double Speed { get; set; }
    public bool Cancelable { get; set; } = true;
    public Action? OnCancel { get; set; }
    public List<string> Logs { get; init; } = new();

    public string StatusText => Status switch
    {
        ProgressStatus.Done => "已完成",
        ProgressStatus.Failed => "失败",
        ProgressStatus.Cancelled => "已停止",
        ProgressStatus.Stopping => "正在停止",
        _ => string.IsNullOrWhiteSpace(Phase) ? "进行中" : Phase
    };

    public int PercentValue => (int)Math.Clamp(Math.Round(Percent ?? 0), 0, 100);
    public string SpeedText => Speed > 0 ? FormatBytes((long)Speed) + "/s" : "";
    public string ElapsedText
    {
        get
        {
            var ms = Math.Max(0, ((FinishedAt ?? DateTimeOffset.Now) - StartedAt).TotalMilliseconds);
            var seconds = (int)Math.Round(ms / 1000);
            return seconds < 60 ? seconds + "s" : seconds / 60 + "m " + seconds % 60 + "s";
        }
    }

    public static string FormatBytes(long value)
    {
        if (value <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return (size >= 100 || unit == 0 ? size.ToString("0") : size.ToString("0.0")) + " " + units[unit];
    }
}

/// <summary>一条已经结束、并且日志已经写进历史文件的任务记录。</summary>
public sealed class ProgressHistoryEntry
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public string LogFile { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public long Bytes { get; init; }
    public long Total { get; init; }

    public string TimeText => StartedAt.LocalDateTime.ToString("MM-dd HH:mm");
    public string LineText => TimeText + " · " + Title + (string.IsNullOrWhiteSpace(Status) ? "" : " · " + Status);
}

/// <summary>所有进行中任务合计出来的总进度（下载与启动共用一条）。</summary>
public sealed record ProgressAggregate(int Count, double Percent, long Bytes, long Total, double Speed, bool Indeterminate);

/// <summary>Mirrors the Electron activity stream: tasks, dock and the 进度显示 overlay.</summary>
public static class ProgressCenter
{
    private static readonly object Gate = new();
    private static readonly List<ProgressTask> Tasks = new();
    private static readonly List<ProgressHistoryEntry> History = new();
    private static string? _selectedId;
    private static DateTime _lastBytesAt;
    private static long _lastBytes;
    private static bool _historyLoaded;

    public static event Action? Changed;

    private static string HistoryDirectory => Path.Combine(
        AppLog.LogDirectory ?? Path.Combine(AppPaths.DefaultRoot, "logs"), "progress");
    private static string HistoryIndex => Path.Combine(HistoryDirectory, "history.jsonl");

    /// <summary>所有进行中任务合计出来的总进度；dock、进度显示和标题栏都读它。</summary>
    public static ProgressAggregate Aggregate()
    {
        lock (Gate)
        {
            var active = Tasks.Where(t => t.Status is ProgressStatus.Active or ProgressStatus.Stopping).ToList();
            if (active.Count == 0) return new ProgressAggregate(0, 0, 0, 0, 0, true);
            var bytes = active.Sum(t => t.Bytes);
            var total = active.Sum(t => t.Total);
            var speed = active.Sum(t => t.Speed);
            // 启动任务是按阶段报百分比的，没有字节分母；只有连百分比都没有时才算「不确定进度」。
            var indeterminate = total <= 0 && active.All(t => t.Indeterminate);
            var percent = total > 0
                ? Math.Clamp(bytes * 100.0 / total, 0, 100)
                : Math.Clamp(active.Average(t => t.Percent ?? 0), 0, 100);
            return new ProgressAggregate(active.Count, percent, bytes, total, speed, indeterminate);
        }
    }

    /// <summary>已结束任务的日志索引（新在前），来自 logs/progress/history.jsonl。</summary>
    public static IReadOnlyList<ProgressHistoryEntry> HistorySnapshot()
    {
        EnsureHistoryLoaded();
        lock (Gate) return History.ToList();
    }

    private static void EnsureHistoryLoaded()
    {
        lock (Gate)
        {
            if (_historyLoaded) return;
            _historyLoaded = true;
            try
            {
                if (!File.Exists(HistoryIndex)) return;
                var lines = File.ReadAllLines(HistoryIndex);
                foreach (var line in lines.Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<ProgressHistoryEntry>(line);
                        if (entry is null || string.IsNullOrWhiteSpace(entry.Id)) continue;
                        History.Add(entry);
                        if (History.Count >= 100) break;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { AppLog.Warn("progress", "读取历史日志索引失败：" + ex.Message); }
        }
    }

    /// <summary>
    /// 把结束的任务写成 logs/progress/&lt;时间&gt;-&lt;id&gt;.log，并在 history.jsonl 里留一行索引。
    /// 运行实例关闭、下载结束都会走这里，所以任务记录可以从列表里删掉，日志不会丢。
    /// </summary>
    private static void Archive(ProgressTask task)
    {
        try
        {
            Directory.CreateDirectory(HistoryDirectory);
            var stamp = task.StartedAt.ToString("yyyyMMdd-HHmmss");
            var safeId = new string(task.Id.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
            if (safeId.Length > 60) safeId = safeId[..60];
            var file = Path.Combine(HistoryDirectory, stamp + "-" + safeId + ".log");

            var builder = new StringBuilder();
            builder.AppendLine("# NovaLink Lumen 进度日志");
            builder.AppendLine("任务：" + task.Title + "（" + task.Id + "）");
            builder.AppendLine("类型：" + task.Kind + " · 状态：" + task.StatusText);
            builder.AppendLine("开始：" + task.StartedAt.ToString("yyyy-MM-dd HH:mm:ss") +
                               " · 结束：" + (task.FinishedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss"));
            var size = ProgressTask.FormatBytes(task.Bytes);
            if (task.Total > 0) size += " / " + ProgressTask.FormatBytes(task.Total);
            builder.AppendLine("耗时：" + task.ElapsedText + " · 已下载：" + size);
            builder.AppendLine("结果：" + task.Message);
            builder.AppendLine(new string('-', 64));
            foreach (var line in task.Logs) builder.AppendLine(line);
            File.WriteAllText(file, builder.ToString(), new UTF8Encoding(false));

            var entry = new ProgressHistoryEntry
            {
                Id = task.Id,
                Title = task.Title,
                Kind = task.Kind,
                Status = task.StatusText,
                Message = task.Message,
                LogFile = file,
                StartedAt = task.StartedAt,
                FinishedAt = task.FinishedAt ?? DateTimeOffset.Now,
                Bytes = task.Bytes,
                Total = task.Total
            };
            lock (Gate)
            {
                History.Insert(0, entry);
                if (History.Count > 100) History.RemoveRange(100, History.Count - 100);
            }
            File.AppendAllText(HistoryIndex, JsonSerializer.Serialize(entry) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn("progress", "写入进度历史日志失败：" + ex.Message);
        }
    }

    public static IReadOnlyList<ProgressTask> Snapshot()
    {
        lock (Gate)
        {
            return Tasks.OrderBy(t => t.Status is ProgressStatus.Active or ProgressStatus.Stopping ? 0 : 1)
                .ThenByDescending(t => t.StartedAt)
                .Select(Clone)
                .ToList();
        }
    }

    public static ProgressTask? Selected
    {
        get
        {
            lock (Gate)
            {
                var task = Tasks.FirstOrDefault(t => t.Id == _selectedId) ?? Tasks.FirstOrDefault();
                return task is null ? null : Clone(task);
            }
        }
    }

    // The dispatcher renders from a copy so worker-thread updates can never mutate
    // the list while the progress dialog is enumerating it.
    private static ProgressTask Clone(ProgressTask task) => new()
    {
        Id = task.Id,
        Title = task.Title,
        Subtitle = task.Subtitle,
        Kind = task.Kind,
        Phase = task.Phase,
        Percent = task.Percent,
        Indeterminate = task.Indeterminate,
        Message = task.Message,
        Status = task.Status,
        StartedAt = task.StartedAt,
        FinishedAt = task.FinishedAt,
        Bytes = task.Bytes,
        Total = task.Total,
        Speed = task.Speed,
        Cancelable = task.Cancelable,
        OnCancel = task.OnCancel,
        Logs = task.Logs.ToList()
    };

    public static int ActiveCount
    {
        get { lock (Gate) return Tasks.Count(t => t.Status is ProgressStatus.Active or ProgressStatus.Stopping); }
    }

    public static void Select(string id)
    {
        lock (Gate) _selectedId = id;
        Changed?.Invoke();
    }

    public static ProgressTask Start(string id, string title, string subtitle, string kind, string phase,
        string message, bool cancelable = true, Action? onCancel = null)
    {
        var task = new ProgressTask
        {
            Id = id, Title = title, Subtitle = subtitle, Kind = kind, Phase = phase,
            Message = message, Cancelable = cancelable, OnCancel = onCancel
        };
        lock (Gate)
        {
            Tasks.RemoveAll(t => t.Id == id);
            Tasks.Add(task);
            _selectedId = id;
        }
        Changed?.Invoke();
        return task;
    }

    public static void Update(string id, string? phase = null, double? percent = null, string? message = null,
        long? bytes = null, bool? indeterminate = null, string? title = null)
    {
        lock (Gate)
        {
            var task = Tasks.FirstOrDefault(t => t.Id == id);
            if (task is null) return;
            if (!string.IsNullOrWhiteSpace(title)) task.Title = title;
            if (!string.IsNullOrWhiteSpace(phase)) task.Phase = phase;
            if (percent is not null) { task.Percent = percent; task.Indeterminate = false; }
            if (indeterminate is not null) task.Indeterminate = indeterminate.Value;
            if (message is not null) task.Message = message;
            if (bytes is not null)
            {
                var now = DateTime.UtcNow;
                if (bytes.Value > task.Bytes && _lastBytes > 0 && (now - _lastBytesAt).TotalMilliseconds >= 250)
                {
                    var sample = (bytes.Value - task.Bytes) * 1000.0 / (now - _lastBytesAt).TotalMilliseconds;
                    if (sample > 0 && double.IsFinite(sample))
                        task.Speed = task.Speed > 0 ? task.Speed * 0.55 + sample * 0.45 : sample;
                    _lastBytesAt = now;
                }
                if (_lastBytes == 0) { _lastBytesAt = now; }
                task.Bytes = bytes.Value;
                _lastBytes = bytes.Value;
            }
        }
        Changed?.Invoke();
    }

    public static void Log(string id, string line)
    {
        var clean = (line ?? "").Replace("\r", "").Trim();
        if (clean.Length == 0) return;
        lock (Gate)
        {
            var task = Tasks.FirstOrDefault(t => t.Id == id);
            if (task is null) return;
            if (task.Logs.Count > 0 && task.Logs[^1] == clean) return;
            task.Logs.Add(clean);
            if (task.Logs.Count > 200) task.Logs.RemoveRange(0, task.Logs.Count - 200);
        }
        Changed?.Invoke();
    }

    /// <param name="remove">
    /// true = 结束的同时把任务从列表里删掉（运行中的实例被关闭时用），日志先归档到历史日志；
    /// false = 保留「已完成」记录，同样会写一份历史日志。
    /// </param>
    public static void Finish(string id, string message, bool failed = false, bool cancelled = false, bool remove = false)
    {
        ProgressTask? finished = null;
        lock (Gate)
        {
            var task = Tasks.FirstOrDefault(t => t.Id == id);
            if (task is null) return;
            task.Status = failed ? (cancelled ? ProgressStatus.Cancelled : ProgressStatus.Failed) : ProgressStatus.Done;
            task.Phase = failed ? (cancelled ? "已停止" : "失败") : "完成";
            task.Message = message;
            task.FinishedAt = DateTimeOffset.Now;
            task.Cancelable = false;
            if (!failed) { task.Percent = 100; task.Indeterminate = false; }
            if (failed) task.Logs.Add(message);
            finished = task;
            if (remove)
            {
                Tasks.RemoveAll(t => t.Id == id);
                if (_selectedId == id) _selectedId = Tasks.FirstOrDefault()?.Id;
            }
        }
        if (finished is not null) Archive(finished);
        Changed?.Invoke();
    }

    /// <summary>Replaces the cancel action without restarting the task; used when a launch becomes a live game.</summary>
    public static void BindStop(string id, Action? onCancel)
    {
        lock (Gate)
        {
            var task = Tasks.FirstOrDefault(t => t.Id == id);
            if (task is null) return;
            task.OnCancel = onCancel;
            task.Cancelable = onCancel is not null;
            task.Status = ProgressStatus.Active;
            task.FinishedAt = null;
        }
        Changed?.Invoke();
    }

    public static void StopSelected()
    {
        ProgressTask? task;
        lock (Gate)
        {
            task = Tasks.FirstOrDefault(t => t.Id == _selectedId) ?? Tasks.FirstOrDefault();
            if (task is null || task.Status is not (ProgressStatus.Active or ProgressStatus.Stopping) || !task.Cancelable) return;
            task.Status = ProgressStatus.Stopping;
            task.Phase = "正在停止";
            task.Message = "正在取消当前任务…";
            task.Logs.Add("请求停止任务");
        }
        try { task.OnCancel?.Invoke(); }
        catch (Exception ex) { AppLog.Warn("progress", "取消失败：" + ex.Message); }
        Changed?.Invoke();
    }

    public static void OpenOverlay() => Views.Dialogs.ProgressDialog.Show();

    public static void CloseOverlay() => Views.Dialogs.ProgressDialog.Close();
}

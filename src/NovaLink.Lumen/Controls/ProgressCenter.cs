using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>Mirrors the Electron activity stream: tasks, dock and the 进度显示 overlay.</summary>
public static class ProgressCenter
{
    private static readonly object Gate = new();
    private static readonly List<ProgressTask> Tasks = new();
    private static string? _selectedId;
    private static DateTime _lastBytesAt;
    private static long _lastBytes;

    public static event Action? Changed;

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

    public static void Finish(string id, string message, bool failed = false, bool cancelled = false)
    {
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
        }
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

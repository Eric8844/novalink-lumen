using System;
using System.IO;
using System.Threading;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 监听 <c>&lt;DataRoot&gt;\plugins</c>（含子目录）的增删改，防抖后只抛一次 <see cref="Changed"/>。
///
/// 为什么要防抖：解压/复制一个插件包会连发几十个 Created/Changed 事件，
/// 每个都去重扫插件目录会把界面拖住，所以统一等 400ms 再刷一次。
/// 只关心插件内容相关的扩展名与目录本身，插件自己写日志/缓存不会触发刷新。
/// 任何异常都只进日志：监听失败最多让插件列表退回手动刷新，绝不影响启动器。
/// </summary>
public sealed class PluginFolderWatcher : IDisposable
{
    private const string LogArea = "plugins";

    /// <summary>防抖窗口。一次解压完再刷新，用户看不到中间态。</summary>
    private const int DebounceMilliseconds = 400;

    /// <summary>与插件内容相关的扩展名（大小写不敏感）。</summary>
    private static readonly string[] WatchedExtensions =
        [".dll", ".exe", ".pri", ".json", ".lumenplugin", ".pdb", ".js"];

    private readonly object _gate = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private bool _disposed;

    public PluginFolderWatcher(string pluginsRoot)
    {
        if (string.IsNullOrWhiteSpace(pluginsRoot))
            throw new ArgumentException("插件目录不能为空。", nameof(pluginsRoot));

        Root = Path.GetFullPath(pluginsRoot);
        Directory.CreateDirectory(Root);

        _timer = new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += (_, e) => Schedule(e.FullPath, e.OldFullPath);
        _watcher.Error += (_, e) =>
            AppLog.Warn(LogArea, "监听插件目录出错（插件列表改为手动刷新）：" + e.GetException().Message);

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>被监听的插件根目录（绝对路径）。</summary>
    public string Root { get; }

    /// <summary>插件目录内容发生变化（防抖后只触发一次）。可能在任意线程触发，调用方自行切回 UI 线程。</summary>
    public event EventHandler? Changed;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _watcher.EnableRaisingEvents = false; } catch { }
        try { _watcher.Dispose(); } catch { }
        try { _timer.Dispose(); } catch { }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => Schedule(e.FullPath, null);

    /// <summary>
    /// 目录本身的 Changed 是"子文件被写"的副作用（目录的修改时间会跟着变），
    /// 已经由子文件自己的事件覆盖；直接跳过，免得插件往自己目录里写缓存就把列表刷一遍。
    /// </summary>
    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (Directory.Exists(e.FullPath)) return;
        }
        catch
        {
            return;
        }

        Schedule(e.FullPath, null);
    }

    /// <summary>记下"有变化"，并把防抖计时器重新计时。</summary>
    private void Schedule(string? path, string? oldPath)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
            }

            if (!IsRelevant(path) && !IsRelevant(oldPath)) return;
            _timer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 退出时计时器先被释放，忽略即可。
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "安排插件目录刷新失败（已忽略）：" + ex.Message);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "插件目录变化回调失败（已忽略）：" + ex.Message);
        }
    }

    /// <summary>目录变化（没有扩展名）与常见插件文件才算相关；插件自己的日志、缓存不触发刷新。</summary>
    private bool IsRelevant(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return true;

            foreach (var watched in WatchedExtensions)
            {
                if (string.Equals(watched, extension, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

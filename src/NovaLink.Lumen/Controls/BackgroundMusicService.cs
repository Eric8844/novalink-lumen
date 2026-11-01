using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using NovaLink.Lumen.Services;
using WinRT;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Render;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// Small playlist player for the launcher. Playback uses MediaPlayer; a second
/// silent AudioGraph reads the decoded samples of the same file and publishes a
/// real 28-band spectrum for the block backdrop.
/// </summary>
public sealed class BackgroundMusicService : IDisposable
{
    private const int BandCount = 28;
    private const int FftSize = 1024;
    private const int TargetSampleRate = 48000;

    private readonly MediaPlayer _player = new();
    private readonly DispatcherQueue? _dispatcher;
    private readonly List<string> _tracks = [];
    private readonly Random _random = new();
    private readonly object _spectrumGate = new();
    private readonly float[] _bands = new float[BandCount];
    private readonly float[] _bandTargets = new float[BandCount];
    private readonly int[] _bandLow = new int[BandCount];
    private readonly int[] _bandHigh = new int[BandCount];
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _fftRe = new float[FftSize];
    private readonly float[] _fftIm = new float[FftSize];
    private float[] _frameBuffer = Array.Empty<float>();
    private int _ringWrite;
    private int _ringCount;
    private int _sampleRate = TargetSampleRate;
    private int _channels = 2;
    private int _index = -1;
    private string _mode = "list";
    // 自己记住音量：MediaPlayer.Volume 在音源还没挂上时读回来可能是 0，
    // 直接用 getter 回显会把界面滑块打成 0%，还会把 0 写回设置。
    private double _volume = 1;

    private AudioGraph? _graph;
    private AudioDeviceOutputNode? _deviceOutput;
    private AudioFrameOutputNode? _frameOutput;
    private MediaSourceAudioInputNode? _audioInput;
    private int _analyzerGeneration;
    private long _lastAnalyzerError;

    public event EventHandler? Changed;

    public BackgroundMusicService(DispatcherQueue? dispatcher)
    {
        _dispatcher = dispatcher;
        _player.AutoPlay = false;
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += (_, _) => OnEnded();
        _player.MediaFailed += (_, _) => OnFailed();
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => Notify();

        for (var i = 0; i < FftSize; i++)
            _window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FftSize - 1));
        BuildBands(TargetSampleRate);
    }

    public IReadOnlyList<string> Tracks => _tracks;
    public string Mode => _mode;
    public bool HasTracks => _tracks.Count > 0;
    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public bool IsAnalyzing => _graph is not null;
    public string? CurrentPath => _index >= 0 && _index < _tracks.Count ? _tracks[_index] : null;
    public string CurrentTitle => CurrentPath is null ? "未选择音乐" : Path.GetFileNameWithoutExtension(CurrentPath);
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            try { _player.Volume = _volume; } catch { }
        }
    }

    /// <summary>当前播放位置；没有加载曲目时为 0。</summary>
    public TimeSpan Position
    {
        get { try { return _player.PlaybackSession.Position; } catch { return TimeSpan.Zero; } }
    }

    /// <summary>当前曲目总时长；媒体还没解析出时长时为 0。</summary>
    public TimeSpan Duration
    {
        get { try { return _player.PlaybackSession.NaturalDuration; } catch { return TimeSpan.Zero; } }
    }

    /// <summary>跳转到指定播放位置；媒体还没解析出时长或位置不合法时安全忽略。</summary>
    public void Seek(TimeSpan position)
    {
        try
        {
            var session = _player.PlaybackSession;
            if (session is null) return;
            var duration = session.NaturalDuration;
            var target = position;
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (duration > TimeSpan.Zero && target > duration) target = duration;
            session.Position = target;
            Notify();
        }
        catch (Exception)
        {
            // 曲目还没准备好时 Seek 会抛，直接忽略即可。
        }
    }

    /// <summary>当前曲目在播放列表里的序号（从 0 开始）；没有选中曲目时为 -1。</summary>
    public int TrackIndex => _index;

    /// <summary>播放列表里的曲目总数。</summary>
    public int TrackCount => _tracks.Count;

    public void Configure(IEnumerable<string> tracks, string mode, double volume)
    {
        _tracks.Clear();
        foreach (var path in tracks.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            _tracks.Add(path);
        _mode = NormalizeMode(mode);
        Volume = volume;
        if (_index >= _tracks.Count) _index = _tracks.Count - 1;
        if (_tracks.Count == 0) Stop();
        Notify();
    }

    public void PlayIndex(int index, bool autoplay = true)
    {
        if (_tracks.Count == 0) return;
        _index = Math.Clamp(index, 0, _tracks.Count - 1);
        PlayCurrent(autoplay);
    }

    public void Stop()
    {
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch { }
        StopAnalyzer();
        _index = -1;
        Notify();
    }

    public void Toggle()
    {
        if (_tracks.Count == 0) return;
        if (_index < 0) { PlayIndex(0); return; }
        if (IsPlaying)
        {
            _player.Pause();
            PauseAnalyzer();
        }
        else
        {
            _player.Play();
            ResumeAnalyzer();
        }
        Notify();
    }

    public void Next(bool fromUser = true)
    {
        if (_tracks.Count == 0) return;
        if (_mode == "shuffle" && _tracks.Count > 1)
        {
            var next = _random.Next(_tracks.Count - 1);
            if (next >= _index) next++;
            _index = next;
        }
        else
        {
            _index = (_index + 1) % _tracks.Count;
        }
        PlayCurrent(true);
    }

    public void Previous()
    {
        if (_tracks.Count == 0) return;
        if (_mode == "shuffle" && _tracks.Count > 1)
        {
            var prev = _random.Next(_tracks.Count - 1);
            if (prev >= _index) prev++;
            _index = prev;
        }
        else
        {
            _index = (_index - 1 + _tracks.Count) % _tracks.Count;
        }
        PlayCurrent(true);
    }

    public string CycleMode()
    {
        _mode = _mode switch
        {
            "one" => "list",
            "list" => "shuffle",
            _ => "one"
        };
        Notify();
        return _mode;
    }

    public void SetMode(string mode)
    {
        _mode = NormalizeMode(mode);
        Notify();
    }

    /// <summary>真实音频频谱，0..1，低音在前、高音在后；未播放时缓慢归零。</summary>
    public double[] GetSpectrum()
    {
        var copy = new double[BandCount];
        lock (_spectrumGate)
        {
            var playing = IsPlaying;
            for (var i = 0; i < BandCount; i++)
            {
                if (!playing) _bands[i] *= 0.86f;
                copy[i] = _bands[i];
            }
        }
        return copy;
    }

    public double GetPulse()
    {
        var spectrum = GetSpectrum();
        var sum = 0.0;
        for (var i = 0; i < spectrum.Length; i++) sum += spectrum[i];
        return Math.Clamp(sum / spectrum.Length, 0, 1);
    }

    private void PlayCurrent(bool autoplay)
    {
        if (CurrentPath is null) return;
        try
        {
            _player.Source = MediaSource.CreateFromUri(new Uri(CurrentPath));
            if (autoplay) _player.Play();
            _ = StartAnalyzerAsync(CurrentPath);
            Notify();
        }
        catch
        {
            Next();
        }
    }

    private async Task StartAnalyzerAsync(string path)
    {
        var generation = Interlocked.Increment(ref _analyzerGeneration);
        DisposeAnalyzer();

        AudioGraph? graph = null;
        AudioDeviceOutputNode? device = null;
        AudioFrameOutputNode? frame = null;
        MediaSourceAudioInputNode? input = null;
        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                DesiredSamplesPerQuantum = 1024
            };
            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success || graphResult.Graph is null)
            {
                LogAnalyzerError("创建 AudioGraph 失败：" + graphResult.Status);
                return;
            }
            graph = graphResult.Graph;

            var deviceResult = await graph.CreateDeviceOutputNodeAsync();
            if (deviceResult.Status != AudioDeviceNodeCreationStatus.Success || deviceResult.DeviceOutputNode is null)
            {
                LogAnalyzerError("创建音频输出失败：" + deviceResult.Status);
                return;
            }
            device = deviceResult.DeviceOutputNode;
            frame = graph.CreateFrameOutputNode();
            var source = MediaSource.CreateFromUri(new Uri(path));
            var inputResult = await graph.CreateMediaSourceAudioInputNodeAsync(source);
            if (inputResult.Status != MediaSourceAudioInputNodeCreationStatus.Success || inputResult.Node is null)
            {
                LogAnalyzerError("读取音乐频谱失败：" + inputResult.Status);
                return;
            }
            input = inputResult.Node;
            input.AddOutgoingConnection(device);
            input.AddOutgoingConnection(frame);
            // 分析器只负责取样，声音仍然由 MediaPlayer 播放，避免双重出声。
            device.OutgoingGain = 0;

            if (generation != _analyzerGeneration)
            {
                DisposeGraph(graph, input, frame, device);
                return;
            }

            _channels = Math.Max(1, (int)frame.EncodingProperties.ChannelCount);
            _sampleRate = Math.Max(8000, (int)frame.EncodingProperties.SampleRate);
            BuildBands(_sampleRate);
            _ringWrite = 0;
            _ringCount = 0;
            graph.QuantumStarted += OnQuantumStarted;
            _graph = graph;
            _deviceOutput = device;
            _frameOutput = frame;
            _audioInput = input;
            graph.Start();
            input.Start();
            AppLog.Info("music", $"频谱分析已连接：{Path.GetFileName(path)} · {_sampleRate}Hz · {_channels}ch");
        }
        catch (Exception ex)
        {
            LogAnalyzerError("频谱分析启动失败：" + ex.Message);
            DisposeGraph(graph, input, frame, device);
        }
    }

    private void OnQuantumStarted(AudioGraph sender, object args)
    {
        if (_frameOutput is null) return;
        try
        {
            using var frame = _frameOutput.GetFrame();
            if (frame is null) return;
            var channels = Math.Max(1, _channels);
            using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
            using var reference = buffer.CreateReference();
            var access = reference.As<IMemoryBufferByteAccess>();
            access.GetBuffer(out var pointer, out var capacity);
            var sampleCount = (int)(capacity / 4) / channels;
            if (sampleCount <= 0) return;
            var total = sampleCount * channels;
            if (_frameBuffer.Length < total) _frameBuffer = new float[total];
            Marshal.Copy(pointer, _frameBuffer, 0, total);

            for (var i = 0; i < sampleCount; i++)
            {
                _ring[_ringWrite] = _frameBuffer[i * channels];
                _ringWrite = (_ringWrite + 1) % FftSize;
                if (_ringCount < FftSize) _ringCount++;
            }
            if (_ringCount >= FftSize) UpdateSpectrum();
        }
        catch (Exception ex)
        {
            var now = Environment.TickCount64;
            if (now - _lastAnalyzerError > 5000)
            {
                _lastAnalyzerError = now;
                LogAnalyzerError("音频取样失败：" + ex.Message);
            }
        }
    }

    private void UpdateSpectrum()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _fftRe[i] = _ring[(_ringWrite + i) % FftSize] * _window[i];
            _fftIm[i] = 0;
        }

        for (int i = 1, j = 0; i < FftSize; i++)
        {
            var bit = FftSize >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (_fftRe[i], _fftRe[j]) = (_fftRe[j], _fftRe[i]);
                (_fftIm[i], _fftIm[j]) = (_fftIm[j], _fftIm[i]);
            }
        }

        for (var len = 2; len <= FftSize; len <<= 1)
        {
            var angle = -2.0 * Math.PI / len;
            var wLenRe = Math.Cos(angle);
            var wLenIm = Math.Sin(angle);
            var half = len >> 1;
            for (var i = 0; i < FftSize; i += len)
            {
                double wRe = 1, wIm = 0;
                for (var k = 0; k < half; k++)
                {
                    var uRe = _fftRe[i + k];
                    var uIm = _fftIm[i + k];
                    var vRe = _fftRe[i + k + half] * wRe - _fftIm[i + k + half] * wIm;
                    var vIm = _fftRe[i + k + half] * wIm + _fftIm[i + k + half] * wRe;
                    _fftRe[i + k] = (float)(uRe + vRe);
                    _fftIm[i + k] = (float)(uIm + vIm);
                    _fftRe[i + k + half] = (float)(uRe - vRe);
                    _fftIm[i + k + half] = (float)(uIm - vIm);
                    var nextRe = wRe * wLenRe - wIm * wLenIm;
                    wIm = wRe * wLenIm + wIm * wLenRe;
                    wRe = nextRe;
                }
            }
        }

        lock (_spectrumGate)
        {
            for (var band = 0; band < BandCount; band++)
            {
                var lo = _bandLow[band];
                var hi = Math.Max(lo + 1, _bandHigh[band]);
                double sum = 0;
                for (var k = lo; k < hi && k < FftSize / 2; k++)
                    sum += Math.Sqrt(_fftRe[k] * _fftRe[k] + _fftIm[k] * _fftIm[k]);
                var avg = sum / (hi - lo) / (FftSize * 0.5);
                var target = (float)Math.Clamp(Math.Log10(1 + avg * 48.0) / 0.92, 0, 1);
                _bandTargets[band] = target;
                var current = _bands[band];
                _bands[band] = target > current
                    ? current + (target - current) * 0.62f
                    : current + (target - current) * 0.29f;
            }
        }
    }

    private void BuildBands(int sampleRate)
    {
        const double minHz = 45.0;
        const double maxHz = 16000.0;
        for (var band = 0; band < BandCount; band++)
        {
            var f0 = minHz * Math.Pow(maxHz / minHz, band / (double)BandCount);
            var f1 = minHz * Math.Pow(maxHz / minHz, (band + 1) / (double)BandCount);
            var lo = Math.Max(1, (int)Math.Round(f0 * FftSize / sampleRate));
            var hi = Math.Max(lo + 1, (int)Math.Round(f1 * FftSize / sampleRate));
            _bandLow[band] = lo;
            _bandHigh[band] = Math.Min(FftSize / 2, hi);
        }
    }

    private void PauseAnalyzer()
    {
        try { _graph?.Stop(); } catch { }
    }

    private void ResumeAnalyzer()
    {
        if (_graph is not null)
        {
            try { _graph.Start(); return; } catch { }
        }
        if (CurrentPath is not null) _ = StartAnalyzerAsync(CurrentPath);
    }

    private void StopAnalyzer()
    {
        Interlocked.Increment(ref _analyzerGeneration);
        DisposeAnalyzer();
    }

    private void DisposeAnalyzer()
    {
        var graph = _graph;
        var input = _audioInput;
        var frame = _frameOutput;
        var device = _deviceOutput;
        _graph = null;
        _audioInput = null;
        _frameOutput = null;
        _deviceOutput = null;
        DisposeGraph(graph, input, frame, device);
    }

    private void DisposeGraph(AudioGraph? graph, MediaSourceAudioInputNode? input, AudioFrameOutputNode? frame, AudioDeviceOutputNode? device)
    {
        if (graph is not null)
        {
            try { graph.QuantumStarted -= OnQuantumStarted; } catch { }
            try { graph.Stop(); } catch { }
        }
        try { input?.Stop(); } catch { }
        try { input?.Dispose(); } catch { }
        try { frame?.Dispose(); } catch { }
        try { device?.Dispose(); } catch { }
        try { graph?.Dispose(); } catch { }
    }

    private void LogAnalyzerError(string message)
    {
        AppLog.Warn("music", message);
    }

    private void OnEnded()
    {
        switch (_mode)
        {
            case "one":
                PlayCurrent(true);
                break;
            case "shuffle":
                Next(false);
                break;
            default:
                _index = (_index + 1) % Math.Max(1, _tracks.Count);
                PlayCurrent(true);
                break;
        }
    }

    private void OnFailed()
    {
        if (_tracks.Count <= 1) { Notify(); return; }
        Next(false);
    }

    private void Notify()
    {
        if (_dispatcher is null) { Changed?.Invoke(this, EventArgs.Empty); return; }
        _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public static string NormalizeMode(string? mode) => mode switch
    {
        "one" => "one",
        "shuffle" => "shuffle",
        _ => "list"
    };

    public static string ModeLabel(string? mode) => mode switch
    {
        "one" => "单曲循环",
        "shuffle" => "随机播放",
        _ => "列表循环"
    };

    public void Dispose()
    {
        StopAnalyzer();
        try { _player.Dispose(); } catch { }
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}

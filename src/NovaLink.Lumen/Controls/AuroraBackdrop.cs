using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using WinRT.Interop;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// 单层 Win2D 背景 + 液态玻璃：程序化星空、高斯模糊、SDF 位移与色散、色调曝光、圆角遮罩与菲涅尔扫光。
/// 整屏只渲染一张场景纹理，卡片按布局注册，文字与图标仍由原生 XAML 渲染。
/// </summary>
public sealed class AuroraBackdrop : UserControl
{
    private readonly CanvasControl _canvas;
    private readonly List<Star> _stars = new();
    private readonly Random _random = new(20261024);
    private readonly List<GlassPane> _panes = new();
    private CanvasRenderTarget? _frost;
    private readonly Queue<float> _frameTimes = new();
    private readonly GlassSettings _settings = new();

    private CanvasRenderTarget? _scene;
    private double _sceneScale;
    private long _lastTicks;
    private double _time;
    private bool _running;
    private double _autoScale = 0.0;
    private int _slowFrames;
    private int _fastFrames;
    private float _pointerX = 0.5f;
    private float _pointerY = 0.74f;
    private float _pointerTargetX = 0.5f;
    private float _pointerTargetY = 0.74f;
    private float _pointerStrength;
    private float _pointerTargetStrength;
    private float _musicPulse;
    private float _musicTargetPulse;
    /// <summary>0..1：音乐是否真的在响。没有播放时方块地形几乎静止，避免「没放歌背景自己在抖」。</summary>
    private float _musicActive;
    private bool _musicPlaying;
    private readonly float[] _musicBands = new float[28];
    private readonly float[] _musicBandTargets = new float[28];
    private IntPtr _hwnd;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private sealed record Star(float X, float Y, float Radius, float Phase, float Speed);

    private sealed record BackdropPalette(
        Color Bg, Color SkyTop, Color SkyMid, Color SkyBottom, Color Star, Color PrismA, Color PrismB,
        Color[] Nebula, Color TerrainFar, Color TerrainMid, Color TerrainNear, Color TerrainTop,
        Color TerrainHaze, Color Vignette, byte VignetteTop, byte VignetteBottom, byte VignetteSide,
        Color GlassRim, Color GlassEdge, Color GlassEdgeOuter);

    private static readonly BackdropPalette DarkPalette = new(
        Color.FromArgb(255, 4, 6, 11),
        Color.FromArgb(255, 5, 8, 15),
        Color.FromArgb(255, 7, 14, 23),
        Color.FromArgb(255, 3, 5, 9),
        Color.FromArgb(255, 210, 234, 255),
        Color.FromArgb(255, 124, 235, 214),
        Color.FromArgb(255, 169, 150, 255),
        new[]
        {
            Color.FromArgb(68, 40, 96, 126), Color.FromArgb(58, 78, 60, 138),
            Color.FromArgb(38, 22, 86, 100), Color.FromArgb(30, 96, 200, 255),
            Color.FromArgb(40, 54, 126, 160)
        },
        Color.FromArgb(255, 16, 34, 48), Color.FromArgb(255, 10, 23, 34), Color.FromArgb(255, 4, 10, 17),
        Color.FromArgb(255, 56, 110, 120), Color.FromArgb(255, 4, 6, 11),
        Color.FromArgb(255, 2, 4, 8), 150, 160, 90,
        Color.FromArgb(255, 255, 255, 255),
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(0x33, 0x00, 0x00, 0x00));

    private static readonly BackdropPalette LightPalette = new(
        Color.FromArgb(255, 238, 244, 249),
        Color.FromArgb(255, 248, 251, 254),
        Color.FromArgb(255, 220, 235, 245),
        Color.FromArgb(255, 198, 219, 233),
        Color.FromArgb(255, 255, 255, 255),
        Color.FromArgb(255, 12, 148, 128),
        Color.FromArgb(255, 107, 85, 217),
        new[]
        {
            Color.FromArgb(56, 146, 199, 230), Color.FromArgb(42, 178, 156, 224),
            Color.FromArgb(34, 132, 214, 204), Color.FromArgb(38, 255, 255, 255),
            Color.FromArgb(34, 156, 190, 226)
        },
        Color.FromArgb(255, 180, 204, 218), Color.FromArgb(255, 128, 161, 182), Color.FromArgb(255, 63, 91, 114),
        Color.FromArgb(255, 230, 245, 248), Color.FromArgb(255, 226, 239, 247),
        Color.FromArgb(255, 201, 218, 231), 40, 56, 28,
        Color.FromArgb(255, 35, 58, 74),
        Color.FromArgb(255, 26, 43, 59), Color.FromArgb(0x14, 0x1A, 0x2B, 0x3B));

    private BackdropPalette _palette = DarkPalette;

    public AuroraBackdrop()
    {
        IsHitTestVisible = false;
        _canvas = new CanvasControl { ClearColor = _palette.Bg };
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (_, _) =>
        {
            _scene = null;
            _sceneScale = 0;
            _frost?.Dispose();
            _frost = null;
        };
        Content = _canvas;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>当前玻璃参数，修改后下一帧生效。/summary>
    public GlassSettings Glass => _settings;

    public void ApplyTheme(bool light)
    {
        _palette = light ? LightPalette : DarkPalette;
        _canvas.ClearColor = _palette.Bg;
        _canvas.Invalidate();
    }

    /// <summary>鼠标位置（DIP）驱动近处方块隆起，归一化后与渲染分辨率无关。</summary>
    public void SetPointer(double x, double y)
    {
        var w = Math.Max(1.0, ActualWidth);
        var h = Math.Max(1.0, ActualHeight);
        _pointerTargetX = (float)Math.Clamp(x / w, 0.0, 1.0);
        _pointerTargetY = (float)Math.Clamp(y / h, 0.0, 1.0);
        _pointerTargetStrength = 1f;
    }

    public void ClearPointer() => _pointerTargetStrength = 0f;

    /// <summary>音乐是否正在播放；只在播放时方块地形才持续起伏。</summary>
    public void SetMusicPlaying(bool playing) => _musicPlaying = playing;

    /// <summary>0..1 envelope from the launcher music player; lifts and brightens the block field.</summary>
    public void SetMusicPulse(double value)
    {
        _musicTargetPulse = (float)Math.Clamp(value, 0, 1);
        for (var i = 0; i < _musicBandTargets.Length; i++) _musicBandTargets[i] = _musicTargetPulse;
    }

    /// <summary>真实音频的 28 段频谱（低音在前），每一列方块按对应频段抬起。</summary>
    public void SetMusicSpectrum(IReadOnlyList<double> bands)
    {
        var sum = 0.0;
        for (var i = 0; i < _musicBandTargets.Length; i++)
        {
            var value = i < bands.Count ? (float)Math.Clamp(bands[i], 0, 1) : 0f;
            _musicBandTargets[i] = value;
            sum += value;
        }
        _musicTargetPulse = (float)Math.Clamp(sum / _musicBandTargets.Length, 0, 1);
    }

    /// <summary>每帧获取卡片位置的回调；由页面跟踪器提供，保证滚动同步。/summary>
    public Func<IEnumerable<GlassPane>>? PaneProvider { get; set; }

    /// <summary>滚动时暂停折射层，避免卡片移动与玻璃重绘之间的单帧错位形成残影。/summary>
    public bool SuppressGlass { get; set; }

    /// <summary>注册需要折射的卡片（窗。DIP 坐标）。布局变化时调用。/summary>
    public void SetPanes(IEnumerable<GlassPane> panes)
    {
        _panes.Clear();
        foreach (var pane in panes)
        {
            if (pane.Width < 24 || pane.Height < 24) continue;
            _panes.Add(pane);
        }
        _canvas.Invalidate();
    }

    public void ClearPanes()
    {
        if (_panes.Count == 0) return;
        _panes.Clear();
        _canvas.Invalidate();
    }

    /// <summary>
    /// 立刻按当前视觉树重算一遍透镜矩形。列表刚重建完时调用，避免新行卡先露透明底、
    /// 等下一次定时重扫才补上玻璃。
    /// </summary>
    public void RefreshPanes()
    {
        if (PaneProvider is null) return;
        var noLens = string.Equals(Environment.GetEnvironmentVariable("NOVALINK_NO_LENS"), "1", StringComparison.Ordinal);
        if (noLens) return;
        _panes.Clear();
        foreach (var pane in PaneProvider())
            if (pane.Width >= 24 && pane.Height >= 24) _panes.Add(pane);
        _canvas.Invalidate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_stars.Count == 0)
        {
            for (var i = 0; i < 150; i++)
            {
                _stars.Add(new Star(
                    (float)_random.NextDouble(),
                    (float)_random.NextDouble(),
                    0.5f + (float)_random.NextDouble() * 1.5f,
                    (float)_random.NextDouble() * 6.283f,
                    0.35f + (float)_random.NextDouble() * 1.4f));
            }
        }
        try { _hwnd = WindowNative.GetWindowHandle(MainWindow.Current!); } catch { _hwnd = IntPtr.Zero; }
        _running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        _canvas.RemoveFromVisualTree();
    }

    private void OnRendering(object? sender, object e)
    {
        if (!_running) return;
        var now = Environment.TickCount64;

        // 只对最小化做限制。正常显示时跟随显示器刷新率，不再人为锁 30/60 帧。
        if (_hwnd == IntPtr.Zero)
        {
            try { _hwnd = WindowNative.GetWindowHandle(MainWindow.Current!); } catch { _lastTicks = now; return; }
        }
        if (_hwnd != IntPtr.Zero && IsIconic(_hwnd))
        {
            _lastTicks = now;
            return;
        }

        var delta = _lastTicks == 0 ? 0.016 : (now - _lastTicks) / 1000.0;
        _lastTicks = now;
        _time += Math.Min(delta, 0.1);
        UpdatePointerFromCursor();
        var follow = (float)(1.0 - Math.Exp(-11.0 * delta));
        _pointerX += (_pointerTargetX - _pointerX) * follow;
        _pointerY += (_pointerTargetY - _pointerY) * follow;
        _pointerStrength += (_pointerTargetStrength - _pointerStrength) * (float)(1.0 - Math.Exp(-7.0 * delta));
        _musicPulse += (_musicTargetPulse - _musicPulse) * (float)(1.0 - Math.Exp(-14.0 * delta));
        _musicActive += ((_musicPlaying ? 1f : 0f) - _musicActive) * (float)(1.0 - Math.Exp(-4.0 * delta));
        var bandFollow = (float)(1.0 - Math.Exp(-20.0 * delta));
        for (var i = 0; i < _musicBands.Length; i++)
            _musicBands[i] += (_musicBandTargets[i] - _musicBands[i]) * bandFollow;
        TrackFrame((float)delta);
        _canvas.Invalidate();
    }

    private void UpdatePointerFromCursor()
    {
        if (_hwnd == IntPtr.Zero)
        {
            try { _hwnd = WindowNative.GetWindowHandle(MainWindow.Current!); } catch { return; }
        }
        if (XamlRoot is null) return;
        if (!GetCursorPos(out var screen)) return;
        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(_hwnd, ref origin)) return;
        var scale = XamlRoot.RasterizationScale <= 0 ? 1.0 : XamlRoot.RasterizationScale;
        var local = TransformToVisual(null).TransformPoint(new Point(0, 0));
        var x = (screen.X - origin.X) / scale - local.X;
        var y = (screen.Y - origin.Y) / scale - local.Y;
        if (x < 0 || y < 0 || x > ActualWidth || y > ActualHeight)
        {
            _pointerTargetStrength = 0f;
            return;
        }
        _pointerTargetX = (float)Math.Clamp(x / Math.Max(1.0, ActualWidth), 0.0, 1.0);
        _pointerTargetY = (float)Math.Clamp(y / Math.Max(1.0, ActualHeight), 0.0, 1.0);
        _pointerTargetStrength = 1f;
    }

    private void TrackFrame(float delta)
    {
        if (_settings.Quality != GlassQuality.Auto) return;
        _frameTimes.Enqueue(delta);
        while (_frameTimes.Count > 120) _frameTimes.Dequeue();
        if (delta > 0.045f) _slowFrames++; else _slowFrames = 0;
        if (delta < 0.0125f) _fastFrames++; else _fastFrames = 0;
        if (_slowFrames >= 24)
        {
            _autoScale = Math.Max(-0.14, _autoScale - 0.04);
            _slowFrames = 0;
        }
        else if (_fastFrames >= 240)
        {
            _autoScale = Math.Min(0, _autoScale + 0.02);
            _fastFrames = 0;
        }
    }

    private double ResolveScale(double area)
    {
        var baseScale = _settings.Quality switch
        {
            GlassQuality.High => 0.85,
            GlassQuality.Balanced => 0.70,
            GlassQuality.Light => 0.55,
            GlassQuality.Saver => 0.50,
            GlassQuality.Off => 0,
            _ => area > 3_400_000 ? 0.55 : area > 2_200_000 ? 0.70 : 0.85
        };
        if (baseScale <= 0) return 0;
        return Math.Clamp(baseScale + _autoScale, 0.5, 0.9);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width < 2 || height < 2) return;

        var ds = args.DrawingSession;
        var area = width * height;
        var scale = ResolveScale(area);
        var t = (float)_time;
        var tiny = area > 3_400_000;

        if (scale <= 0)
        {
            DrawScene(ds, width, height, t, tiny, _pointerX, _pointerY, _pointerStrength);
            DrawVignette(ds, width, height, _palette);
            return;
        }

        EnsureScene(sender, width, height, scale);
        if (_scene is null)
        {
            DrawScene(ds, width, height, t, tiny, _pointerX, _pointerY, _pointerStrength);
            DrawVignette(ds, width, height, _palette);
            return;
        }

        using (var s = _scene.CreateDrawingSession())
        {
            DrawScene(s, (float)(width * scale), (float)(height * scale), t, tiny, _pointerX, _pointerY, _pointerStrength);
        }

        ds.DrawImage(_scene, new Rect(0, 0, width, height));
        var noLens = string.Equals(Environment.GetEnvironmentVariable("NOVALINK_NO_LENS"), "1", StringComparison.Ordinal);
        if (PaneProvider is not null && !noLens)
        {
            _panes.Clear();
            foreach (var pane in PaneProvider())
                if (pane.Width >= 24 && pane.Height >= 24) _panes.Add(pane);
        }
        if (_panes.Count > 0)
        {
            if (SuppressGlass) DrawGlassFallback(ds, sender, width, height);
            else DrawFrostedPanes(ds, sender, width, height, scale);
        }
        if (!tiny) DrawPrism(ds, width, height, t, _palette);
        DrawVignette(ds, width, height, _palette);
    }

    private void EnsureScene(CanvasControl sender, float width, float height, double scale)
    {
        var pixelWidth = Math.Max(2, (int)Math.Round(width * scale));
        var pixelHeight = Math.Max(2, (int)Math.Round(height * scale));
        if (_scene is not null && Math.Abs(_sceneScale - scale) < 0.005 &&
            Math.Abs(_scene.SizeInPixels.Width - pixelWidth) < 2 && Math.Abs(_scene.SizeInPixels.Height - pixelHeight) < 2)
            return;
        _scene = new CanvasRenderTarget(sender, pixelWidth, pixelHeight, 96f);
        _sceneScale = scale;
    }

    private void DrawScene(CanvasDrawingSession ds, float width, float height, float t, bool lowQuality,
        float pointerX, float pointerY, float pointerStrength)
    {
        ds.Clear(_palette.Bg);
        DrawSky(ds, width, height, t, _palette);
        DrawNebula(ds, width, height, t, lowQuality, _palette);
        DrawStars(ds, width, height, t, lowQuality, _palette);
        DrawWorld(ds, width, height, t, lowQuality, _palette, pointerX, pointerY, pointerStrength, _musicPulse, _musicBands, _musicActive);
    }

    /// <summary>
    /// 毛玻璃（Lens 参考实现）：整屏场景先降采样做一次高斯模糊，所有卡片都从这张 frost 纹理上按矩形取样。
    /// 旧实现是每张卡一张 SDF 位移贴图 + 三通道色散，每帧每卡三次位移采样，4K 下 GPU 直接吃满；
    /// 而且位移会把 rect 边缘的像素往内拉，描边内侧多出一圈假的亮边（看起来像「两层边」）。
    /// 现在只剩「模糊 + 冷白薄雾 + 高光 + 菲涅尔」，一次模糊全卡复用，也不再有任何位移。
    /// </summary>
    private void DrawFrostedPanes(CanvasDrawingSession ds, CanvasControl sender, float width, float height, double scale)
    {
        if (_scene is null || width < 2 || height < 2) return;

        // 半分辨率：模糊本身就抹掉高频细节，降采样对观感几乎无损，成本直接降到四分之一。
        const double frostScale = 0.5;
        var fw = Math.Max(2, (int)Math.Round(width * frostScale));
        var fh = Math.Max(2, (int)Math.Round(height * frostScale));
        if (_frost is null || _frost.SizeInPixels.Width != fw || _frost.SizeInPixels.Height != fh)
        {
            _frost?.Dispose();
            _frost = new CanvasRenderTarget(sender, fw, fh, 96f);
        }

        // 0.40 系数 + 半分辨率，最终观感约 8~10px 模糊（Lens 的 --lg-fallback-blur 是 10px）。
        var blurAmount = (float)Math.Clamp(_settings.BlurAmount * scale * 0.40, 1.5, 18.0);
        var lightTheme = ReferenceEquals(_palette, LightPalette);
        using (var blur = new GaussianBlurEffect
        {
            Source = _scene,
            BlurAmount = blurAmount,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Speed
        })
        using (var frostDs = _frost.CreateDrawingSession())
        {
            frostDs.Clear(Color.FromArgb(0, 0, 0, 0));
            frostDs.DrawImage(blur, new Rect(0, 0, fw, fh), new Rect(0, 0, _scene.SizeInPixels.Width, _scene.SizeInPixels.Height));
        }

        var invW = (double)fw / width;
        var invH = (double)fh / height;
        var frostWidth = fw;
        var frostHeight = fh;
        foreach (var pane in _panes)
        {
            var rect = new Rect(pane.X, pane.Y, pane.Width, pane.Height);
            if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > width || rect.Top > height) continue;
            var radius = Math.Max(0, pane.Radius);
            var strength = pane.Strength;
            var src = new Rect(pane.X * invW, pane.Y * invH, pane.Width * invW, pane.Height * invH);
            if (src.Width < 2 || src.Height < 2) continue;

            // 边缘折射（Lens 的 .lg-optic-rim）：取样矩形以卡片中心为原点放大 1.006~1.012，
            // 边缘像素被整体外移不到 1px，看起来就是玻璃把背景「挤」了一下。
            // 这是纯采样变换，不用 DisplacementMapEffect（4K 下每卡三次位移采样太贵，已经砍掉）。
            var refract = 1.022 + 0.012 * Math.Clamp(pane.Strength, 0, 1);
            var srcW = src.Width * refract;
            var srcH = src.Height * refract;
            // 取样中心朝光标方向偏 1.2% 卡宽：移动鼠标时玻璃里的背景会跟着轻轻滑，像真玻璃而不是贴图。
            var driftX = (_pointerX * width - (pane.X + pane.Width * 0.5)) / Math.Max(1, width);
            var driftY = (_pointerY * height - (pane.Y + pane.Height * 0.5)) / Math.Max(1, height);
            var srcX = Math.Clamp(src.X + (src.Width - srcW) / 2 + driftX * src.Width * 0.012, 0, Math.Max(0, fw - srcW));
            var srcY = Math.Clamp(src.Y + (src.Height - srcH) / 2 + driftY * src.Height * 0.012, 0, Math.Max(0, fh - srcH));
            var refractSrc = new Rect(srcX, srcY, srcW, srcH);

            using var geometry = BuildRoundedRect(sender, rect, radius);
            using (var layer = ds.CreateLayer(1f, geometry))
            {
                ds.DrawImage(_frost, rect, refractSrc, lightTheme ? 0.92f : 0.97f);
                // 薄雾：浅色主题加冷白（压成高调白，文字对比更稳），深色主题只加一点点白光当"起雾"，
                // 否则深色卡片会被糊成一块发灰的板子，反而更难看。
                var veil = lightTheme
                    ? (byte)Math.Clamp(30 * strength + 12, 0, 110)
                    : (byte)Math.Clamp(9 * strength, 0, 34);
                ds.FillRectangle(rect, Color.FromArgb(veil, lightTheme ? (byte)236 : (byte)255, lightTheme ? (byte)245 : (byte)255, 255));

                var tint = _settings.Tint;
                if (tint.A > 0 && lightTheme)
                    ds.FillRectangle(rect, Color.FromArgb((byte)Math.Clamp(tint.A * 0.30 * strength, 0, 50), tint.R, tint.G, tint.B));

                if (_settings.Exposure > 1.001)
                    ds.FillRectangle(rect, Color.FromArgb((byte)Math.Clamp((_settings.Exposure - 1) * 40, 0, 70), 255, 255, 255));
                else if (_settings.Exposure < 0.999)
                    ds.FillRectangle(rect, Color.FromArgb((byte)Math.Clamp((1 - _settings.Exposure) * 60, 0, 100), 0, 0, 0));

                // 整块玻璃做一次很轻的放大取样（1.03 左右）+ 朝光标方向的小偏移：
                // 这是真实的光学位移，整面连续、没有分界线；苹果玻璃的「厚度感」主要来自它和下面这圈跟手高光。
                DrawCardLight(ds, rect, radius, strength, pane.Hover, _pointerX * width, _pointerY * height);
            }
            // 外缘暗边必须画在裁剪层外面，否则 0.5px 会被裁掉一半。
            DrawOuterEdge(ds, rect, radius, strength);
        }
    }

    /// <summary>滚动/禁用玻璃时只画基底、边缘高光和菲涅尔，保留质感但不做模糊采样，避免拖影与整层消失。</summary>
    private void DrawGlassFallback(CanvasDrawingSession ds, CanvasControl sender, float width, float height)
    {
        foreach (var pane in _panes) DrawFallbackPane(ds, sender, pane, 0.8f);
    }

    /// <summary>不做模糊的玻璃面：只铺冷白薄雾、顶部高光与边缘菲涅尔。</summary>
    private void DrawFallbackPane(CanvasDrawingSession ds, CanvasControl sender, GlassPane pane, float strengthScale = 1f)
    {
        var rect = new Rect(pane.X, pane.Y, pane.Width, pane.Height);
        var radius = Math.Max(0, pane.Radius);
        var strength = pane.Strength * strengthScale;
        using var geometry = BuildRoundedRect(sender, rect, radius);
        using (var layer = ds.CreateLayer(1f, geometry))
        {
            var lightTheme = ReferenceEquals(_palette, LightPalette);
            ds.FillRectangle(rect, lightTheme
                ? Color.FromArgb((byte)Math.Clamp(28 * strength + 10, 0, 100), 236, 245, 255)
                : Color.FromArgb((byte)Math.Clamp(10 * strength, 0, 40), 255, 255, 255));
            DrawCardLight(ds, rect, radius, strength, pane.Hover, _pointerX * _canvas.ActualWidth, _pointerY * _canvas.ActualHeight);
        }
        DrawOuterEdge(ds, rect, radius, strength);
        if (string.Equals(Environment.GetEnvironmentVariable("NOVALINK_DEBUG_LENS"), "1", StringComparison.Ordinal))
            ds.DrawRoundedRectangle(rect, (float)radius, (float)radius, Windows.UI.Color.FromArgb(255, 255, 0, 255), 1.5f);
    }

    /// <summary>
    /// <summary>外缘暗边：玻璃外侧压 0.5px 深色，卡片才有「玻璃片」的厚度，而不是一块发光的糊面。</summary>
    private void DrawOuterEdge(CanvasDrawingSession ds, Rect rect, double radius, double strength)
    {
        var outer = _palette.GlassEdgeOuter;
        var alpha = (byte)Math.Clamp(outer.A * Math.Clamp(strength, 0.35, 1.0), 0, 255);
        if (alpha == 0) return;
        var strokeRect = new Rect(rect.X - 0.25, rect.Y - 0.25, rect.Width + 0.5, rect.Height + 0.5);
        ds.DrawRoundedRectangle(strokeRect, (float)(radius + 0.25), (float)(radius + 0.25),
            Color.FromArgb(alpha, outer.R, outer.G, outer.B), 0.5f);
    }


    /// <summary>
    /// 卡片受光：把光标当成唯一光源。光只照到光标附近的玻璃（按到卡片的距离衰减），
    /// 并且是从光标方向「照进整张卡」——卡面沿光向铺一层渐变、光标落点盖一团柔光、
    /// 迎光的一侧边缘再收一圈高光。三者共用同一套方向与衰减，卡片内部才不会和边缘脱节。
    /// </summary>
    private void DrawCardLight(CanvasDrawingSession ds, Rect rect, double radius, double strength, double hover, double pointerX, double pointerY)
    {
        var width = Math.Max(1.0, rect.Width);
        var height = Math.Max(1.0, rect.Height);
        var cx = rect.X + width * 0.5;
        var cy = rect.Y + height * 0.5;

        // 光标到卡片矩形最近点的距离，就是这张卡接收到的光程；超出光程就完全没有光，
        // 远处的卡片不会再被点亮（旧版是给每张卡都画一圈跟手高光）。
        var nearestX = Math.Clamp(pointerX, rect.X, rect.X + width);
        var nearestY = Math.Clamp(pointerY, rect.Y, rect.Y + height);
        var distX = pointerX - nearestX;
        var distY = pointerY - nearestY;
        var distance = Math.Sqrt(distX * distX + distY * distY);
        var inside = distance < 0.5;
        // 光程随卡片尺寸放大：大面板要更近才吃满光，小卡片近处也不至于一片死白。
        // 平方衰减：光标离开一张卡的范围后，光要掉得够快，屏幕另一头不能跟着亮。
        var reach = Math.Max(width, height) * 0.42 + 120;
        var proximity = Math.Clamp(1.0 - distance / reach, 0.0, 1.0);
        proximity = proximity * proximity * (3.0 - 2.0 * proximity);
        proximity *= proximity;
        var amount = Math.Clamp(strength * proximity * (1.0 + (inside ? 0.35 : 0.0)), 0.0, 1.35);

        // 远处的卡片直接跳过：既不再「漏光」，也省掉这些绘制。
        if (amount < 0.02) return;

        var lightTheme = ReferenceEquals(_palette, LightPalette);

        // 光向：卡片中心 → 光标。这样鼠标在卡片里移动时，受光面也会跟着变，像光在玻璃上扫过。
        var dirX = pointerX - cx;
        var dirY = pointerY - cy;
        var dirLength = Math.Sqrt(dirX * dirX + dirY * dirY);
        if (dirLength < 2) { dirX = 0; dirY = -1; dirLength = 1; }
        var nx = dirX / dirLength;
        var ny = dirY / dirLength;
        var half = Math.Abs(nx) * width * 0.5 + Math.Abs(ny) * height * 0.5;

        // ① 面光：从背光侧到迎光侧的一整层渐变，光铺满整张卡，而不是只在边缘描一条线。
        // 用 Add 混合：光是「加」上去的，卡面被照亮、对比度一起抬起来，而不是蒙一层白雾。
        var previousBlend = ds.Blend;
        ds.Blend = CanvasBlend.Add;
        var washPeak = (byte)Math.Clamp((lightTheme ? 26.0 : 20.0) * amount, 0, 56);
        using (var wash = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, 250, 252, 255) },
            new CanvasGradientStop { Position = 0.40f, Color = Color.FromArgb((byte)(washPeak * 0.16), 250, 252, 255) },
            new CanvasGradientStop { Position = 0.76f, Color = Color.FromArgb((byte)(washPeak * 0.56), 250, 252, 255) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(washPeak, 250, 252, 255) }
        })
        {
            StartPoint = new Vector2((float)(cx - nx * half), (float)(cy - ny * half)),
            EndPoint = new Vector2((float)(cx + nx * half), (float)(cy + ny * half))
        })
        {
            ds.FillRectangle(rect, wash);
        }

        // ② 光斑：在光标落在卡片上的最近点盖一团柔光，光「真的落到这块玻璃上」，
        // 并且和下面那圈边缘高光连成一片，不再像贴了一张独立的高光贴纸。
        // 光斑半径跟着卡片走：普通卡片一口吃掉整张（整卡被照入），特大面板只亮光标周围一小片。
        var poolRadius = (float)Math.Clamp(Math.Max(width, height) * 0.55, 60.0, 300.0);
        var poolPeak = (byte)Math.Clamp((lightTheme ? 58.0 : 72.0) * amount, 0, 110);
        using (var pool = new CanvasRadialGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(poolPeak, 250, 252, 255) },
            new CanvasGradientStop { Position = 0.32f, Color = Color.FromArgb((byte)(poolPeak * 0.52), 250, 252, 255) },
            new CanvasGradientStop { Position = 0.68f, Color = Color.FromArgb((byte)(poolPeak * 0.16), 250, 252, 255) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, 250, 252, 255) }
        })
        {
            Center = new Vector2((float)nearestX, (float)nearestY),
            OriginOffset = Vector2.Zero,
            RadiusX = poolRadius,
            RadiusY = poolRadius
        })
        {
            ds.FillRectangle(rect, pool);
        }
        // ③ 迎光边缘：同一套方向与衰减，只是更亮更细，作为「玻璃厚度」的收边。
        // 依旧用 Add 混合 —— 边缘和卡面是同一束光，不能一边是光、一边是刷上去的白漆。
        // Add 混合下颜色是「加进去的光」：浅色主题的边缘色是深蓝，加进去等于没加，
        // 所以浅色主题统一用白光，深色主题沿用主题边缘色。
        var edge = lightTheme ? Color.FromArgb(255, 255, 255, 255) : _palette.GlassEdge;
        var peak = (byte)Math.Clamp((lightTheme ? 150 : 140) * amount, 12, lightTheme ? (byte)172 : (byte)158);
        var brush = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, edge.R, edge.G, edge.B) },
            new CanvasGradientStop { Position = 0.38f, Color = Color.FromArgb((byte)(peak * 0.05), edge.R, edge.G, edge.B) },
            new CanvasGradientStop { Position = 0.74f, Color = Color.FromArgb((byte)(peak * 0.38), edge.R, edge.G, edge.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(peak, edge.R, edge.G, edge.B) }
        })
        {
            StartPoint = new Vector2((float)(cx - nx * half), (float)(cy - ny * half)),
            EndPoint = new Vector2((float)(cx + nx * half), (float)(cy + ny * half))
        };
        const double inset = 0.7;
        var ringRect = new Rect(rect.X + inset, rect.Y + inset, Math.Max(1, rect.Width - inset * 2), Math.Max(1, rect.Height - inset * 2));
        // 先铺一圈更宽、更淡的光，再叠细亮边 —— 单一细线会像描边，两层才有「光在玻璃里散开」的质感。
        var glowWidth = (float)Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.042, 4.0, 11.0);
        var glowRect = new Rect(rect.X + glowWidth * 0.5, rect.Y + glowWidth * 0.5,
            Math.Max(1, rect.Width - glowWidth), Math.Max(1, rect.Height - glowWidth));
        var glowBrush = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, edge.R, edge.G, edge.B) },
            new CanvasGradientStop { Position = 0.62f, Color = Color.FromArgb((byte)(peak * 0.04), edge.R, edge.G, edge.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb((byte)(peak * 0.30), edge.R, edge.G, edge.B) }
        })
        {
            StartPoint = new Vector2((float)(cx - nx * half), (float)(cy - ny * half)),
            EndPoint = new Vector2((float)(cx + nx * half), (float)(cy + ny * half))
        };
        ds.Blend = CanvasBlend.Add;
        ds.DrawRoundedRectangle(glowRect, (float)Math.Max(0, radius - glowWidth * 0.5), (float)Math.Max(0, radius - glowWidth * 0.5), glowBrush, glowWidth);
        ds.DrawRoundedRectangle(ringRect, (float)Math.Max(0, radius - inset), (float)Math.Max(0, radius - inset), brush, 1.4f);
        ds.Blend = previousBlend;
    }

    private static CanvasGeometry BuildRoundedRect(ICanvasResourceCreator sender, Rect rect, double radius)
    {
        using var builder = new CanvasPathBuilder(sender);
        var r = (float)Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var x = (float)rect.X;
        var y = (float)rect.Y;
        var w = (float)rect.Width;
        var h = (float)rect.Height;
        builder.BeginFigure(new Vector2(x + r, y));
        builder.AddLine(new Vector2(x + w - r, y));
        builder.AddArc(new Vector2(x + w, y + r), r, r, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        builder.AddLine(new Vector2(x + w, y + h - r));
        builder.AddArc(new Vector2(x + w - r, y + h), r, r, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        builder.AddLine(new Vector2(x + r, y + h));
        builder.AddArc(new Vector2(x, y + h - r), r, r, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        builder.AddLine(new Vector2(x, y + r));
        builder.AddArc(new Vector2(x + r, y), r, r, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static void DrawSky(CanvasDrawingSession ds, float width, float height, float t, BackdropPalette p)
    {
        var top = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = p.SkyTop },
            new CanvasGradientStop { Position = 0.55f, Color = p.SkyMid },
            new CanvasGradientStop { Position = 1f, Color = p.SkyBottom }
        })
        {
            StartPoint = new Vector2(0, 0),
            EndPoint = new Vector2(width * 0.2f, height)
        };
        ds.FillRectangle(0, 0, width, height, top);
    }

    private static void DrawNebula(CanvasDrawingSession ds, float width, float height, float t, bool lowQuality, BackdropPalette p)
    {
        DrawBlob(ds, width * 0.78f + MathF.Sin(t * 0.12f) * width * 0.02f,
            height * 0.18f + MathF.Cos(t * 0.10f) * height * 0.02f,
            width * 0.34f, height * 0.40f, p.Nebula[0], 1.0f);
        DrawBlob(ds, width * 0.62f + MathF.Cos(t * 0.09f) * width * 0.03f,
            height * 0.34f + MathF.Sin(t * 0.11f) * height * 0.03f,
            width * 0.24f, height * 0.30f, p.Nebula[1], 0.9f);
        if (lowQuality) return;
        DrawBlob(ds, width * 0.12f + MathF.Sin(t * 0.07f) * width * 0.03f,
            height * 0.82f + MathF.Cos(t * 0.08f) * height * 0.02f,
            width * 0.30f, height * 0.30f, p.Nebula[2], 0.8f);
        DrawBlob(ds, width * 0.40f, height * 0.08f, width * 0.18f, height * 0.16f, p.Nebula[3], 0.6f);
        DrawBlob(ds, width * 0.82f, height * 0.58f + MathF.Sin(t * 0.08f) * height * 0.02f,
            width * 0.26f, height * 0.28f, p.Nebula[4], 0.85f);
    }

    private static void DrawBlob(CanvasDrawingSession ds, float cx, float cy, float rx, float ry, Color color, float opacity)
    {
        for (var i = 0; i < 5; i++)
        {
            var scale = 1f - i * 0.16f;
            var alpha = (byte)(color.A * opacity * (0.18f + i * 0.16f));
            ds.FillEllipse(cx, cy, rx * scale, ry * scale, Color.FromArgb(alpha, color.R, color.G, color.B));
        }
    }

    private void DrawStars(CanvasDrawingSession ds, float width, float height, float t, bool lowQuality, BackdropPalette p)
    {
        var count = lowQuality ? _stars.Count / 2 : _stars.Count;
        for (var i = 0; i < count; i++)
        {
            var star = _stars[i];
            var twinkle = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin(t * star.Speed + star.Phase));
            var alpha = (byte)((p == LightPalette ? 78 : 120) * twinkle);
            ds.FillCircle(star.X * width, star.Y * height, star.Radius, Color.FromArgb(alpha, p.Star.R, p.Star.G, p.Star.B));
        }
    }

    private static void DrawWorld(CanvasDrawingSession ds, float width, float height, float t, bool lowQuality, BackdropPalette p,
        float pointerX, float pointerY, float pointerStrength, float musicPulse, float[] musicBands, float musicActive)
    {
        var horizon = height * 0.58f;
        if (ReferenceEquals(p, LightPalette))
        {
            var mist = new CanvasLinearGradientBrush(ds, new[]
            {
                new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, p.TerrainFar.R, p.TerrainFar.G, p.TerrainFar.B) },
                new CanvasGradientStop { Position = 0.52f, Color = Color.FromArgb(48, p.TerrainFar.R, p.TerrainFar.G, p.TerrainFar.B) },
                new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, p.TerrainFar.R, p.TerrainFar.G, p.TerrainFar.B) }
            })
            {
                StartPoint = new Vector2(0, horizon - height * 0.22f),
                EndPoint = new Vector2(0, height)
            };
            ds.FillRectangle(0, horizon - height * 0.22f, width, height * 0.72f, mist);
            return;
        }
        var baseBrush = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = p.TerrainMid },
            new CanvasGradientStop { Position = 0.55f, Color = p.TerrainFar },
            new CanvasGradientStop { Position = 1f, Color = p.Bg }
        })
        {
            StartPoint = new Vector2(0, horizon),
            EndPoint = new Vector2(0, height)
        };
        ds.FillRectangle(0, horizon - 2, width, height - horizon + 2, baseBrush);

        DrawRidge(ds, width, horizon, height * 0.075f, t, p, 0.018f, 1.7f, 118);
        DrawRidge(ds, width, horizon, height * 0.105f, t, p, 0.032f, 5.3f, 174);
        if (lowQuality)
        {
            DrawTerrain(ds, width, height, t, 8, 18, p, pointerX, pointerY, pointerStrength, musicPulse, musicBands, musicActive);
            return;
        }
        DrawRidge(ds, width, horizon, height * 0.145f, t, p, 0.052f, 9.1f, 232);
        DrawTerrain(ds, width, height, t, 13, 28, p, pointerX, pointerY, pointerStrength, musicPulse, musicBands, musicActive);

        var glow = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, p.TerrainTop.R, p.TerrainTop.G, p.TerrainTop.B) },
            new CanvasGradientStop { Position = 0.55f, Color = Color.FromArgb(p == LightPalette ? (byte)28 : (byte)18, p.TerrainTop.R, p.TerrainTop.G, p.TerrainTop.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, p.TerrainTop.R, p.TerrainTop.G, p.TerrainTop.B) }
        })
        {
            StartPoint = new Vector2(0, horizon - height * 0.04f),
            EndPoint = new Vector2(0, horizon + height * 0.06f)
        };
        ds.FillRectangle(0, horizon - height * 0.04f, width, height * 0.1f, glow);
    }

    private static void DrawRidge(CanvasDrawingSession ds, float width, float horizon, float amplitude, float t,
        BackdropPalette p, float speed, float seed, byte alpha)
    {
        const int steps = 56;
        var points = new Vector2[steps + 2];
        points[0] = new Vector2(-4, horizon + 18);
        for (var i = 0; i < steps; i++)
        {
            var x = i * width / (steps - 1f);
            var n = Noise(i * 0.31f + seed + t * speed, seed * 1.9f + t * speed * 0.6f);
            var y = horizon - amplitude * (0.16f + n * 0.84f);
            points[i + 1] = new Vector2(x, y);
        }
        points[^1] = new Vector2(width + 4, horizon + 18);
        using var geometry = CanvasGeometry.CreatePolygon(ds, points);
        var color = Color.FromArgb(alpha, p.TerrainFar.R, p.TerrainFar.G, p.TerrainFar.B);
        ds.FillGeometry(geometry, color);
    }

    private static void DrawTerrain(CanvasDrawingSession ds, float width, float height, float t, int rows, int cols, BackdropPalette p,
        float pointerX, float pointerY, float pointerStrength, float musicPulse, float[] musicBands, float musicActive)
    {
        var horizon = height * 0.58f;
        // 没有音乐在放时把地形的蠕动压到几乎静止（0.12 倍速），只有播放时才是完整的起伏。
        var motion = 0.12f + 0.88f * Math.Clamp(musicActive, 0f, 1f);
        var drift = t * 0.016f * motion;
        var baseCell = width / (cols * 1.24f);
        var light = p == LightPalette;
        var pointerPixelX = pointerX * width;
        var pointerPixelY = pointerY * height;
        var pointerOn = pointerStrength > 0.01f;
        for (var row = 0; row < rows; row++)
        {
            var z = (row + 1f) / rows;
            var y = horizon + MathF.Pow(z, 1.62f) * (height - horizon) * 1.06f;
            var cell = baseCell * (0.52f + z * 1.28f);
            var centerX = width * 0.5f + MathF.Sin(t * 0.045f * motion) * width * 0.012f;
            var startX = centerX - cols * cell * 0.5f;
            for (var col = 0; col < cols; col++)
            {
                var u = (col + 0.5f) / cols * 2f - 1f;
                var edge = 1f - SmoothStep(0.60f, 1f, MathF.Abs(u));
                var n = Noise((col - cols * 0.5f) * 0.34f + drift, z * 2.2f + drift * 0.7f);
                var bump = MathF.Pow(n, 1.55f);
                var band = musicBands.Length > 0
                    ? musicBands[(col + row * 2) % musicBands.Length]
                    : musicPulse;
                var blockH = (height * 0.006f + bump * height * 0.125f)
                             * (0.25f + z * 1.32f) * (0.20f + edge * 1.05f)
                             * (1f + musicPulse * 0.12f + band * 0.42f);
                blockH += height * (0.006f + band * 0.105f) * (0.35f + z);
                if (blockH < 1f) continue;
                var x = startX + col * cell;
                var influence = 0f;
                if (pointerOn)
                {
                    var dx = (x + cell * 0.54f - pointerPixelX) / (width * 0.25f);
                    var dy = (y - blockH * 0.5f - pointerPixelY) / (height * 0.32f);
                    influence = MathF.Exp(-(dx * dx + dy * dy) * 1.8f) * pointerStrength;
                    blockH += height * 0.12f * influence * (0.32f + z * 1.02f);
                }
                var top = y - blockH;
                var body = Lerp(p.TerrainFar, p.TerrainNear, 0.18f + z * 0.82f);
                var side = Lerp(body, Color.FromArgb(255, 0, 0, 0), light ? 0.26f : 0.46f);
                var cap = Lerp(p.TerrainTop, body, 0.20f + (1f - edge) * 0.45f);
                if (influence > 0f) cap = Lerp(cap, p.TerrainTop, influence * 0.68f);
                if (band > 0.01f) cap = Lerp(cap, p.TerrainTop, band * 0.34f);
                var capH = MathF.Max(3f, cell * (0.28f + z * 0.20f));
                var frontTop = top + capH * 0.45f;
                ds.FillRectangle(x, frontTop, cell * 1.08f, MathF.Max(1f, blockH - capH * 0.45f), body);
                ds.FillRectangle(x, frontTop, MathF.Max(1f, cell * 0.20f), MathF.Max(1f, blockH - capH * 0.45f), side);
                var topFace = new[]
                {
                    new Vector2(x, frontTop),
                    new Vector2(x + cell * 0.18f, top),
                    new Vector2(x + cell * 1.08f, top),
                    new Vector2(x + cell * 0.90f, frontTop)
                };
                using (var face = CanvasGeometry.CreatePolygon(ds, topFace))
                    ds.FillGeometry(face, cap);
                var rimAlpha = (byte)Math.Clamp((light ? 120 : 95) + influence * 140f + musicPulse * 30f + band * 95f, 0f, 255f);
                var rim = Color.FromArgb(rimAlpha, p.TerrainTop.R, p.TerrainTop.G, p.TerrainTop.B);
                ds.DrawLine(x, frontTop + 0.5f, x + cell * 1.08f, frontTop + 0.5f, rim, 1f + influence * 1.1f);
            }
        }
    }

    private static float Noise(float x, float y)
    {
        var n = MathF.Sin(x * 1.93f + y * 0.77f) * 0.52f
                + MathF.Sin(x * 0.61f - y * 2.13f + 1.7f) * 0.31f
                + MathF.Sin(x * 2.71f + y * 1.37f + 4.2f) * 0.17f;
        return Math.Clamp(0.5f + 0.5f * n, 0f, 1f);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var v = Math.Clamp((x - edge0) / MathF.Max(0.0001f, edge1 - edge0), 0f, 1f);
        return v * v * (3f - 2f * v);
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static void DrawPrism(CanvasDrawingSession ds, float width, float height, float t, BackdropPalette p)
    {
        var cx = width * 0.52f + MathF.Sin(t * 0.16f) * width * 0.015f;
        var cy = -height * 0.04f;
        var strength = p == LightPalette ? 0.7f : 1f;
        var brush = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, p.PrismA.R, p.PrismA.G, p.PrismA.B) },
            new CanvasGradientStop { Position = 0.45f, Color = Color.FromArgb((byte)(22 * strength), p.PrismA.R, p.PrismA.G, p.PrismA.B) },
            new CanvasGradientStop { Position = 0.7f, Color = Color.FromArgb((byte)(16 * strength), p.PrismB.R, p.PrismB.G, p.PrismB.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, p.PrismB.R, p.PrismB.G, p.PrismB.B) }
        }) { StartPoint = new Vector2(cx - width * 0.42f, 0), EndPoint = new Vector2(cx + width * 0.42f, 0) };
        ds.FillEllipse(cx, cy, width * 0.44f, height * 0.26f, brush);
    }

    private static void DrawVignette(CanvasDrawingSession ds, float width, float height, BackdropPalette p)
    {
        var side = MathF.Max(width, height) * 0.22f;
        var top = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(p.VignetteTop, p.Vignette.R, p.Vignette.G, p.Vignette.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, p.Vignette.R, p.Vignette.G, p.Vignette.B) }
        }) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, side) };
        ds.FillRectangle(0, 0, width, side, top);
        var bottom = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, p.Vignette.R, p.Vignette.G, p.Vignette.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(p.VignetteBottom, p.Vignette.R, p.Vignette.G, p.Vignette.B) }
        }) { StartPoint = new Vector2(0, height - side), EndPoint = new Vector2(0, height) };
        ds.FillRectangle(0, height - side, width, side, bottom);
        var left = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(p.VignetteSide, p.Vignette.R, p.Vignette.G, p.Vignette.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, p.Vignette.R, p.Vignette.G, p.Vignette.B) }
        }) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(side, 0) };
        ds.FillRectangle(0, 0, side, height, left);
    }
}
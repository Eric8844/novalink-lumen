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
    private readonly Dictionary<string, CanvasBitmap> _maps = new();
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
        Color GlassRim);

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
        Color.FromArgb(255, 255, 255, 255));

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
        Color.FromArgb(255, 35, 58, 74));

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
            _maps.Clear();
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
        _maps.Clear();
        _canvas.Invalidate();
    }

    public void ClearPanes()
    {
        if (_panes.Count == 0) return;
        _panes.Clear();
        _maps.Clear();
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
        if (PaneProvider is not null)
        {
            _panes.Clear();
            foreach (var pane in PaneProvider())
                if (pane.Width >= 24 && pane.Height >= 24) _panes.Add(pane);
        }
        if (_panes.Count > 0)
        {
            if (SuppressGlass) DrawGlassFallback(ds, sender, width, height);
            else DrawGlassPanes(ds, sender, width, height, scale, t);
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
        _maps.Clear();
    }

    private void DrawScene(CanvasDrawingSession ds, float width, float height, float t, bool lowQuality,
        float pointerX, float pointerY, float pointerStrength)
    {
        ds.Clear(_palette.Bg);
        DrawSky(ds, width, height, t, _palette);
        DrawNebula(ds, width, height, t, lowQuality, _palette);
        DrawStars(ds, width, height, t, lowQuality, _palette);
        DrawWorld(ds, width, height, t, lowQuality, _palette, pointerX, pointerY, pointerStrength, _musicPulse, _musicBands);
    }

    private void DrawGlassPanes(CanvasDrawingSession ds, CanvasControl sender, float width, float height, double scale, float t)
    {
        var blurRadius = _settings.BlurAmount * scale;
        using var blur = new GaussianBlurEffect
        {
            Source = _scene,
            BlurAmount = (float)blurRadius,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced
        };

        foreach (var pane in _panes)
        {
            var rect = new Rect(pane.X, pane.Y, pane.Width, pane.Height);
            if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > width || rect.Top > height) continue;
            var radius = Math.Max(0, pane.Radius);
            var crop = new Rect(pane.X * scale, pane.Y * scale, pane.Width * scale, pane.Height * scale);
            if (crop.Width < 4 || crop.Height < 4) continue;

            var map = GetDisplacementMap(sender, pane, scale);
            using var cropEffect = new CropEffect { Source = blur, SourceRectangle = crop };

            using var composite = new CompositeEffect { Mode = CanvasComposite.Add };
            var dispersion = _settings.RefDispersion * pane.Strength;
            var baseAmount = (float)(_settings.RefFactor * 7.0 * scale * pane.Strength);
            var channelShift = (float)(_settings.DispersionRange * 0.22 * dispersion);
            AddChannel(composite, cropEffect, map, baseAmount * (1 - channelShift), 0);
            AddChannel(composite, cropEffect, map, baseAmount, 1);
            AddChannel(composite, cropEffect, map, baseAmount * (1 + channelShift), 2);

            using var geometry = BuildRoundedRect(sender, rect, radius);
            using (var layer = ds.CreateLayer(1f, geometry))
            {
                ds.DrawImage(composite, rect, crop, 0.62f);
                var tint = _settings.Tint;
                ds.FillRectangle(rect, Color.FromArgb((byte)(tint.A * pane.Strength), tint.R, tint.G, tint.B));
                ds.FillRectangle(rect, Color.FromArgb(10, 255, 255, 255));
                if (_settings.Exposure > 1.001)
                    ds.FillRectangle(rect, Color.FromArgb((byte)Math.Clamp((_settings.Exposure - 1) * 46, 0, 90), 255, 255, 255));
                else if (_settings.Exposure < 0.999)
                    ds.FillRectangle(rect, Color.FromArgb((byte)Math.Clamp((1 - _settings.Exposure) * 70, 0, 120), 0, 0, 0));

                DrawGlare(ds, rect, radius, pane.Strength);
                DrawFresnel(ds, rect, radius, pane.Strength);
            }
        }
    }

    /// <summary>滚动时只画基底、边缘高光和菲涅尔，保留玻璃质感但不做模糊采样，避免拖影和整层消失。/summary>
    private void DrawGlassFallback(CanvasDrawingSession ds, CanvasControl sender, float width, float height)
    {
        foreach (var pane in _panes)
        {
            var rect = new Rect(pane.X, pane.Y, pane.Width, pane.Height);
            if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > width || rect.Top > height) continue;
            var radius = Math.Max(0, pane.Radius);
            using var geometry = BuildRoundedRect(sender, rect, radius);
            using var layer = ds.CreateLayer(1f, geometry);
            var tint = _settings.Tint;
            ds.FillRectangle(rect, Color.FromArgb((byte)Math.Clamp(tint.A * pane.Strength + 18, 0, 255), tint.R, tint.G, tint.B));
            ds.FillRectangle(rect, Color.FromArgb(14, 255, 255, 255));
            DrawGlare(ds, rect, radius, pane.Strength * 0.8);
            DrawFresnel(ds, rect, radius, pane.Strength * 0.8);
        }
    }

    private static void AddChannel(CompositeEffect composite, ICanvasImage source, CanvasBitmap map, float amount, int channel)
    {
        var displacement = new DisplacementMapEffect
        {
            Source = source,
            Displacement = map,
            XChannelSelect = EffectChannelSelect.Red,
            YChannelSelect = EffectChannelSelect.Green,
            Amount = amount
        };
        var matrix = new Matrix5x4
        {
            M11 = channel == 0 ? 1 : 0,
            M22 = channel == 1 ? 1 : 0,
            M33 = channel == 2 ? 1 : 0,
            M44 = 1,
            M51 = 0, M52 = 0, M53 = 0, M54 = 0
        };
        var isolate = new ColorMatrixEffect { Source = displacement, ColorMatrix = matrix };
        composite.Sources.Add(isolate);
    }

    private void DrawGlare(CanvasDrawingSession ds, Rect rect, double radius, double strength)
    {
        var amount = _settings.GlareFactor * strength;
        if (amount <= 0.01) return;
        var angle = _settings.GlareAngle * Math.PI / 180.0;
        var dx = (float)(Math.Cos(angle) * rect.Width * 0.5);
        var dy = (float)(Math.Sin(angle) * rect.Height * 0.5);
        var brush = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, 255, 255, 255) },
            new CanvasGradientStop { Position = (float)Math.Clamp(0.40 - _settings.GlareRange * 0.16, 0.05, 0.6), Color = Color.FromArgb((byte)Math.Clamp(amount * 34, 0, 90), 255, 255, 255) },
            new CanvasGradientStop { Position = (float)Math.Clamp(0.52 + _settings.GlareHardness * 0.12, 0.2, 0.85), Color = Color.FromArgb(0, 255, 255, 255) }
        })
        {
            StartPoint = new Vector2((float)(rect.X + rect.Width * 0.15), (float)(rect.Y + rect.Height * 0.1)),
            EndPoint = new Vector2((float)(rect.X + rect.Width * 0.15 + dx), (float)(rect.Y + rect.Height * 0.1 + dy))
        };
        ds.FillRoundedRectangle(rect, (float)radius, (float)radius, brush);
    }

    private void DrawFresnel(CanvasDrawingSession ds, Rect rect, double radius, double strength)
    {
        var amount = _settings.FresnelFactor * strength;
        if (amount <= 0.01) return;
        var angle = (_settings.GlareAngle + 200) * Math.PI / 180.0;
        var dx = (float)(Math.Cos(angle) * rect.Width * 0.5);
        var dy = (float)(Math.Sin(angle) * rect.Height * 0.5);
        var edge = (byte)Math.Clamp(amount * 82, 8, 130);
        var brush = new CanvasLinearGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(edge, _palette.GlassRim.R, _palette.GlassRim.G, _palette.GlassRim.B) },
            new CanvasGradientStop { Position = (float)Math.Clamp(_settings.FresnelRange, 0.08, 0.8), Color = Color.FromArgb((byte)(edge * 0.28), _palette.GlassRim.R, _palette.GlassRim.G, _palette.GlassRim.B) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb((byte)(edge * 0.55), _palette.GlassRim.R, _palette.GlassRim.G, _palette.GlassRim.B) }
        })
        {
            StartPoint = new Vector2((float)(rect.X + rect.Width * 0.5 - dx), (float)(rect.Y + rect.Height * 0.5 - dy)),
            EndPoint = new Vector2((float)(rect.X + rect.Width * 0.5 + dx), (float)(rect.Y + rect.Height * 0.5 + dy))
        };
        ds.DrawRoundedRectangle(rect, (float)radius, (float)radius, brush, (float)(0.9 + _settings.FresnelHardness * 0.8));
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

    private CanvasBitmap GetDisplacementMap(CanvasControl sender, GlassPane pane, double scale)
    {
        var w = Math.Max(8, (int)Math.Round(pane.Width * scale));
        var h = Math.Max(8, (int)Math.Round(pane.Height * scale));
        var key = w + "x" + h + "r" + (int)Math.Round(pane.Radius * scale) + "t" + _settings.RefThickness.ToString("0.00");
        if (_maps.TryGetValue(key, out var cached)) return cached;

        var pixels = new byte[w * h * 4];
        var radius = (float)(pane.Radius * scale);
        var thickness = (float)Math.Max(2.0, _settings.RefThickness * 16 * scale);
        var cx = w * 0.5f;
        var cy = h * 0.5f;
        var halfW = w * 0.5f;
        var halfH = h * 0.5f;

        float Sdf(float px, float py)
        {
            var qx = MathF.Abs(px - cx) - (halfW - radius);
            var qy = MathF.Abs(py - cy) - (halfH - radius);
            var ax = MathF.Max(qx, 0);
            var ay = MathF.Max(qy, 0);
            return MathF.Sqrt(ax * ax + ay * ay) + MathF.Min(MathF.Max(qx, qy), 0) - radius;
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d = Sdf(x + 0.5f, y + 0.5f);
                var influence = Math.Clamp(1f + d / thickness, 0f, 1f);
                influence = MathF.Pow(influence, 1.7f);
                var gx = Sdf(x + 2.5f, y + 0.5f) - Sdf(x - 1.5f, y + 0.5f);
                var gy = Sdf(x + 0.5f, y + 2.5f) - Sdf(x - 0.5f, y - 1.5f);
                var len = MathF.Sqrt(gx * gx + gy * gy);
                var nx = len > 0.0001f ? -gx / len : 0;
                var ny = len > 0.0001f ? -gy / len : 0;
                var i = (y * w + x) * 4;
                pixels[i] = 128;
                pixels[i + 1] = (byte)Math.Clamp(128 + ny * influence * 127f, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(128 + nx * influence * 127f, 0, 255);
                pixels[i + 3] = 255;
            }
        }

        var map = CanvasBitmap.CreateFromBytes(sender, pixels, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f);
        _maps[key] = map;
        return map;
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
        float pointerX, float pointerY, float pointerStrength, float musicPulse, float[] musicBands)
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
            DrawTerrain(ds, width, height, t, 8, 18, p, pointerX, pointerY, pointerStrength, musicPulse, musicBands);
            return;
        }
        DrawRidge(ds, width, horizon, height * 0.145f, t, p, 0.052f, 9.1f, 232);
        DrawTerrain(ds, width, height, t, 13, 28, p, pointerX, pointerY, pointerStrength, musicPulse, musicBands);

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
        float pointerX, float pointerY, float pointerStrength, float musicPulse, float[] musicBands)
    {
        var horizon = height * 0.58f;
        var drift = t * 0.016f;
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
            var centerX = width * 0.5f + MathF.Sin(t * 0.045f) * width * 0.012f;
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
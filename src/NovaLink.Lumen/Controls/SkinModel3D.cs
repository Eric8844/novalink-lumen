using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Services;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// 主页账号卡片里的 3D 我的世界人物。
/// 不引入任何 3D 库：把人物拆成 6 个长方体，每个面在皮肤 PNG 上取一块矩形，
/// 用正交投影把这块矩形映射成屏幕上的平行四边形（平行四边形映射恰好是仿射变换），
/// 再按面心深度从远到近绘制（画家算法），最后按面法线与光向叠一层明暗。
/// 所有面都是轴对齐的平面，所以旋转后依然是平行四边形，贴图不会歪。
/// </summary>
public sealed class SkinModel3D : Grid
{
    private readonly record struct UvRect(double U, double V, double W, double H);

    private readonly record struct UvSet(
        UvRect Front, UvRect Back, UvRect Left, UvRect Right, UvRect Top, UvRect Bottom);

    private readonly record struct Face(
        Vector3 TopLeft, Vector3 TopRight, Vector3 BottomLeft, Vector3 Normal, UvRect Uv);

    private readonly CanvasControl _canvas;
    private readonly DispatcherTimer _timer;
    private CanvasBitmap? _skin;
    private bool _slim;
    private bool _useDefault = true;
    private bool _defaultLoading;
    private bool _officialStevePending;
    private double _time;

    // 拖动旋转的状态：yaw 左右、pitch 上下（deg 换算成弧度直接累加）；松手 2.5 秒后恢复轻微摇头。
    private double _yaw = 0.60;
    private double _pitch = 0.10;
    private double _idle = 3.0;
    private bool _dragging;
    private Point _dragPoint;

    public SkinModel3D()
    {
        // 透明底也要能接指针：Background=null 的 Grid 不参与命中测试，拖动会完全没反应。
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        _canvas = new CanvasControl
        {
            ClearColor = Color.FromArgb(0, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            _time += 0.033;
            if (!_dragging) _idle += 0.033;
            if (IsLoaded) _canvas.Invalidate();
        };

        Loaded += (_, _) => { _timer.Start(); StartDefaultSkinLoad(); };
        Unloaded += (_, _) => _timer.Stop();

        try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll); }
        catch (Exception ex) { AppLog.Warn("skin", "设置拖动指针失败：" + ex.Message); }

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        PointerCanceled += (_, _) => _dragging = false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _idle = 0;
        _dragPoint = e.GetCurrentPoint(this).Position;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var now = e.GetCurrentPoint(this).Position;
        var dx = now.X - _dragPoint.X;
        var dy = now.Y - _dragPoint.Y;
        _dragPoint = now;
        _yaw += dx * 0.012;
        _pitch = Math.Clamp(_pitch + dy * 0.008, -0.60, 0.60);
        _idle = 0;
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _idle = 0;
        ReleasePointerCapture(e.Pointer);
    }

    /// <summary>Alex 细手臂模型。</summary>
    public bool Slim
    {
        get => _slim;
        set { _slim = value; _canvas.Invalidate(); }
    }

    /// <summary>换成内置的史蒂夫皮肤（没有账号、或皮肤下载失败时用）。</summary>
    public void UseDefaultSkin()
    {
        _useDefault = true;
        try
        {
            _skin?.Dispose();
            _skin = BuildDefaultSkin();
            _canvas.Invalidate();
        }
        catch (Exception ex)
        {
            AppLog.Warn("skin", "生成默认皮肤失败：" + ex.Message);
        }
        StartDefaultSkinLoad();
    }

    /// <summary>
    /// 内置的史蒂夫换成正版贴图（Assets\steve.png，来自 Mojang 官方 64×64 史蒂夫皮肤）。
    /// 先用程序化像素图顶上，贴图加载好再替换，离线或文件缺失时依然是可看的人。
    /// </summary>
    private void StartDefaultSkinLoad()
    {
        if (_defaultLoading) return;
        _defaultLoading = true;
        // 不能在构造函数/Loaded 里直接 LoadAsync：那时 CanvasControl 还没有 CanvasDevice，
        // 会抛"The control does not currently have a CanvasDevice associated with it"，
        // 官方史蒂夫贴图就永远加载不上（日志里能看到这条 Warn）。放到第一帧 OnDraw 里再加载。
        _officialStevePending = true;
        _canvas.Invalidate();
    }

    private async Task LoadOfficialSteveAsync()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "steve.png");
            if (!System.IO.File.Exists(path)) return;
            var bitmap = await CanvasBitmap.LoadAsync(_canvas, path);
            if (!_useDefault)
            {
                bitmap.Dispose();
                return;
            }
            _skin?.Dispose();
            _skin = bitmap;
            _canvas.Invalidate();
        }
        catch (Exception ex)
        {
            AppLog.Warn("skin", "加载官方史蒂夫皮肤失败，沿用内置绘制：" + ex.Message);
        }
    }

    /// <summary>加载一张皮肤 PNG；文件不存在或解析失败时退回默认皮肤。</summary>
    public async Task LoadSkinAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            UseDefaultSkin();
            return;
        }

        try
        {
            var bitmap = await CanvasBitmap.LoadAsync(_canvas, path);
            var width = bitmap.SizeInPixels.Width;
            if (width < 64 || width % 64 != 0)
            {
                bitmap.Dispose();
                UseDefaultSkin();
                return;
            }

            _skin?.Dispose();
            _skin = bitmap;
            _useDefault = false;
            _canvas.Invalidate();
        }
        catch (Exception ex)
        {
            AppLog.Warn("skin", "加载皮肤失败，改用默认皮肤：" + ex.Message);
            UseDefaultSkin();
        }
    }

    // ------------------------------------------------------------------ 绘制

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width < 8 || height < 8) return;

        if (_officialStevePending)
        {
            _officialStevePending = false;
            _ = LoadOfficialSteveAsync();
        }

        _skin ??= BuildDefaultSkin();
        var skin = _skin;
        if (skin is null) return;

        var faces = BuildModel(_slim ? 3f : 4f);

        // 轻微摇头 + 呼吸起伏：静止的卡片上看起来是活的，但不至于晃眼。
        // 用户拖动过就停手，松手 2.5 秒后再淡入回自动摇头，避免"刚摆好角度又被抢走"。
        var sway = _dragging ? 0.0 : Math.Clamp((_idle - 2.5) / 1.5, 0.0, 1.0);
        var yaw = _yaw + Math.Sin(_time * 0.42) * 0.28 * sway;
        var pitch = _pitch;
        var bob = Math.Sin(_time * 1.5) * 0.5;
        // 32 单位高的人物放在卡片右侧 96x158 的区域里，尽量画大一点才有存在感。
        var scale = Math.Min(width / 19.0, height / 35.0);
        var cx = (float)(width * 0.5);
        var cy = (float)(height * 0.5 - 3.0);

        var count = faces.Count;
        var corners = new Vector2[count * 3];
        var depths = new float[count];
        var order = new int[count];

        for (var i = 0; i < count; i++)
        {
            var face = faces[i];
            corners[i * 3] = Project(face.TopLeft + new Vector3(0, (float)bob, 0), yaw, pitch, scale, cx, cy);
            corners[i * 3 + 1] = Project(face.TopRight + new Vector3(0, (float)bob, 0), yaw, pitch, scale, cx, cy);
            corners[i * 3 + 2] = Project(face.BottomLeft + new Vector3(0, (float)bob, 0), yaw, pitch, scale, cx, cy);
            // 平行四边形中心 = (右上 + 左下) / 2，用它算深度最稳（拿三个角求和会偏）。
            depths[i] = Depth((face.TopRight + face.BottomLeft) * 0.5f, yaw, pitch);
            order[i] = i;
        }

        Array.Sort(order, (a, b) => depths[a].CompareTo(depths[b]));

        // 人物身后一团薄荷微光 + 脚下一圈投影：既把它从卡片底色里托出来，也不至于像悬浮。
        // 投影跟着脚的真实投影走，人物转身时才不会"影子留在原地"。
        var feet = Project(new Vector3(0, -16f, 0), yaw, pitch, scale, cx, cy);
        var glow = new Vector2(cx, cy);
        args.DrawingSession.FillEllipse(glow, (float)(9.5 * scale), (float)(15.0 * scale), Color.FromArgb(0x0E, 0x7C, 0xEB, 0xD6));
        args.DrawingSession.FillEllipse(new Vector2(feet.X, feet.Y + 1.2f * (float)scale), (float)(9.0 * scale), (float)(1.9 * scale), Color.FromArgb(0x26, 0, 0, 0));
        args.DrawingSession.FillEllipse(new Vector2(feet.X, feet.Y + 1.2f * (float)scale), (float)(5.5 * scale), (float)(1.1 * scale), Color.FromArgb(0x34, 0, 0, 0));

        var textureHeight = (float)skin.SizeInPixels.Height;
        foreach (var index in order)
        {
            var face = faces[index];
            var p0 = corners[index * 3];
            var p1 = corners[index * 3 + 1];
            var p3 = corners[index * 3 + 2];
            var p2 = p1 + p3 - p0;

            var area = (p1.X - p0.X) * (p3.Y - p0.Y) - (p3.X - p0.X) * (p1.Y - p0.Y);
            if (Math.Abs(area) < 0.05f) continue;

            var uv = face.Uv;
            if (uv.V + uv.H > textureHeight + 0.5)
            {
                // 64x32 的老皮肤没有部分顶/底面，用同一个面取一个像素平涂，避免开天窗。
                var probe = skin.GetPixelColors((int)uv.U, (int)Math.Min(uv.V, textureHeight - 1), 1, 1);
                var flat = probe.Length > 0 ? probe[0] : Color.FromArgb(255, 128, 128, 128);
                using var flatGeometry = CanvasGeometry.CreatePolygon(_canvas, new[] { p0, p1, p2, p3 });
                args.DrawingSession.FillGeometry(flatGeometry, Color.FromArgb(255, flat.R, flat.G, flat.B));
                continue;
            }

            args.DrawingSession.Transform = Affine(p0, p1, p3, uv);
            args.DrawingSession.DrawImage(
                skin,
                new Rect(uv.U, uv.V, uv.W, uv.H),
                new Rect(uv.U, uv.V, uv.W, uv.H),
                1f,
                // 皮肤是 64x64 的像素图，放大时要保持硬边，插值会让它变成一团糊。
                CanvasImageInterpolation.NearestNeighbor);
            args.DrawingSession.Transform = Matrix3x2.Identity;

            var shade = Shade(face.Normal, yaw, pitch);
            if (shade > 0.01f)
            {
                using var geometry = CanvasGeometry.CreatePolygon(_canvas, new[] { p0, p1, p2, p3 });
                args.DrawingSession.FillGeometry(geometry, Color.FromArgb((byte)(shade * 255f), 0, 0, 0));
            }
        }

        args.DrawingSession.Transform = Matrix3x2.Identity;
    }

    private static Vector2 Project(Vector3 point, double yaw, double pitch, double scale, float cx, float cy)
    {
        var cosYaw = Math.Cos(yaw);
        var sinYaw = Math.Sin(yaw);
        var rx = point.X * cosYaw + point.Z * sinYaw;
        var rz = -point.X * sinYaw + point.Z * cosYaw;
        var cosPitch = Math.Cos(pitch);
        var sinPitch = Math.Sin(pitch);
        var ry = point.Y * cosPitch - rz * sinPitch;
        return new Vector2((float)(cx + rx * scale), (float)(cy - ry * scale));
    }

    private static float Depth(Vector3 point, double yaw, double pitch)
    {
        var cosYaw = Math.Cos(yaw);
        var sinYaw = Math.Sin(yaw);
        var rz = -point.X * sinYaw + point.Z * cosYaw;
        var ry = point.Y * Math.Cos(pitch) - rz * Math.Sin(pitch);
        return (float)(rz * Math.Cos(pitch) + ry * Math.Sin(pitch));
    }

    /// <summary>把皮肤上的矩形映射到屏幕上的平行四边形 p0(左上)、p1(右上)、p3(左下)。</summary>
    private static Matrix3x2 Affine(Vector2 p0, Vector2 p1, Vector2 p3, UvRect uv)
    {
        var ex = (p1 - p0) / (float)uv.W;
        var ey = (p3 - p0) / (float)uv.H;
        var origin = p0 - ex * (float)uv.U - ey * (float)uv.V;
        return new Matrix3x2(ex.X, ex.Y, ey.X, ey.Y, origin.X, origin.Y);
    }

    private static float Shade(Vector3 normal, double yaw, double pitch)
    {
        var cosYaw = Math.Cos(yaw);
        var sinYaw = Math.Sin(yaw);
        var nx = normal.X * cosYaw + normal.Z * sinYaw;
        var nz = -normal.X * sinYaw + normal.Z * cosYaw;
        var ny = normal.Y * Math.Cos(pitch) - nz * Math.Sin(pitch);
        var rotated = Vector3.Normalize(new Vector3((float)nx, (float)ny, (float)nz));

        // 光从左上前方打过来：正面亮、侧面次之、背面与底面暗。
        var light = Vector3.Normalize(new Vector3(-0.42f, 0.78f, 0.46f));
        var lambert = Math.Clamp(Vector3.Dot(rotated, light), 0f, 1f);
        // 只做很轻的明暗过渡：皮肤本身颜色就够，压太狠会显脏、显透明。
        return (1f - (0.72f + 0.28f * lambert)) * 0.38f;
    }

    // ------------------------------------------------------------------ 模型

    private static List<Face> BuildModel(float armWidth)
    {
        // 人物高 32 单位：腿 -16..-4、身体 -4..8、头 8..16；z 正向朝观察者。
        var faces = new List<Face>(36);
        AddBox(faces, -4, 8, -4, 8, 8, 8, HeadUv);
        AddBox(faces, -4, -4, -2, 8, 12, 4, BodyUv);
        AddBox(faces, -4 - armWidth, -4, -2, armWidth, 12, 4, RightArmUv);
        AddBox(faces, 4, -4, -2, armWidth, 12, 4, LeftArmUv);
        AddBox(faces, -4, -16, -2, 4, 12, 4, RightLegUv);
        AddBox(faces, 0, -16, -2, 4, 12, 4, LeftLegUv);
        return faces;
    }

    private static void AddBox(List<Face> faces,
        float x, float y, float z, float sizeX, float sizeY, float sizeZ, UvSet uv)
    {
        var x0 = x; var x1 = x + sizeX;
        var y0 = y; var y1 = y + sizeY;
        var z0 = z; var z1 = z + sizeZ;

        // 正面（+z）：左上角在模型左上。
        faces.Add(new Face(new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x0, y0, z1), new Vector3(0, 0, 1), uv.Front));
        // 背面（-z）：从背后看，左右反过来。
        faces.Add(new Face(new Vector3(x1, y1, z0), new Vector3(x0, y1, z0), new Vector3(x1, y0, z0), new Vector3(0, 0, -1), uv.Back));
        // 右面（-x）：纹理左边缘贴着角色的正面。
        faces.Add(new Face(new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z1), new Vector3(-1, 0, 0), uv.Right));
        // 左面（+x）。
        faces.Add(new Face(new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z0), new Vector3(1, 0, 0), uv.Left));
        // 顶面（+y）：正面朝向观察者的下方。
        faces.Add(new Face(new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z1), new Vector3(0, 1, 0), uv.Top));
        // 底面（-y）。
        faces.Add(new Face(new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x0, y0, z0), new Vector3(0, -1, 0), uv.Bottom));
    }

    private static UvRect R(double u, double v, double w, double h) => new(u, v, w, h);

    private static readonly UvSet HeadUv = new(
        R(8, 8, 8, 8), R(24, 8, 8, 8), R(16, 8, 8, 8), R(0, 8, 8, 8), R(8, 0, 8, 8), R(16, 0, 8, 8));

    private static readonly UvSet BodyUv = new(
        R(20, 20, 8, 12), R(32, 20, 8, 12), R(28, 20, 4, 12), R(16, 20, 4, 12), R(20, 16, 8, 4), R(28, 16, 8, 4));

    private static readonly UvSet RightArmUv = new(
        R(44, 20, 4, 12), R(52, 20, 4, 12), R(48, 20, 4, 12), R(40, 20, 4, 12), R(44, 16, 4, 4), R(48, 16, 4, 4));

    private static readonly UvSet LeftArmUv = new(
        R(36, 52, 4, 12), R(44, 52, 4, 12), R(40, 52, 4, 12), R(32, 52, 4, 12), R(36, 48, 4, 4), R(40, 48, 4, 4));

    private static readonly UvSet RightLegUv = new(
        R(4, 20, 4, 12), R(12, 20, 4, 12), R(8, 20, 4, 12), R(0, 20, 4, 12), R(4, 16, 4, 4), R(8, 16, 4, 4));

    private static readonly UvSet LeftLegUv = new(
        R(20, 52, 4, 12), R(28, 52, 4, 12), R(24, 52, 4, 12), R(16, 52, 4, 12), R(20, 48, 4, 4), R(24, 48, 4, 4));

    // ------------------------------------------------------------------ 默认皮肤

    private static readonly Color Hair = Color.FromArgb(255, 0x3B, 0x2A, 0x1E);
    private static readonly Color HairDark = Color.FromArgb(255, 0x2E, 0x20, 0x16);
    private static readonly Color SkinTone = Color.FromArgb(255, 0xB9, 0x8A, 0x62);
    private static readonly Color SkinShade = Color.FromArgb(255, 0xA9, 0x78, 0x50);
    private static readonly Color Shirt = Color.FromArgb(255, 0x00, 0xA6, 0xA6);
    private static readonly Color ShirtDark = Color.FromArgb(255, 0x00, 0x8C, 0x8C);
    private static readonly Color Pants = Color.FromArgb(255, 0x3B, 0x3B, 0x8F);
    private static readonly Color PantsDark = Color.FromArgb(255, 0x33, 0x33, 0x7A);
    private static readonly Color Shoes = Color.FromArgb(255, 0x4A, 0x4A, 0x4A);
    private static readonly Color EyeWhite = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);
    private static readonly Color EyeBlue = Color.FromArgb(255, 0x3C, 0x5A, 0x9A);
    private static readonly Color Mouth = Color.FromArgb(255, 0x6E, 0x4A, 0x38);

    /// <summary>程序化画一个史蒂夫：没有网络、没有任何素材依赖。</summary>
    private CanvasBitmap BuildDefaultSkin()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];

        void SetPixel(int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            var i = (y * size + x) * 4;
            pixels[i] = c.B;
            pixels[i + 1] = c.G;
            pixels[i + 2] = c.R;
            pixels[i + 3] = c.A;
        }

        void Fill(int x, int y, int w, int h, Color c)
        {
            for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++)
                SetPixel(xx, yy, c);
        }

        // 头
        Fill(8, 0, 8, 8, Hair);          // 顶
        Fill(16, 0, 8, 8, SkinShade);    // 底
        Fill(0, 8, 8, 8, Hair);          // 右
        Fill(16, 8, 8, 8, Hair);         // 左
        Fill(24, 8, 8, 8, Hair);         // 后
        Fill(8, 8, 8, 8, SkinTone);      // 脸
        Fill(8, 8, 8, 3, Hair);          // 刘海
        SetPixel(9, 8, HairDark);
        SetPixel(14, 8, HairDark);
        // 眉毛、眼睛、嘴
        Fill(9, 11, 2, 1, HairDark);
        Fill(13, 11, 2, 1, HairDark);
        SetPixel(9, 12, EyeWhite);
        SetPixel(10, 12, EyeBlue);
        SetPixel(13, 12, EyeWhite);
        SetPixel(14, 12, EyeBlue);
        Fill(10, 14, 4, 1, Mouth);
        Fill(10, 15, 4, 1, SkinShade);

        // 身体
        Fill(20, 20, 8, 12, Shirt);
        Fill(32, 20, 8, 12, Shirt);
        Fill(16, 20, 4, 12, ShirtDark);
        Fill(28, 20, 4, 12, ShirtDark);
        Fill(20, 16, 8, 4, Shirt);
        Fill(28, 16, 8, 4, ShirtDark);

        // 手臂（袖子在上、手在下）
        foreach (var (u, v) in new[] { (44, 20), (36, 52) })
        {
            Fill(u, v, 4, 12, SkinTone);
            Fill(u, v, 4, 4, Shirt);
            Fill(u - 4, v, 4, 12, SkinTone);
            Fill(u - 4, v, 4, 4, ShirtDark);
            Fill(u + 4, v, 4, 12, SkinTone);
            Fill(u + 8, v, 4, 12, SkinTone);
            Fill(u, v - 4, 4, 4, Shirt);
            Fill(u + 4, v - 4, 4, 4, SkinShade);
        }

        // 腿（裤子 + 鞋）
        foreach (var (u, v) in new[] { (4, 20), (20, 52) })
        {
            Fill(u, v, 4, 12, Pants);
            Fill(u, v + 10, 4, 2, Shoes);
            Fill(u + 8, v, 4, 12, Pants);
            Fill(u + 8, v + 10, 4, 2, Shoes);
            Fill(u + 4, v, 4, 12, PantsDark);
            Fill(u + 4, v + 10, 4, 2, Shoes);
            Fill(u - 4, v, 4, 12, PantsDark);
            Fill(u - 4, v + 10, 4, 2, Shoes);
            Fill(u, v - 4, 4, 4, PantsDark);
            Fill(u + 4, v - 4, 4, 4, Shoes);
        }

        return CanvasBitmap.CreateFromBytes(
            _canvas, pixels, size, size,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f, CanvasAlphaMode.Premultiplied);
    }
}

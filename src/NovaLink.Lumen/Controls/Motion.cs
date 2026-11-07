using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NovaLink.Lumen.Services;
using Windows.Foundation;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// Shared, low-cost motion language. XAML handles layout; composition handles paint-only
/// scale/rotation; a TranslateTransform handles the 1-3px lift so StackPanel layout never moves.
/// </summary>
public static class Motion
{
    // One motion vocabulary for the whole shell. Anything that animates should pick a duration
    // from here instead of inventing 190/240/320/520-style one-offs, so hover, press, reveal and
    // row entrances all read as the same design language.
    public const int PressMs = 90;
    public const int HoverInMs = 170;
    public const int HoverOutMs = 230;
    public const int RevealMs = 340;
    public const int RiseMs = 460;
    public const int HeightMs = 520;
    public const int StaggerMs = 32;

    /// <summary>按钮悬停时的轻微放大（参考 Lens 站台：按钮悬停 +0.4%）。</summary>
    public const float ButtonHoverScale = 1.004f;

    /// <summary>按钮按下时的收缩比例（参考 Lens 站台 .lg-button:active{transform:scale(.97)}）。</summary>
    public const float ButtonPressScale = 0.975f;

    private static readonly ConditionalWeakTable<FrameworkElement, HoverState> HoverStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> ButtonStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> RotateStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> ButtonScaleStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> CenterStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Pose> ScaleStates = new();

    /// <summary>
    /// 记录一个元素当前的“渲染姿态”（合成器 Scale + Translation）。
    /// 不只为做动画：背景里的液态玻璃透镜（GlassPanes）是按卡片的布局矩形绘制的，
    /// 卡片一旦在合成器上被缩放/上浮，透镜还停在原位，就会看到“同一张卡有两层、按压时不同步”。
    /// 有了这个姿态，透镜每帧都能取到卡片的真实大小与位置。
    /// </summary>
    private sealed class Pose
    {
        /// <summary>已到达（或正在趋向）的缩放值，供连续打断时当起点。</summary>
        public float Target = 1f;

        public float ScaleFrom = 1f, ScaleTo = 1f;
        public int ScaleMs = 1;
        public long ScaleStart;
        public bool ScaleAnimating;

        public double LiftFrom, LiftTo;
        public int LiftMs = 1;
        public long LiftStart;
        public bool LiftAnimating;

        public float ScaleAt(long now)
        {
            if (!ScaleAnimating) return Target;
            var t = Progress(now, ScaleStart, ScaleMs);
            return (float)(ScaleFrom + (ScaleTo - ScaleFrom) * Ease(t));
        }

        public double LiftAt(long now)
        {
            if (!LiftAnimating) return LiftTo;
            var t = Progress(now, LiftStart, LiftMs);
            return LiftFrom + (LiftTo - LiftFrom) * Ease(t);
        }

        private static double Progress(long now, long start, int ms)
        {
            if (ms <= 0) return 1d;
            var t = (now - start) / (double)ms;
            return t <= 0 ? 0d : t >= 1 ? 1d : t;
        }
    }

    /// <summary>
    /// 读一个元素当前的合成器姿态。GlassPanes 用它把背景透镜对齐到“正在缩放/上浮”的卡片上。
    /// 返回 false 表示这个元素没被 Motion 驱动过（静止在原位）。
    /// </summary>
    public static bool TryGetPose(FrameworkElement element, out float scale, out double liftY)
    {
        scale = 1f;
        liftY = 0;
        if (!ScaleStates.TryGetValue(element, out var pose)) return false;
        var now = Environment.TickCount64;
        scale = pose.ScaleAt(now);
        liftY = pose.LiftAt(now);
        return true;
    }

    /// <summary>与 EaseOut() 的 cubic-bezier(0.22, 1, 0.36, 1) 一致，供姿态采样用。</summary>
    private static double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // 求解 x(u)=t 后取 y(u)。20 次二分对 1px 级别的误差完全够用，且只在采样时调用。
        double lo = 0, hi = 1, u = t;
        for (var i = 0; i < 20; i++)
        {
            u = (lo + hi) / 2;
            var x = 3 * (1 - u) * (1 - u) * u * 0.22 + 3 * (1 - u) * u * u * 0.36 + u * u * u;
            if (x < t) lo = u; else hi = u;
        }
        return 3 * (1 - u) * (1 - u) * u * 1.0 + 3 * (1 - u) * u * u * 1.0 + u * u * u;
    }

    private sealed class HoverState
    {
        public bool Over { get; set; }
    }

    public static void Hover(FrameworkElement element, float scale = 1.006f, float lift = 1.6f)
    {
        if (HoverStates.TryGetValue(element, out _)) return;
        var state = new HoverState();
        HoverStates.Add(element, state);
        Center(element);

        // 卡片的悬停/按压只用合成层位移（Translation）表达，不再改 Scale。
        // 一旦缩放，卡片自身描边会内缩几个像素，而背景玻璃透镜是另一层，永远慢半拍，
        // 按下时就会同时看到“原位置的旧轮廓 + 缩小后的新描边”——也就是用户说的分层。
        // 位移不会改变元素的采样矩形，所以无论透镜有没有跟上，都不可能多出一圈边。
        element.PointerEntered += (_, _) =>
        {
            state.Over = true;
            Lift(element, -lift, HoverInMs);
        };
        element.PointerExited += (_, _) =>
        {
            state.Over = false;
            Lift(element, 0, HoverOutMs);
        };
        element.PointerPressed += (_, _) =>
        {
            Lift(element, 1.2, PressMs);
        };
        element.PointerReleased += (_, _) =>
        {
            Lift(element, state.Over ? -lift : 0, HoverInMs);
        };
        element.PointerCaptureLost += (_, _) =>
        {
            state.Over = false;
            Lift(element, 0, HoverOutMs);
        };

        // 按钮额外叠加「悬停微放大 / 按下收缩」，位移量不变，只是让点击有实感。
        // 卡片保持只上浮：给玻璃卡片加缩放会让描边内缩，和背景透镜错位分层。
        if (element is ButtonBase) AttachButtonScale(element);
    }

    /// <summary>
    /// 按钮专用：悬停 1.004、按下 0.975，时长取 HoverInMs / PressMs。
    /// 只作用于 ButtonBase（按钮、开关式按钮），卡片与整页容器不参与。
    /// </summary>
    public static void AttachButtonScale(FrameworkElement element)
    {
        if (ButtonScaleStates.TryGetValue(element, out _)) return;
        ButtonScaleStates.Add(element, new object());

        var over = false;
        element.PointerEntered += (_, _) =>
        {
            over = true;
            AnimateScale(element, ButtonHoverScale, HoverInMs);
        };
        element.PointerExited += (_, _) =>
        {
            over = false;
            AnimateScale(element, 1f, HoverOutMs);
        };
        element.PointerPressed += (_, _) => AnimateScale(element, ButtonPressScale, PressMs);
        element.PointerReleased += (_, _) => AnimateScale(element, over ? ButtonHoverScale : 1f, HoverInMs);
        element.PointerCaptureLost += (_, _) =>
        {
            over = false;
            AnimateScale(element, 1f, HoverOutMs);
        };
    }

    /// <summary>Fade + rise + a whisper of scale for a dialog or a swapped settings panel.</summary>
    public static void Enter(FrameworkElement element, int delayMs = 0, float fromScale = 0.992f, double fromY = 10)
    {
        element.Opacity = 0;

        // The rise runs on the composition Translation channel, not on the TranslateTransform that
        // Hover() drives: sharing one transform made an entrance still in flight fight the hover
        // lift (two storyboards writing the same Y), which is the "twitching" seen when the cursor
        // reached a row while it was still rising in.
        var visual = ElementCompositionPreview.GetElementVisual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        // 没有强类型的 Visual.Translation（本 SDK 的 Visual 不暴露该属性），初始化值走属性表。
        visual.Properties.InsertVector3("Translation", new Vector3(0f, (float)fromY, 0f));
        var rise = visual.Compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(1f, Vector3.Zero, EaseOut(visual.Compositor));
        rise.Duration = TimeSpan.FromMilliseconds(RiseMs);
        if (delayMs > 0)
        {
            rise.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        visual.StartAnimation("Translation", rise);

        var story = new Storyboard();
        AddFade(story, element, 0, 1, RevealMs, delayMs);
        story.Begin();

        if (Math.Abs(fromScale - 1f) > 0.0005f)
        {
            var scaleVisual = ElementCompositionPreview.GetElementVisual(element);
            Center(element);
            scaleVisual.Scale = new Vector3(fromScale, fromScale, 1f);
            ScaleStates.GetOrCreateValue(element).Target = 1f;
            var anim = scaleVisual.Compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, Vector3.One, EaseOut(scaleVisual.Compositor));
            anim.Duration = TimeSpan.FromMilliseconds(HeightMs);
            anim.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            anim.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            scaleVisual.StartAnimation("Scale", anim);
        }
    }

    /// <summary>Staggered page entrance: the first 8 direct sections rise in sequence.</summary>
    public static void EnterPage(FrameworkElement page)
    {
        var root = page is Page p && p.Content is FrameworkElement content ? content : page;
        var host = root is ScrollViewer scroll && scroll.Content is FrameworkElement inner ? inner : root;
        if (host is Panel panel && panel.Children.Count > 1)
        {
            var index = 0;
            foreach (var child in panel.Children)
            {
                if (child is not FrameworkElement element || element.Visibility != Visibility.Visible) continue;
                if (index >= 8) break;
                Enter(element, index * StaggerMs, 0.996f, 14);
                index++;
            }
            return;
        }
        Enter(host, 0, 0.996f, 14);
    }

    /// <summary>
    /// Entrance for one row inside a batch-rendered list: a short fade + rise whose delay is
    /// capped so a 116-row list still finishes settling almost immediately.
    /// </summary>
    public static void RowIn(FrameworkElement element, int indexInBatch, double fromY = 9)
        => Enter(element, Math.Min(indexInBatch, 8) * StaggerMs, 1f, fromY);

    /// <summary>One-shot springy scale for the selected navigation pill or a completed action.</summary>
    public static void Bump(FrameworkElement element, float scale = 1.045f, int durationMs = 320)
    {
        var state = ScaleStates.GetOrCreateValue(element);
        var rest = state.Target;
        if (Math.Abs(scale - rest) < 0.0004f) return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        Center(element);
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, new Vector3(rest, rest, 1f));
        anim.InsertKeyFrame(0.42f, new Vector3(scale, scale, 1f), EaseOut(visual.Compositor));
        anim.InsertKeyFrame(1f, new Vector3(rest, rest, 1f), EaseOut(visual.Compositor));
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Scale", anim);
    }

    /// <summary>Slow ambient drift for decorative orbs, animated on the compositor so 4K stays cheap.</summary>
    public static void Float(FrameworkElement element, double amplitude = 6, double seconds = 6, int delayMs = 0)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, Vector3.Zero);
        anim.InsertKeyFrame(0.5f, new Vector3(0f, (float)-amplitude, 0f),
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        anim.InsertKeyFrame(1f, Vector3.Zero,
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        anim.Duration = TimeSpan.FromSeconds(Math.Max(2.4, seconds));
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        if (delayMs > 0)
        {
            anim.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            anim.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        visual.StartAnimation("Translation", anim);
    }

    /// <summary>Soft breathing opacity for status dots, compositor-only.</summary>
    public static void Pulse(FrameworkElement element, double min = 0.55, double max = 1, int durationMs = 2800)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, (float)min);
        anim.InsertKeyFrame(0.5f, (float)max);
        anim.InsertKeyFrame(1f, (float)min);
        anim.Duration = TimeSpan.FromMilliseconds(Math.Max(400, durationMs));
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", anim);
    }

    /// <summary>Brand-mark reaction: a small tilt with a matching scale.</summary>
    public static void RotateOnHover(FrameworkElement element, float degrees = 4f, float scale = 1.04f)
    {
        if (RotateStates.TryGetValue(element, out _)) return;
        RotateStates.Add(element, new object());
        Center(element);
        element.PointerEntered += (_, _) => AnimateRotation(element, -degrees, scale, HoverInMs + 90);
        element.PointerExited += (_, _) => AnimateRotation(element, 0f, 1f, HoverOutMs + 70);
    }

    public static void AttachButtons(DependencyObject root)
    {
        if (root is Button button && button.Tag is not "no-motion")
        {
            if (!ButtonStates.TryGetValue(button, out _))
            {
                ButtonStates.Add(button, new object());
                var accent = Application.Current?.Resources.TryGetValue("AccentButton", out var style) == true
                             && ReferenceEquals(button.Style, style);
                Hover(button, accent ? 1.018f : 1.006f, accent ? 1.8f : 1.2f);
                AttachButtonScale(button);
            }
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            AttachButtons(VisualTreeHelper.GetChild(root, i));
    }

    /// <summary>
    /// Scale with a tracked start value. Composition keyframe animations normally start from the
    /// *base* value (1.0), so a second animation that interrupts a running one snaps to 1.0 first —
    /// that snap is what reads as the card "twitching" when click (press) lands on top of hover.
    /// Keeping our own target and feeding it as the 0% keyframe makes press/release/hover chain
    /// continuously instead of restarting from scratch.
    /// </summary>
    private static void AnimateScale(FrameworkElement element, float scale, int durationMs)
    {
        var state = ScaleStates.GetOrCreateValue(element);
        var now = Environment.TickCount64;
        // 起点取“此刻屏幕上真实的值”，打断时不会先从旧目标跳一下。
        var from = state.ScaleAt(now);
        if (Math.Abs(from - scale) < 0.0004f && Math.Abs(state.Target - scale) < 0.0004f) return;

        state.Target = scale;
        state.ScaleFrom = from;
        state.ScaleTo = scale;
        state.ScaleMs = Math.Max(1, durationMs);
        state.ScaleStart = now;
        state.ScaleAnimating = true;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, new Vector3(from, from, 1f));
        anim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), EaseOut(visual.Compositor));
        anim.Duration = TimeSpan.FromMilliseconds(state.ScaleMs);
        visual.StartAnimation("Scale", anim);
    }

    private static void AnimateRotation(FrameworkElement element, float degrees, float scale, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
        rotation.InsertKeyFrame(1f, degrees, EaseOut(visual.Compositor));
        rotation.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("RotationAngleInDegrees", rotation);

        var scaleAnim = visual.Compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), EaseOut(visual.Compositor));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Scale", scaleAnim);
    }

    /// <summary>
    /// Hover/press 的上浮走合成器 Translation，而不是 XAML RenderTransform + 依赖动画。
    /// 之前用 Storyboard 写 TranslateTransform.Y（EnableDependentAnimation=true），会和同一元素上的
    /// 合成器 Scale 打架：元素被临时重新栅格化，玻璃底与描边各自偏移 1~2px，看起来就是卡片"分层"。
    /// 现在两者都在同一个视觉上，只有一份栅格化结果，按压时不会再多出一圈轮廓。
    /// </summary>
    private static void Lift(FrameworkElement element, double y, int durationMs)
    {
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var state = ScaleStates.GetOrCreateValue(element);
            var now = Environment.TickCount64;
            state.LiftFrom = state.LiftAt(now);
            state.LiftTo = y;
            state.LiftMs = Math.Max(1, durationMs);
            state.LiftStart = now;
            state.LiftAnimating = true;

            var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(0f, new Vector3(0f, (float)state.LiftFrom, 0f));
            anim.InsertKeyFrame(1f, new Vector3(0f, (float)y, 0f), EaseOut(visual.Compositor));
            anim.Duration = TimeSpan.FromMilliseconds(state.LiftMs);
            visual.StartAnimation("Translation", anim);
        }
        catch (Exception ex)
        {
            AppLog.Warn("motion", "上浮动画失败：" + ex.Message);
        }
    }

    private static void AddFade(Storyboard story, DependencyObject target, double from, double to, int durationMs, int delayMs)
    {
        var fade = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
    }

    private static void Center(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        void Apply() => visual.CenterPoint = new Vector3((float)element.ActualWidth / 2f, (float)element.ActualHeight / 2f, 0f);
        Apply();
        if (CenterStates.TryGetValue(element, out _)) return;
        CenterStates.Add(element, new object());
        element.SizeChanged += (_, _) => Apply();
    }

    private static CompositionEasingFunction EaseOut(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
}

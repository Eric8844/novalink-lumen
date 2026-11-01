using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

    private static readonly ConditionalWeakTable<FrameworkElement, HoverState> HoverStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> ButtonStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> RotateStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> CenterStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, ScaleState> ScaleStates = new();

    /// <summary>Tracked scale target so an interrupting animation never snaps back to 1f first.</summary>
    private sealed class ScaleState
    {
        public float Target = 1f;
    }

    private sealed class HoverState
    {
        public required TranslateTransform Translate { get; init; }
        public bool Over { get; set; }
        public Storyboard? Move { get; set; }
    }

    public static void Hover(FrameworkElement element, float scale = 1.006f, float lift = 1.6f)
    {
        if (HoverStates.TryGetValue(element, out _)) return;
        var state = new HoverState { Translate = EnsureTranslate(element) };
        HoverStates.Add(element, state);
        Center(element);

        element.PointerEntered += (_, _) =>
        {
            state.Over = true;
            AnimateScale(element, scale, HoverInMs);
            MoveY(state, -lift, HoverInMs, 0);
        };
        element.PointerExited += (_, _) =>
        {
            state.Over = false;
            AnimateScale(element, 1f, HoverOutMs);
            MoveY(state, 0, HoverOutMs, 0);
        };
        element.PointerPressed += (_, _) =>
        {
            AnimateScale(element, 0.986f, PressMs);
            MoveY(state, 0, PressMs, 0);
        };
        element.PointerReleased += (_, _) =>
        {
            AnimateScale(element, state.Over ? scale : 1f, HoverInMs);
            MoveY(state, state.Over ? -lift : 0, HoverInMs, 0);
        };
        element.PointerCaptureLost += (_, _) =>
        {
            state.Over = false;
            AnimateScale(element, 1f, HoverOutMs);
            MoveY(state, 0, HoverOutMs, 0);
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
        var from = state.Target;
        if (Math.Abs(from - scale) < 0.0004f) return;
        state.Target = scale;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, new Vector3(from, from, 1f));
        anim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), EaseOut(visual.Compositor));
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
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

    private static void MoveY(HoverState state, double y, int durationMs, int delayMs)
    {
        state.Move?.Stop();
        var story = new Storyboard();
        AddY(story, state.Translate, null, y, durationMs, delayMs, new CubicEase { EasingMode = EasingMode.EaseOut });
        story.Begin();
        state.Move = story;
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

    private static void AddY(Storyboard story, TranslateTransform target, double? from, double to, int durationMs, int delayMs, EasingFunctionBase ease)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EnableDependentAnimation = true,
            EasingFunction = ease
        };
        if (from.HasValue) anim.From = from.Value;
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Y");
        story.Children.Add(anim);
    }

    private static TranslateTransform EnsureTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group)
        {
            var existing = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (existing is not null) return existing;
            var added = new TranslateTransform();
            group.Children.Add(added);
            return added;
        }
        if (element.RenderTransform is TranslateTransform translate) return translate;

        var created = new TranslateTransform();
        element.RenderTransform = created;
        return created;
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

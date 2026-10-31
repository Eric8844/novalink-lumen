using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Views;
using Windows.Foundation;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// 把页面里的液态玻璃卡片注册给背景渲染层。
/// 渲染层每帧回调读取卡片当前位置，滚动时玻璃与卡片严格同步，不会出现分层。
/// </summary>
public static class GlassPanes
{
    private static readonly ConditionalWeakTable<FrameworkElement, Tracker> Trackers = new();
    private static Tracker? _active;

    public static void Track(FrameworkElement page)
    {
        if (Trackers.TryGetValue(page, out _)) return;
        var tracker = new Tracker(page);
        Trackers.Add(page, tracker);
        _active = tracker;
        if (ShellPage.Current?.GlassBackdrop is { } backdrop) backdrop.PaneProvider = tracker.BuildPanes;
    }

    private sealed class Tracker
    {
        private readonly FrameworkElement _page;
        private readonly List<Border> _cards = new();
        private bool _scanned;

        public Tracker(FrameworkElement page)
        {
            _page = page;
            page.Loaded += (_, _) => Scan();
            page.Unloaded += (_, _) =>
            {
                if (!ReferenceEquals(_active, this)) return;
                if (ShellPage.Current?.GlassBackdrop is { } backdrop)
                {
                    backdrop.PaneProvider = null;
                    backdrop.ClearPanes();
                }
                _active = null;
            };
            _page.DispatcherQueue.TryEnqueue(Scan);
        }

        private void Scan()
        {
            if (_scanned || _page.ActualWidth < 10) return;
            _scanned = true;
            var style = Application.Current.Resources.TryGetValue("GlassCard", out var value) ? value as Style : null;
            _cards.Clear();
            var viewport = Math.Max(1, _page.ActualWidth * _page.ActualHeight);
            foreach (var border in Find<Border>(_page))
            {
                if (style is not null && !ReferenceEquals(border.Style, style)) continue;
                _cards.Add(border);

                // 只有真正的小卡片才跟随鼠标轻微放大。
                // 列表、设置这类整块大容器一旦缩放，用户会觉得“点空白处也在抖”。
                var area = border.ActualWidth * border.ActualHeight;
                if (border.Tag is "no-motion") continue;
                if (area > viewport * 0.22 || border.ActualHeight > 380) continue;
                Motion.Hover(border, 1.004f, 1.4f);
            }
            _cards.Sort((a, b) => (b.ActualWidth * b.ActualHeight).CompareTo(a.ActualWidth * a.ActualHeight));
            if (_cards.Count > 7) _cards.RemoveRange(7, _cards.Count - 7);
        }

        /// <summary>每帧由 Win2D 背景调用，坐标实时取自 XAML 视觉树。</summary>
        public IEnumerable<GlassPane> BuildPanes()
        {
            var backdrop = ShellPage.Current?.GlassBackdrop;
            if (backdrop is null || _page.ActualWidth < 10) yield break;
            var viewport = backdrop.ActualWidth * backdrop.ActualHeight;
            for (var i = 0; i < _cards.Count; i++)
            {
                var card = _cards[i];
                if (card.Visibility != Visibility.Visible || card.Opacity < 0.55 || card.ActualWidth < 60 || card.ActualHeight < 40) continue;
                // 大块容器不做整片折射，否则会像一层厚玻璃盖住背景；只给真正的卡片加透镜。
                if (viewport > 0 && card.ActualWidth * card.ActualHeight > viewport * 0.30) continue;
                Point origin;
                try { origin = card.TransformToVisual(backdrop).TransformPoint(new Point(0, 0)); }
                catch { continue; }
                var radius = card.CornerRadius.TopLeft;
                if (radius <= 0) radius = 24;
                yield return new GlassPane(origin.X, origin.Y, card.ActualWidth, card.ActualHeight, radius, i < 4 ? 1.0 : 0.86);
            }
        }

        private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var nested in Find<T>(child)) yield return nested;
            }
        }
    }
}
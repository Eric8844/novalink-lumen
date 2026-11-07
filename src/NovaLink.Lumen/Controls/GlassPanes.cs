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
    /// <summary>透镜上限：共享一次模糊之后每块玻璃的边际成本很小，同屏 28 块仍然只算一张模糊。</summary>
    private const int MaxPanes = 28;

    /// <summary>
    /// 大容器阈值（占视口面积）。玻璃改成整屏一次模糊、按矩形取样后，大面板的成本和一张卡几乎一样，
    /// 所以不再像位移贴图时代那样把大面板排除在外，只挡掉「整屏铺满」的容器，免得整页糊成一片。
    /// </summary>
    private const double BigPaneRatio = 0.55;

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

    /// <summary>
    /// 列表重建后立刻重扫一次。分批渲染的行卡是新加进视觉树的，等下一次定时重扫最多要 800ms，
    /// 这段时间里行卡只有半透明底色、没有透镜，看起来就是「先透明玻璃、过一会儿才叠上液态玻璃」。
    /// </summary>
    public static void Rescan(FrameworkElement? page)
    {
        if (page is null) return;
        if (Trackers.TryGetValue(page, out var tracker)) tracker.Scan();
        ShellPage.Current?.GlassBackdrop?.RefreshPanes();
    }

    private sealed class Tracker
    {
        private readonly FrameworkElement _page;
        private readonly List<Border> _cards = new();
        // 会随悬停/按压做合成器缩放的卡片：背景层永远慢半拍，只画不依赖位移的玻璃。
        private readonly HashSet<Border> _animated = new();
        private int _lastPosed = -1;
        private long _lastDebugLog;
        private long _lastScan;
        // 扫描签名：只有数量组合变化时才写日志，否则每条扫描都写会把日志刷满。
        private string _lastScanSignature = "";

        /// <summary>
        /// 诊断日志（扫描数量、姿态命中、卡片清单）默认关闭：透镜扫描在滚动时每秒会变两三次数，
        /// 姿态命中在鼠标悬停时来回翻，开着会把 lumen-*.log 刷到几百 KB。排查时设
        /// NOVALINK_DEBUG_MOTION=1 再启动即可全部恢复。
        /// </summary>
        private static readonly bool DebugLog =
            string.Equals(Environment.GetEnvironmentVariable("NOVALINK_DEBUG_MOTION"), "1", StringComparison.Ordinal);
        // 悬停高光：鼠标停在哪张卡上，背景层就把那张卡的边缘高光提亮（不再用跟手光斑覆盖层）。
        private readonly ConditionalWeakTable<Border, HoverState> _hover = new();

        private sealed class HoverState
        {
            public bool Over;
        }

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

        /// <summary>
        /// 重新扫描页面里的卡片。列表是分批重建的（首页刷新、库页切页签、发现换筛选都会整块重来），
        /// 只在第一次扫描会把旧卡片留在缓存里：旧卡片已经离开视觉树，透镜却还在原位置绘制，
        /// 看起来就像“同一张卡有两层”。所以按固定间隔重扫，代价是 2~3 次/秒的视觉树遍历。
        /// 扫描时只保留与视口相交（含半屏预取边距）的卡片，按面积降序最多 28 块：
        /// 库页、发现页一次能生成几百个行卡，全画透镜没必要，全缓存又会让每帧多算几百次变换。
        /// </summary>
        internal void Scan()
        {
            if (_page.ActualWidth < 10) return;
            _lastScan = Environment.TickCount64;
            var cardStyle = GlassStyle("GlassCard");
            var rowStyle = GlassStyle("GlassRow");
            _cards.Clear();
            _animated.Clear();

            // 视口按背景层算（它是全窗口尺寸）；拿不到时退回页面本身。
            var view = ViewportRoot();
            var viewWidth = Math.Max(1, view.ActualWidth);
            var viewHeight = Math.Max(1, view.ActualHeight);
            var viewport = viewWidth * viewHeight;
            // 一半视口的预取边距：滚动发生时卡片在 350ms 内就会进入画面，先收进来免得“先露白再补玻璃”。
            var marginX = viewWidth * 0.5;
            var marginY = viewHeight * 0.5;

            var cardCount = 0;
            var rowCount = 0;
            var nested = 0;
            foreach (var border in Find<Border>(_page))
            {
                var isCard = cardStyle is not null && ReferenceEquals(border.Style, cardStyle);
                var isRow = rowStyle is not null && ReferenceEquals(border.Style, rowStyle);
                if (!isCard && !isRow) continue;
                if (!border.IsLoaded) continue;
                // 折叠容器里的卡片（发现页切换 内容/启动器插件、插件商店的加载骨架、各页的隐藏空状态）
                // 自己仍然是 Visibility.Visible、ActualWidth/Height 也还在，只有祖先 Collapsed。
                // 不过滤的话它们会被一直当成可见卡片，在背景层原位画出一块块没有内容的「幽灵玻璃」，
                // 看起来就是「这里多出一张空卡、还和真卡片叠在一起」。
                if (!IsEffectivelyVisible(border)) continue;
                // 玻璃卡里再套一层玻璃卡（插件商店、设置项这类）时，内层既不该画第二块透镜，
                // 也不该单独接悬停/按压缩放：内外两层各按自己的中心缩，按压时就会出现“两层边不同步”。
                // 但行卡是例外：库页的行卡整片放在一个 GlassCard 容器里，这种「行卡嵌在大卡里」必须照画，
                // 否则库页永远没有玻璃。只有行卡套行卡、卡片套卡片才继续算嵌套。
                if (isRow ? HasGlassAncestor(border, rowStyle) : HasGlassAncestor(border, cardStyle, rowStyle)) { nested++; continue; }
                // 一屏之外（滚动内容里排到下面的行卡）不参与透镜：它们的位置每帧都要换算，
                // 全部留下会让 60fps 下多出上百次 TransformToVisual。
                if (!InView(border, view, viewWidth, viewHeight, marginX, marginY)) continue;
                // 再按所在列表的滚动视口过滤一次：库页 76 个行卡里绝大多数已经滚出列表视口，
                // 它们虽然还在页面里，却会挤占 28 块透镜的名额，让真正看得见的行卡反而没玻璃。
                if (!InScrollViewport(border, view)) continue;

                if (isRow) rowCount++; else cardCount++;
                _cards.Add(border);
                EnsureHover(border);

                // 只有真正的小卡片才跟随鼠标轻微放大。
                // 列表、设置这类整块大容器一旦缩放，用户会觉得“点空白处也在抖”。
                var area = border.ActualWidth * border.ActualHeight;
                if (border.Tag is "no-motion") continue;
                if (area > viewport * 0.22 || border.ActualHeight > 380) continue;
                Motion.Hover(border, 1.004f, 1.4f);
                _animated.Add(border);
            }
            _cards.Sort((a, b) => (b.ActualWidth * b.ActualHeight).CompareTo(a.ActualWidth * a.ActualHeight));
            var matched = _cards.Count;
            if (_cards.Count > MaxPanes) _cards.RemoveRange(MaxPanes, _cards.Count - MaxPanes);


            // 数量组合变化时才写一条：卡片数 / 行卡数 / 上限 / 大容器阈值，方便真机核对“全覆盖”有没有生效。
            var signature = cardCount + "/" + rowCount + "/" + matched + "/" + _cards.Count + "/" + nested;
            if (DebugLog && !string.Equals(signature, _lastScanSignature, StringComparison.Ordinal))
            {
                _lastScanSignature = signature;
                NovaLink.Lumen.Services.AppLog.Info("glass",
                    "透镜扫描：卡 " + cardCount + "，行卡 " + rowCount + "，视口相交 " + matched +
                    "，保留 " + _cards.Count + "，上限 " + MaxPanes + "，大容器阈值 " + (int)(BigPaneRatio * 100) + "%" +
                    "");
                if (nested > 0)
                    NovaLink.Lumen.Services.AppLog.Info("glass", "跳过嵌套玻璃卡：" + nested + " 个（只给最外层画透镜与缩放）");
            }
        }

        private void EnsureHover(Border border)
        {
            if (_hover.TryGetValue(border, out _)) return;
            var state = new HoverState();
            _hover.Add(border, state);
            border.PointerEntered += (_, _) => state.Over = true;
            border.PointerExited += (_, _) => state.Over = false;
            border.PointerCaptureLost += (_, _) => state.Over = false;
        }

        private static Style? GlassStyle(string key)
            => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

        /// <summary>透镜的坐标参照物：背景层才是真正的视口。</summary>
        private FrameworkElement ViewportRoot()
        {
            var backdrop = ShellPage.Current?.GlassBackdrop;
            return backdrop is { ActualWidth: > 10, ActualHeight: > 10 } ? backdrop : _page;
        }

        /// <summary>卡片是否与视口（外扩 marginX/marginY）相交。取不到变换时按不相交处理。</summary>
        private static bool InView(FrameworkElement card, FrameworkElement root, double width, double height, double marginX, double marginY)
        {
            Point origin;
            try { origin = card.TransformToVisual(root).TransformPoint(new Point(0, 0)); }
            catch { return false; }
            var right = origin.X + card.ActualWidth;
            var bottom = origin.Y + card.ActualHeight;
            return right > -marginX && bottom > -marginY && origin.X < width + marginX && origin.Y < height + marginY;
        }

        /// <summary>这个玻璃卡是否被指定样式的玻璃卡（卡片或行卡）包着。</summary>
        private bool HasGlassAncestor(Border border, params Style?[] styles)
        {
            var parent = VisualTreeHelper.GetParent(border);
            while (parent is not null && !ReferenceEquals(parent, _page))
            {
                if (parent is Border outer)
                {
                    foreach (var style in styles)
                        if (style is not null && ReferenceEquals(outer.Style, style)) return true;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            return false;
        }

        /// <summary>每帧由 Win2D 背景调用，坐标实时取自 XAML 视觉树。</summary>
        public IEnumerable<GlassPane> BuildPanes()
        {
            var backdrop = ShellPage.Current?.GlassBackdrop;
            if (backdrop is null || _page.ActualWidth < 10) yield break;
            // 300ms 重扫：滚动时新进入视口的行卡最多晚 0.3 秒补上透镜（再慢就会明显看到「先透明后玻璃」）。
            if (_cards.Count == 0 || Environment.TickCount64 - _lastScan > 300) Scan();
            var viewport = backdrop.ActualWidth * backdrop.ActualHeight;
            var viewWidth = (double)backdrop.ActualWidth;
            var viewHeight = (double)backdrop.ActualHeight;
            var posed = 0;
            var emitted = 0;
            for (var i = 0; i < _cards.Count; i++)
            {
                if (emitted >= MaxPanes) yield break;
                var card = _cards[i];
                if (!IsEffectivelyVisible(card)) continue;
                if (card.Opacity < 0.55 || card.ActualWidth < 60 || card.ActualHeight < 40) continue;
                // 已经从页面摘掉的卡片不能再画透镜，否则滚动/重渲染后会在原处留下“第二圈边”。
                if (!card.IsLoaded) continue;
                // 只挡「整屏铺满」的大容器：共享模糊之后大面板并不比一张卡贵，
                // 阈值放宽到视口的 55%，设置页、插件详情这类大玻璃面板终于也有透镜了。
                if (viewport > 0 && card.ActualWidth * card.ActualHeight > viewport * BigPaneRatio) continue;
                Point origin;
                try { origin = card.TransformToVisual(backdrop).TransformPoint(new Point(0, 0)); }
                catch { continue; }
                var radius = card.CornerRadius.TopLeft;
                if (radius <= 0) radius = 24;

                // TransformToVisual 只反映布局，不包含合成器上的 Scale/Translation。
                // 卡片悬停放大或按压缩小时，透镜必须跟着走，否则会同时看到原位的旧轮廓和新缩小的卡片 —— 就是“两张卡分层”。
                var width = card.ActualWidth;
                var height = card.ActualHeight;
                var x = origin.X;
                var y = origin.Y;
                var followPose = !string.Equals(Environment.GetEnvironmentVariable("NOVALINK_NO_LENS_POSE"), "1", StringComparison.Ordinal);
                if (followPose && Motion.TryGetPose(card, out var scale, out var liftY))
                {
                    posed++;
                    if (Math.Abs(scale - 1f) > 0.0005f)
                    {
                        var scaledWidth = width * scale;
                        var scaledHeight = height * scale;
                        x = origin.X + (width - scaledWidth) / 2;
                        y = origin.Y + (height - scaledHeight) / 2;
                        width = scaledWidth;
                        height = scaledHeight;
                        radius *= scale;
                    }
                    y += liftY;
                }
                // 姿态修正之后可能已经滚出视口，这一步只做一次矩形比较，比每帧多画一块被裁掉的玻璃便宜。
                if (x + width < 0 || y + height < 0 || x > viewWidth || y > viewHeight) continue;
                // 行卡都在列表的 ScrollViewer 里。滚出视口（但仍在页面里）的行卡不算「离开页面」，
                // 位置却是标题栏/工具条上方 —— 不裁掉的话透镜会在那里画出一整块带描边的玻璃，
                // 看起来就是「往上一滚，卡片残影留在上方」。裁到滚动视口，滚动时边缘干净、没有重影。
                if (!ClipToScrollViewport(card, backdrop, ref x, ref y, ref width, ref height, ref radius)) continue;
                emitted++;
                var hover = _hover.TryGetValue(card, out var hoverState) && hoverState.Over ? 1.0 : 0.0;
                yield return new GlassPane(x, y, width, height, radius, i < 4 ? 1.0 : 0.86, _animated.Contains(card), hover);
            }
            // 只在「有卡在动 / 全部静止」之间切换时写一条；悬停时每帧命中数都在变，
            // 按数值写日志会一秒刷几十行。
            if (DebugLog && (posed > 0) != (_lastPosed > 0))
            {
                _lastPosed = posed;
                NovaLink.Lumen.Services.AppLog.Info("glass", "透镜姿态命中：" + posed + "/" + _cards.Count + "，动画卡 " + _animated.Count);
            }
            if (DebugLog && Environment.TickCount64 - _lastDebugLog > 700)
            {
                _lastDebugLog = Environment.TickCount64;
                var sb = new System.Text.StringBuilder("透镜列表：");
                foreach (var c in _cards)
                {
                    var pt = c.TransformToVisual(_page).TransformPoint(new Windows.Foundation.Point(0, 0));
                    sb.Append(c.GetHashCode().ToString("X8"))
                      .Append("/tag=").Append((c.Tag as object)?.GetType().Name ?? "-")
                      .Append("/").Append(c.ActualWidth.ToString("0")).Append("x").Append(c.ActualHeight.ToString("0"))
                      .Append("@").Append(pt.X.ToString("0")).Append(",").Append(pt.Y.ToString("0"))
                      .Append(Motion.TryGetPose(c, out var ps, out _) ? "(pose " + ps.ToString("0.###") + ") " : "(none) ");
                }
                NovaLink.Lumen.Services.AppLog.Info("glass", sb.ToString());
            }
        }

        /// <summary>行卡所在的最近一层 ScrollViewer；不在滚动容器里时返回 null。</summary>
        private ScrollViewer? ScrollAncestor(FrameworkElement card)
        {
            DependencyObject? node = VisualTreeHelper.GetParent(card);
            while (node is not null && !ReferenceEquals(node, _page))
            {
                if (node is ScrollViewer scroll) return scroll;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        /// <summary>把透镜矩形裁到滚动视口内；完全滚出视口时返回 false（这一帧不画）。</summary>
        private bool ClipToScrollViewport(FrameworkElement card, FrameworkElement backdrop,
            ref double x, ref double y, ref double width, ref double height, ref double radius)
        {
            var scroll = ScrollAncestor(card);
            if (scroll is null) return true;
            if (scroll.ActualWidth < 4 || scroll.ActualHeight < 4) return true;
            Point origin;
            try { origin = scroll.TransformToVisual(backdrop).TransformPoint(new Point(0, 0)); }
            catch { return true; }
            var left = Math.Max(x, origin.X);
            var top = Math.Max(y, origin.Y);
            var right = Math.Min(x + width, origin.X + scroll.ActualWidth);
            var bottom = Math.Min(y + height, origin.Y + scroll.ActualHeight);
            if (right - left < 8 || bottom - top < 8) return false;
            x = left;
            y = top;
            width = right - left;
            height = bottom - top;
            // 裁过的矩形可能只剩很窄一条，圆角按短边收一下，避免半径超过半宽画出歪边。
            radius = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
            return true;
        }

        /// <summary>卡片是否与所在滚动视口相交（滚出视口的行卡不占透镜名额）。</summary>
        private bool InScrollViewport(FrameworkElement card, FrameworkElement view)
        {
            var scroll = ScrollAncestor(card);
            if (scroll is null) return true;
            if (scroll.ActualWidth < 4 || scroll.ActualHeight < 4) return true;
            try
            {
                var rect = card.TransformToVisual(view).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                var host = scroll.TransformToVisual(view).TransformBounds(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
                return rect.Right > host.Left - 24 && rect.Bottom > host.Top - 24 &&
                       rect.Left < host.Right + 24 && rect.Top < host.Bottom + 24;
            }
            catch { return true; }
        }

        /// <summary>
        /// 这张卡在视觉树上到底看不看得见：自己和所有祖先都不能是 Collapsed/Hidden，祖先也不能整体透明。
        /// 只查 <c>Visibility</c> 不够 —— 子元素不会继承父元素的 Visibility 值，折叠面板里的卡片
        /// 依旧报告 Visible，于是玻璃透镜会留在它最后布局的位置上。
        /// </summary>
        private static bool IsEffectivelyVisible(FrameworkElement element)
        {
            DependencyObject? node = element;
            while (node is not null)
            {
                if (node is UIElement ui && ui.Visibility != Visibility.Visible) return false;
                if (node is FrameworkElement fe && fe.Opacity <= 0.01) return false;
                if (ReferenceEquals(node, ShellPage.Current)) break;
                node = VisualTreeHelper.GetParent(node);
            }
            return true;
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

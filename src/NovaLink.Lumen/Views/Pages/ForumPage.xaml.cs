using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Forum;
using NovaLink.Lumen.Services.Http;
using NovaLink.Lumen.Views.Dialogs;
using Windows.System;
using Windows.UI;

namespace NovaLink.Lumen.Views.Pages;

public sealed partial class ForumPage : Page
{
    private ForumMe? _me;
    private bool _feedTab;
    private bool _loading;
    private string? _topicId;
    private DateTimeOffset _feedCooldownUntil;
    private DateTimeOffset _commentCooldownUntil;
    private DateTimeOffset _replyCooldownUntil;
    private DispatcherTimer? _ticker;
    private LoadingState? _busy;
    private long _renderToken;

    public ForumPage()
    {
        InitializeComponent();
        _busy = LoadingState.Attach(PageRoot, "正在读取论坛…");
        _busy.Hide();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>显示整页加载层（只做提示，不拦截点击）。</summary>
    private void ShowBusy(string message)
    {
        if (_busy is null) return;
        _busy.SetMessage(message);
        _busy.Show();
    }

    /// <summary>收起整页加载层。</summary>
    private void HideBusy() => _busy?.Hide();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LumenRuntime.Accounts.AccountsChanged += OnAccountChanged;
        LumenRuntime.Settings.Changed += OnAccountChanged;
        _ticker ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick -= Ticker_Tick;
        _ticker.Tick += Ticker_Tick;
        _ticker.Start();
        _ = LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LumenRuntime.Accounts.AccountsChanged -= OnAccountChanged;
        LumenRuntime.Settings.Changed -= OnAccountChanged;
        _ticker?.Stop();
        // 页面卸载：摘掉加载层，并让正在分批搭建的列表立即作废。
        _renderToken++;
        _busy?.Detach();
        _busy = null;
    }

    private void OnAccountChanged(object? sender, EventArgs e) => _ = LoadAsync();

    private void Ticker_Tick(object? sender, object e) => UpdateCooldowns();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        StatusText.Text = "正在读取论坛…";
        ShowBusy("正在读取论坛…");
        try
        {
            // 论坛请求与 JSON 解析放到后台线程，UI 线程只负责更新控件。
            var account = LumenRuntime.Accounts.Selected;
            var session = await Task.Run(() => LumenRuntime.Forum.SessionAsync(account));
            _me = session.Me;
            UpdateMe(session);

            var topics = await Task.Run(() => LumenRuntime.Forum.TopicsAsync(account));
            await RenderTopicsAsync(topics.Topics);

            var feed = await Task.Run(() => LumenRuntime.Forum.FeedAsync(account));
            await RenderFeedAsync(feed.Feed);

            if (_topicId is not null && !TopicDetailPanel.Visibility.Equals(Visibility.Collapsed))
                await ShowTopicAsync(_topicId, false);

            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = "论坛暂时不可用：" + Human(ex);
            AppLog.Warn("forum", "读取论坛失败：" + ex);
        }
        finally
        {
            _loading = false;
            UpdateCooldowns();
            HideBusy();
        }
    }

    private void UpdateMe(ForumSessionResponse session)
    {
        if (session.Banned)
        {
            MeName.Text = "已被封禁";
            MeKind.Text = "当前账户无法参与论坛";
            MeStats.Text = session.Msg ?? "";
            MeAvatarHost.Child = AvatarVisual("禁", "", 38);
            NewTopicButton.IsEnabled = false;
            TopicHint.Text = "当前账户已被限制发言。";
            return;
        }

        if (!session.Authenticated || session.Me is null)
        {
            MeName.Text = "未登录";
            MeKind.Text = "离线只读";
            MeStats.Text = "登录后可发言";
            MeAvatarHost.Child = AvatarVisual("N", "", 38);
            NewTopicButton.IsEnabled = false;
            TopicHint.Text = "登录 NovaLink 或 Microsoft 账户后即可创建话题。";
            return;
        }

        var me = session.Me;
        MeName.Text = me.Name;
        MeKind.Text = KindLabel(me.Kind);
        MeStats.Text = me.PostCount + " 次观点";
        MeAvatarHost.Child = AvatarVisual(me.Name, me.Avatar, 38);

        NewTopicButton.IsEnabled = me.CanCreate;
        TopicHint.Text = me.CanCreate
            ? "创建新话题会先经过 AI 审核，未通过时转交管理员。"
            : "再表达 " + Math.Max(0, 10 - me.PostCount) + " 次观点即可创建话题。";

        if (me.FeedCooldown > 0)
            _feedCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(me.FeedCooldown);
        if (me.CommentCooldown > 0)
            _commentCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(me.CommentCooldown);
    }

    private void UpdateCooldowns()
    {
        var authenticated = IsAuthenticated;
        var feedLeft = Math.Max(0, (int)Math.Ceiling((_feedCooldownUntil - DateTimeOffset.UtcNow).TotalSeconds));
        var commentLeft = Math.Max(0, (int)Math.Ceiling((_commentCooldownUntil - DateTimeOffset.UtcNow).TotalSeconds));
        var replyLeft = Math.Max(0, (int)Math.Ceiling((_replyCooldownUntil - DateTimeOffset.UtcNow).TotalSeconds));

        FeedHint.Text = !authenticated ? "登录后参与动态" : feedLeft > 0 ? "动态冷却 " + feedLeft + " 秒" : "每 60 秒一条 · AI 审核";
        MomentButton.IsEnabled = authenticated && feedLeft == 0;
        ReplyButton.IsEnabled = authenticated && replyLeft == 0;
        ReplyHint.Text = !authenticated ? ForumService.LoginHint : replyLeft > 0 ? "回复冷却 " + replyLeft + " 秒" : "回复会立即显示。";

    }

    private bool IsAuthenticated => _me is { } me && !me.Banned && !string.IsNullOrWhiteSpace(me.Key);

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _topicId = null;
        TopicDetailPanel.Visibility = Visibility.Collapsed;
        TopicsPanel.Visibility = Visibility.Visible;
        await LoadAsync();
    }

    private void Community_Click(object sender, RoutedEventArgs e)
    {
        _ = Launcher.LaunchUriAsync(new Uri("http://121.41.174.153:3210/"));
    }

    private void MeCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShellPage.Current?.OpenAccountMenu();
    }

    private void TopicsTab_Click(object sender, RoutedEventArgs e) => SelectTab(false);
    private void FeedTab_Click(object sender, RoutedEventArgs e) => SelectTab(true);

    private void SelectTab(bool feed)
    {
        _feedTab = feed;
        TopicsPanel.Visibility = feed ? Visibility.Collapsed : Visibility.Visible;
        FeedPanel.Visibility = feed ? Visibility.Visible : Visibility.Collapsed;
        if (feed) TopicDetailPanel.Visibility = Visibility.Collapsed;
        TopicsTabLine.Background = feed ? new SolidColorBrush(Colors.Transparent) : Res("BrushMint");
        FeedTabLine.Background = feed ? Res("BrushMint") : new SolidColorBrush(Colors.Transparent);
        // 标签切换时内容区淡入，避免整块列表硬切。
        FadeIn(feed ? FeedPanel : TopicsPanel);
        if (feed) _ = LoadFeedAsync();
    }

    private async void NewTopic_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAuthenticated)
        {
            await MessageDialog.ShowAsync(XamlRoot, "需要登录", ForumService.LoginHint + "。");
            return;
        }
        if (_me is not { CanCreate: true })
        {
            await MessageDialog.ShowAsync(XamlRoot, "还不能创建话题", "先在论坛或动态中表达 " + Math.Max(0, 10 - (_me?.PostCount ?? 0)) + " 次观点。");
            return;
        }
        await ShowCreateTopicAsync();
    }

    private async Task ShowCreateTopicAsync()
    {
        var modal = GlassModal.Show("创建话题", "创建新话题会先经过 AI 审核，未通过时转交管理员。", 600);
        var titleBox = Field("标题", 60);
        var bodyBox = Field("把你的观点写完整…", 170, true);
        var hint = new TextBlock
        {
            Text = "标题尽量清楚，正文可以分多段。",
            FontSize = 11.5,
            Foreground = Res("BrushTextTertiary"),
            TextWrapping = TextWrapping.Wrap
        };
        modal.Body.Children.Add(titleBox);
        modal.Body.Children.Add(bodyBox);
        modal.Body.Children.Add(hint);
        modal.PrimaryText = "发布";
        modal.SecondaryText = "取消";
        modal.PrimaryInvoked = async () =>
        {
            var title = titleBox.Text.Trim();
            var body = bodyBox.Text.Trim();
            if (title.Length < 2 || body.Length < 4)
            {
                hint.Text = "标题至少 2 个字，正文至少 4 个字。";
                hint.Foreground = Res("BrushMint");
                return;
            }
            modal.SetBusy(true);
            try
            {
                var result = await LumenRuntime.Forum.CreateTopicAsync(LumenRuntime.Accounts.Selected, title, body);
                modal.Close();
                if (result.Pending)
                    await MessageDialog.ShowAsync(XamlRoot, "已提交审核", "AI 暂未通过这条话题，已转交管理员复核。");
                else if (!result.Ok)
                    await MessageDialog.ShowAsync(XamlRoot, "未能发布", result.Msg ?? result.AiReason ?? "服务端拒绝了这次发布。");
                else
                {
                    SelectTab(false);
                    await LoadAsync();
                }
            }
            catch (Exception ex)
            {
                hint.Text = Human(ex);
                hint.Foreground = Res("BrushMint");
                AppLog.Warn("forum", "创建话题失败：" + ex);
            }
            finally
            {
                modal.SetBusy(false);
            }
        };
        await modal.WaitAsync();
    }

    /// <summary>分批搭建话题卡片（每批 6 张让出一帧），过期的一轮在检查点立即退出。</summary>
    private async Task RenderTopicsAsync(List<ForumTopicDto> topics)
    {
        var token = ++_renderToken;
        TopicList.Children.Clear();
        HideBusy();
        if (topics.Count == 0)
        {
            TopicList.Children.Add(EmptyCard("还没有话题", "登录后创建第一个话题，让观点被看见。"));
            return;
        }
        var built = 0;
        foreach (var topic in topics)
        {
            if (token != _renderToken) return;
            var card = new Border
            {
                Style = StyleOf("GlassCard"),
                Padding = new Thickness(18, 16, 18, 16),
                Tag = topic.Id
            };
            var grid = new Grid { ColumnSpacing = 14 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = AvatarVisual(topic.Author.Name, topic.Author.Avatar, 42);
            avatar.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(avatar);

            var stack = new StackPanel { Spacing = 6 };
            var title = new TextBlock
            {
                Text = topic.Title,
                FontSize = 15.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("BrushText"),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stack.Children.Add(title);

            var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            var author = new TextBlock
            {
                Text = topic.Author.Name,
                FontSize = 11.5,
                Foreground = Res("BrushMint")
            };
            author.Tapped += async (_, e) =>
            {
                e.Handled = true;
                await ShowProfileAsync(topic.Author.Key);
            };
            meta.Children.Add(author);
            meta.Children.Add(Dot());
            meta.Children.Add(new TextBlock
            {
                Text = KindLabel(topic.Author.Kind),
                FontSize = 11.5,
                Foreground = Res("BrushTextTertiary")
            });
            meta.Children.Add(Dot());
            meta.Children.Add(new TextBlock
            {
                Text = Relative(topic.LastActivity > 0 ? topic.LastActivity : topic.Created),
                FontSize = 11.5,
                Foreground = Res("BrushTextTertiary")
            });
            stack.Children.Add(meta);

            if (!string.IsNullOrWhiteSpace(topic.Body))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = topic.Body.Replace("\n", " "),
                    FontSize = 12.5,
                    Foreground = Res("BrushTextSecondary"),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            var right = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(new TextBlock
            {
                Text = topic.PostCount.ToString(),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("BrushText"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            right.Children.Add(new TextBlock
            {
                Text = "回复",
                FontSize = 10.5,
                Foreground = Res("BrushTextTertiary"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            card.Child = GlassSurface(grid);
            card.Tapped += async (_, _) => await ShowTopicAsync(topic.Id);
            Motion.Hover(card, 1.005f, 1.4f);
            TopicList.Children.Add(card);
            Motion.RowIn(card, built);
            if (++built % 6 == 0) await UiYield.NextFrameAsync();
        }
    }

    private void BackToTopics_Click(object sender, RoutedEventArgs e)
    {
        _topicId = null;
        TopicDetailPanel.Visibility = Visibility.Collapsed;
        if (_feedTab)
        {
            TopicsPanel.Visibility = Visibility.Collapsed;
            FeedPanel.Visibility = Visibility.Visible;
        }
        else
        {
            FeedPanel.Visibility = Visibility.Collapsed;
            TopicsPanel.Visibility = Visibility.Visible;
        }
    }

    private async Task ShowTopicAsync(string topicId, bool scrollToTop = true)
    {
        try
        {
            var result = await LumenRuntime.Forum.TopicAsync(topicId, LumenRuntime.Accounts.Selected);
            if (result.Topic is null)
            {
                await MessageDialog.ShowAsync(XamlRoot, "话题不存在", result.Msg ?? "这条话题可能已被删除。");
                return;
            }
            var topic = result.Topic;
            _topicId = topic.Id;
            DetailTitle.Text = topic.Title;
            DetailAuthor.Text = topic.Author.Name;
            DetailTime.Text = Relative(topic.Created) + " · " + KindLabel(topic.Author.Kind);
            DetailBody.Text = topic.Body;
            RenderReplies(topic.Posts);
            TopicsPanel.Visibility = Visibility.Collapsed;
            FeedPanel.Visibility = Visibility.Collapsed;
            TopicDetailPanel.Visibility = Visibility.Visible;
            if (scrollToTop) ContentScroll.ChangeView(null, 0, null);
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(XamlRoot, "无法打开话题", Human(ex));
            AppLog.Warn("forum", "打开话题失败：" + ex);
        }
    }

    private void RenderReplies(List<ForumPostDto> posts)
    {
        ReplyList.Children.Clear();
        if (posts.Count == 0)
        {
            // 空态也走同一级淡玻璃，否则详情页里会突然冒出一张完整的 GlassCard。
            ReplyList.Children.Add(EmptyCard("还没有回复", "成为第一个留下观点的人。", faint: true));
            return;
        }
        for (var index = 0; index < posts.Count; index++)
        {
            var post = posts[index];
            // 回复条目降一级玻璃：原来复用 GlassCard（BrushGlassCard + BrushGlassRim + 24 圆角 + 顶部高光），
            // 摊在话题主卡下面就是「卡中卡」——浅色下两层半透明白叠出双描边，看起来像套了两个框。
            // 现在改为 BrushGlassFaint 淡底 + 1px BrushBorderSubtle 细描边 + 16 圆角，只留一层轻量条目感。
            var card = new Border
            {
                Background = Res("BrushGlassFaint"),
                BorderBrush = Res("BrushBorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18, 14, 18, 14)
            };
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = AvatarVisual(post.Author.Name, post.Author.Avatar, 34);
            avatar.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(avatar);

            var stack = new StackPanel { Spacing = 5 };
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            var name = new TextBlock { Text = post.Author.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("BrushText") };
            name.Tapped += async (_, e) =>
            {
                e.Handled = true;
                await ShowProfileAsync(post.Author.Key);
            };
            meta.Children.Add(name);
            meta.Children.Add(new TextBlock { Text = KindLabel(post.Author.Kind), FontSize = 11, Foreground = Res("BrushTextTertiary") });
            meta.Children.Add(new TextBlock { Text = Relative(post.Created), FontSize = 11, Foreground = Res("BrushTextTertiary") });
            stack.Children.Add(meta);
            stack.Children.Add(new TextBlock
            {
                Text = post.Body,
                FontSize = 13,
                Foreground = Res("BrushTextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21
            });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            // 轻量条目不再包 GlassSurface：顶部高光是给整页主卡用的，每条回复都叠一条亮边又变回"重"卡片。
            card.Child = grid;
            ReplyList.Children.Add(card);
            Motion.RowIn(card, index);
        }
    }

    private async void Reply_Click(object sender, RoutedEventArgs e)
    {
        if (_topicId is null) return;
        if (!IsAuthenticated)
        {
            await MessageDialog.ShowAsync(XamlRoot, "需要登录", ForumService.LoginHint + "。");
            return;
        }
        var body = ReplyBox.Text.Trim();
        if (body.Length < 1)
        {
            ReplyHint.Text = "先写一点内容。";
            return;
        }
        ReplyButton.IsEnabled = false;
        try
        {
            var result = await LumenRuntime.Forum.ReplyAsync(LumenRuntime.Accounts.Selected, _topicId, body);
            if (!result.Ok)
            {
                ReplyHint.Text = result.Msg ?? "回复失败。";
                return;
            }
            ReplyBox.Text = "";
            _replyCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(5);
            await ShowTopicAsync(_topicId, false);
        }
        catch (ApiException ex) when (ex.StatusCode == 429)
        {
            _replyCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(5);
            ReplyHint.Text = "回复太快了，5 秒后再试。";
        }
        catch (Exception ex)
        {
            ReplyHint.Text = Human(ex);
            AppLog.Warn("forum", "回复失败：" + ex);
        }
        finally
        {
            UpdateCooldowns();
        }
    }

    private async void Moment_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAuthenticated)
        {
            await MessageDialog.ShowAsync(XamlRoot, "需要登录", ForumService.LoginHint + "。");
            return;
        }
        var body = MomentBox.Text.Trim();
        if (body.Length < 1)
        {
            FeedHint.Text = "写一点想法再发布。";
            return;
        }
        MomentButton.IsEnabled = false;
        try
        {
            var result = await LumenRuntime.Forum.CreateMomentAsync(LumenRuntime.Accounts.Selected, body);
            if (result.Pending)
                await MessageDialog.ShowAsync(XamlRoot, "已提交审核", "AI 暂未通过这条动态，已转交管理员复核。");
            else if (!result.Ok)
                await MessageDialog.ShowAsync(XamlRoot, "未能发布", result.Msg ?? result.AiReason ?? "服务端拒绝了这条动态。");
            else
            {
                MomentBox.Text = "";
                _feedCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(60);
                await LoadFeedAsync();
            }
        }
        catch (ApiException ex) when (ex.StatusCode == 429)
        {
            _feedCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            FeedHint.Text = "动态发得太快了，60 秒后再试。";
        }
        catch (Exception ex)
        {
            FeedHint.Text = Human(ex);
            AppLog.Warn("forum", "发布动态失败：" + ex);
        }
        finally
        {
            UpdateCooldowns();
        }
    }

    private async Task LoadFeedAsync()
    {
        ShowBusy("正在读取动态…");
        try
        {
            var feed = await Task.Run(() => LumenRuntime.Forum.FeedAsync(LumenRuntime.Accounts.Selected));
            await RenderFeedAsync(feed.Feed);
        }
        catch (Exception ex)
        {
            StatusText.Text = "动态读取失败：" + Human(ex);
            AppLog.Warn("forum", "读取动态失败：" + ex);
        }
        finally
        {
            HideBusy();
        }
    }

    /// <summary>分批搭建动态卡片（每批 6 张让出一帧），避免几十条动态把 UI 线程一次堵死。</summary>
    private async Task RenderFeedAsync(List<ForumMomentDto> moments)
    {
        var token = ++_renderToken;
        FeedList.Children.Clear();
        HideBusy();
        if (moments.Count == 0)
        {
            FeedList.Children.Add(EmptyCard("还没有动态", "分享此刻的想法，让论坛像朋友圈一样流动。"));
            return;
        }
        var built = 0;
        foreach (var moment in moments)
        {
            if (token != _renderToken) return;
            var card = new Border { Style = StyleOf("GlassCard"), Padding = new Thickness(20, 16, 20, 16) };
            var stack = new StackPanel { Spacing = 10 };

            var head = new Grid { ColumnSpacing = 11 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = AvatarVisual(moment.Author.Name, moment.Author.Avatar, 38);
            head.Children.Add(avatar);

            var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = moment.Author.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("BrushText") };
            name.Tapped += async (_, e) =>
            {
                e.Handled = true;
                await ShowProfileAsync(moment.Author.Key);
            };
            who.Children.Add(name);
            who.Children.Add(new TextBlock { Text = KindLabel(moment.Author.Kind), FontSize = 10.5, Foreground = Res("BrushTextTertiary") });
            Grid.SetColumn(who, 1);
            head.Children.Add(who);

            var time = new TextBlock { Text = Relative(moment.Created), FontSize = 10.5, Foreground = Res("BrushTextTertiary"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(time, 2);
            head.Children.Add(time);
            stack.Children.Add(head);

            stack.Children.Add(new TextBlock
            {
                Text = moment.Body,
                FontSize = 13.5,
                Foreground = Res("BrushText"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 23,
                Margin = new Thickness(49, 0, 0, 0)
            });

            if (moment.Comments.Count > 0)
            {
                var comments = new StackPanel { Spacing = 8, Margin = new Thickness(49, 2, 0, 0) };
                foreach (var comment in moment.Comments)
                    comments.Children.Add(CommentRow(comment));
                stack.Children.Add(comments);
            }

            var composer = new Grid { ColumnSpacing = 8, Margin = new Thickness(49, 4, 0, 0) };
            composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox
            {
                PlaceholderText = "留下评论…",
                Height = 36,
                FontSize = 12.5,
                Style = StyleOf("FieldBox"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var send = new Button
            {
                Content = "评论",
                Style = StyleOf("GhostButton"),
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            send.IsEnabled = IsAuthenticated;
            send.Click += async (_, _) =>
            {
                var text = box.Text.Trim();
                if (text.Length < 1) return;
                if (!IsAuthenticated)
                {
                    await MessageDialog.ShowAsync(XamlRoot, "需要登录", ForumService.LoginHint + "。");
                    return;
                }
                send.IsEnabled = false;
                try
                {
                    var result = await LumenRuntime.Forum.CommentMomentAsync(LumenRuntime.Accounts.Selected, moment.Id, text);
                    if (result.Pending)
                        await MessageDialog.ShowAsync(XamlRoot, "评论已提交审核", "AI 审核通过后会显示出来。");
                    else if (!result.Ok)
                        await MessageDialog.ShowAsync(XamlRoot, "未能评论", result.Msg ?? result.AiReason ?? "服务端拒绝了这条评论。");
                    else
                    {
                        _commentCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(60);
                        await LoadFeedAsync();
                    }
                }
                catch (ApiException ex) when (ex.StatusCode == 429)
                {
                    _commentCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(60);
                    await MessageDialog.ShowAsync(XamlRoot, "评论太快了", "每条评论之间需要间隔 60 秒。");
                }
                catch (Exception ex)
                {
                    await MessageDialog.ShowAsync(XamlRoot, "评论失败", Human(ex));
                }
                finally
                {
                    send.IsEnabled = true;
                }
            };
            composer.Children.Add(box);
            Grid.SetColumn(send, 1);
            composer.Children.Add(send);
            stack.Children.Add(composer);

            card.Child = GlassSurface(stack);
            FeedList.Children.Add(card);
            Motion.RowIn(card, built);
            if (++built % 6 == 0) await UiYield.NextFrameAsync();
        }
    }

    private FrameworkElement CommentRow(ForumCommentDto comment)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var avatar = AvatarVisual(comment.Author.Name, comment.Author.Avatar, 24);
        avatar.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(avatar);
        var stack = new StackPanel { Spacing = 2 };
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        meta.Children.Add(new TextBlock { Text = comment.Author.Name, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Res("BrushMint") });
        meta.Children.Add(new TextBlock { Text = Relative(comment.Created), FontSize = 10.5, Foreground = Res("BrushTextTertiary") });
        stack.Children.Add(meta);
        stack.Children.Add(new TextBlock
        {
            Text = comment.Body,
            FontSize = 12.5,
            Foreground = Res("BrushTextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        });
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        return grid;
    }

    private async Task ShowProfileAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        try
        {
            var result = await LumenRuntime.Forum.ProfileAsync(key);
            var profile = result.Profile;
            if (profile is null)
            {
                await MessageDialog.ShowAsync(XamlRoot, "用户不存在", "这个论坛身份还没有留下观点。");
                return;
            }
            var modal = GlassModal.Show(profile.Name, KindLabel(profile.Kind), 430);
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            head.Children.Add(AvatarVisual(profile.Name, profile.Avatar, 64));
            var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = profile.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Res("BrushText") });
            info.Children.Add(new TextBlock { Text = profile.PostCount + " 次观点", FontSize = 12, Foreground = Res("BrushTextSecondary") });
            head.Children.Add(info);
            modal.Body.Children.Add(head);
            modal.PrimaryText = "关闭";
            modal.HideSecondary();
            await modal.WaitAsync();
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            await MessageDialog.ShowAsync(XamlRoot, "用户不存在", "这个论坛身份还没有留下观点。");
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(XamlRoot, "无法读取主页", Human(ex));
            AppLog.Warn("forum", "读取论坛主页失败：" + ex);
        }
    }

    private static TextBox Field(string placeholder, double height, bool multiline = false) => new()
    {
        PlaceholderText = placeholder,
        Height = height,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Style = StyleOf("FieldBox"),
        VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
    };

    private static FrameworkElement AvatarVisual(string name, string url, double size)
    {
        var root = new Grid { Width = size, Height = size };
        // 头像占位块：主题 accent 淡底 + 主题描边，浅色下不再是"浅薄荷底 + 近白字"。
        var letter = new Border
        {
            CornerRadius = new CornerRadius(size * 0.3),
            Background = Res("BrushSelected"),
            BorderBrush = Res("BrushBorderSubtle"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = Initial(name),
                FontSize = size * 0.42,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("BrushMint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        root.Children.Add(letter);
        if (!string.IsNullOrWhiteSpace(url))
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri(url)) };
            var clipped = new Border
            {
                CornerRadius = new CornerRadius(size * 0.3),
                BorderBrush = Res("BrushBorderSubtle"),
                BorderThickness = new Thickness(1),
                Child = image
            };
            image.ImageFailed += (_, _) => clipped.Visibility = Visibility.Collapsed;
            root.Children.Add(clipped);
        }
        return root;
    }

    private static string Initial(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        return char.ToUpperInvariant(name.Trim()[0]).ToString();
    }

    private static TextBlock Dot() => new()
    {
        Text = "·",
        FontSize = 11,
        Foreground = Res("BrushTextTertiary")
    };

    /// <summary>空态卡片。faint=true 时用回复条目那一级淡玻璃（详情页里避免"卡中卡"）。</summary>
    private static Border EmptyCard(string title, string text, bool faint = false)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("BrushText"), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Res("BrushTextSecondary"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        if (faint)
        {
            // 与回复条目同一配方，保证详情页里所有"条目级"卡片只有一层细描边。
            return new Border
            {
                Background = Res("BrushGlassFaint"),
                BorderBrush = Res("BrushBorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(20, 18, 20, 18),
                Child = stack
            };
        }
        return new Border
        {
            Style = StyleOf("GlassCard"),
            Padding = new Thickness(24, 26, 24, 26),
            Child = GlassSurface(stack)
        };
    }

    /// <summary>液态玻璃卡片的统一外壳：内容 + 顶部 1px 高光（高光 IsHitTestVisible=false，不吃点击）。</summary>
    private static FrameworkElement GlassSurface(UIElement content)
    {
        var grid = new Grid();
        grid.Children.Add(content);
        grid.Children.Add(new Rectangle
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(1, 0, 1, 0),
            RadiusX = 1,
            RadiusY = 1,
            Fill = Res("BrushGlassHighlight"),
            IsHitTestVisible = false
        });
        return grid;
    }

    /// <summary>标签页切换内容区时的统一淡入时长（落在 180–260ms 区间）。</summary>
    private const int TabFadeMs = 220;

    private static void FadeIn(FrameworkElement element, int durationMs = TabFadeMs)
    {
        if (element is null) return;
        element.Opacity = 0;
        var story = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        story.Begin();
    }

    private static string KindLabel(string kind) => kind switch
    {
        "novalink_microsoft" => "NovaLink · Microsoft",
        "novalink" => "NovaLink",
        "microsoft" => "Microsoft",
        "system" => "NovaLink",
        _ => "离线账户"
    };

    private static string Relative(long value)
    {
        if (value <= 0) return "";
        if (value > 1_000_000_000_000) value /= 1000;
        DateTimeOffset time;
        try { time = DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime(); }
        catch { return ""; }
        var span = DateTimeOffset.Now - time;
        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " 分钟前";
        if (span.TotalHours < 24) return (int)span.TotalHours + " 小时前";
        if (span.TotalDays < 7) return (int)span.TotalDays + " 天前";
        return time.ToString("yyyy年M月d日");
    }

    private static string Human(Exception ex)
    {
        if (ex is InvalidOperationException) return ex.Message;
        if (ex is ApiException api) return api.Message;
        return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
    }

    private static Brush Res(string key)
    {
        try
        {
            if (Application.Current.Resources[key] is Brush brush) return brush;
        }
        catch { }
        return new SolidColorBrush(Colors.White);
    }

    private static Style StyleOf(string key)
    {
        if (Application.Current.Resources[key] is Style style) return style;
        throw new InvalidOperationException("缺少样式：" + key);
    }
}

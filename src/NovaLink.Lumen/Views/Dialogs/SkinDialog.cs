using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Accounts;
using NovaLink.Lumen.Services.Skins;
using Windows.Storage.Pickers;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>
/// 账户皮肤弹窗：
/// 离线账户可以套用某个正版玩家的官方皮肤、选一张本地 PNG，或恢复默认；
/// 正版账户（Microsoft / NovaLink·Microsoft）可以把本地 PNG 上传到正版账号。
/// 网络与落盘都在服务层，这里只做界面、校验提示与中文错误。
/// </summary>
public static class SkinDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root, string accountId)
    {
        var record = LumenRuntime.Settings.Find(accountId);
        if (record is null) return false;

        var isMicrosoft = record.Type == "microsoft";
        var isBound = record.Type == "novalink" && record.Microsoft is not null;
        var canUpload = isMicrosoft || isBound;
        var canUseMojang = record.Type == "offline";
        if (!canUpload && !canUseMojang) return false;

        var modal = GlassModal.Show(
            "账户皮肤",
            canUpload
                ? "把本地皮肤上传到正版账号，游戏里会立刻换成这张；也可以换回账号当前的官方皮肤。"
                : "给离线账户套用正版玩家的皮肤，或选一张本地皮肤。只改外观，不改变账户身份。",
            520);
        modal.PrimaryText = "完成";
        modal.PrimaryInvoked = () =>
        {
            modal.Close();
            return Task.CompletedTask;
        };

        var changed = false;
        var mojang = new MojangSkinService(LumenRuntime.Http);

        // ---------- 顶部预览 ----------
        var previewImage = new Image { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
        var previewFallback = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(record.Name) ? "N" : record.Name[..1].ToUpperInvariant(),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.BrushTextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var previewHost = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(16),
            Background = ThemeManager.Brush("BrushFillSubtle", ThemeManager.ControlFill),
            BorderBrush = ThemeManager.Brush("BrushFieldBorder", ThemeManager.ControlBorder),
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { previewFallback, previewImage } }
        };
        var previewName = new TextBlock
        {
            Text = record.Name,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.BrushText
        };
        var previewHint = new TextBlock
        {
            Text = canUpload
                ? "上传会直接改掉正版账号的皮肤，游戏里与皮肤站都会同步更新。"
                : "套用皮肤不会让你的离线账户变成正版账号。",
            FontSize = 11.5,
            Foreground = ThemeManager.BrushTextTertiary,
            TextWrapping = TextWrapping.Wrap
        };
        var previewInfo = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        previewInfo.Children.Add(previewName);
        previewInfo.Children.Add(previewHint);
        var previewRow = new Grid { ColumnSpacing = 14 };
        previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previewRow.Children.Add(previewHost);
        Grid.SetColumn(previewInfo, 1);
        previewRow.Children.Add(previewInfo);
        modal.Body.Children.Add(previewRow);

        // ---------- 状态行 ----------
        var status = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeManager.BrushTextTertiary
        };

        void SetStatus(string text, bool error = false, bool success = false)
        {
            status.Text = text;
            status.Foreground = error
                ? ThemeManager.BrushDanger
                : success ? ThemeManager.BrushAccent : ThemeManager.BrushTextTertiary;
        }

        void UpdatePreview()
        {
            var current = LumenRuntime.Accounts.ToPublic(record);
            previewName.Text = current.Name;
            // 先把首字母放上来：网络/本地图片解码完成再替换，避免出现一块空白。
            previewImage.Source = null;
            previewImage.Visibility = Visibility.Collapsed;
            previewFallback.Visibility = Visibility.Visible;
            TrySetPreview(previewImage, previewFallback, current.AvatarUrl);
        }
        UpdatePreview();

        if (canUseMojang)
        {
            modal.Body.Children.Add(Divider());
            modal.Body.Children.Add(Section("用正版玩家的皮肤",
                "输入正版用户名，把他在官方的皮肤套到当前离线账户上。进游戏时只认你的离线名字。"));
            var nameBox = Field(record.Name, "正版玩家名，例如 Notch");
            var fetch = ActionButton("获取皮肤", primary: true);
            var nameRow = new Grid { ColumnSpacing = 8 };
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nameRow.Children.Add(nameBox);
            Grid.SetColumn(fetch, 1);
            nameRow.Children.Add(fetch);
            modal.Body.Children.Add(nameRow);

            fetch.Click += async (_, _) =>
            {
                var name = (nameBox.Text ?? "").Trim();
                if (name.Length == 0)
                {
                    SetStatus("请先填写正版玩家名。", error: true);
                    nameBox.Focus(FocusState.Programmatic);
                    return;
                }

                modal.SetBusy(true);
                SetStatus("正在从 Mojang 获取皮肤…");
                try
                {
                    await LumenRuntime.Accounts.ApplyMojangSkinAsync(record, name);
                    changed = true;
                    UpdatePreview();
                    SetStatus("已应用「" + name + "」的皮肤。", success: true);
                }
                catch (Exception ex)
                {
                    SetStatus(Human(ex), error: true);
                    AppLog.Warn("account", "套用正版皮肤失败：" + ex.Message);
                }
                finally
                {
                    modal.SetBusy(false);
                }
            };
        }

        if (canUseMojang)
        {
            modal.Body.Children.Add(Divider());
            modal.Body.Children.Add(Section("本地皮肤图片",
                "支持 64×64 或 64×32 的 PNG。挑一张你喜欢的皮肤文件即可。"));
            var localButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var pick = ActionButton("选择本地图片…", primary: true);
            var reset = ActionButton("恢复默认皮肤");
            localButtons.Children.Add(pick);
            localButtons.Children.Add(reset);
            modal.Body.Children.Add(localButtons);

            pick.Click += async (_, _) =>
            {
                var path = await PickSkinAsync();
                if (path is null) return;
                if (!AccountService.LooksLikeSkin(path))
                {
                    SetStatus("这张图片不能当皮肤：需要 64×64 或 64×32 的 PNG。", error: true);
                    return;
                }

                modal.SetBusy(true);
                SetStatus("正在保存皮肤…");
                try
                {
                    await LumenRuntime.Accounts.ApplyLocalSkinAsync(record, path);
                    changed = true;
                    UpdatePreview();
                    SetStatus("已应用本地皮肤。", success: true);
                }
                catch (Exception ex)
                {
                    SetStatus(Human(ex), error: true);
                    AppLog.Warn("account", "应用本地皮肤失败：" + ex.Message);
                }
                finally
                {
                    modal.SetBusy(false);
                }
            };

            reset.Click += (_, _) =>
            {
                try
                {
                    LumenRuntime.Accounts.ClearSkin(record);
                    changed = true;
                    UpdatePreview();
                    SetStatus("已恢复默认皮肤。", success: true);
                }
                catch (Exception ex)
                {
                    SetStatus("恢复默认皮肤失败，请稍后再试。", error: true);
                    AppLog.Warn("account", "恢复默认皮肤失败：" + ex.Message);
                }
            };
        }

        if (canUpload)
        {
            modal.Body.Children.Add(Divider());
            modal.Body.Children.Add(Section("上传到正版账号",
                "这张皮肤会替换掉正版账号当前的皮肤，游戏里立刻生效，并同步到皮肤站。"));
            var variant = new ComboBox
            {
                MinWidth = 150,
                SelectedIndex = 0,
                FontSize = 12.5,
                Items = { "经典模型", "纤细模型" }
            };
            var variantRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            variantRow.Children.Add(new TextBlock
            {
                Text = "皮肤模型",
                FontSize = 12.5,
                Foreground = ThemeManager.BrushTextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });
            variantRow.Children.Add(variant);
            modal.Body.Children.Add(variantRow);

            var uploadButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var upload = ActionButton("选择本地图片并上传…", primary: true);
            var revert = ActionButton("换回账号当前皮肤");
            uploadButtons.Children.Add(upload);
            uploadButtons.Children.Add(revert);
            modal.Body.Children.Add(uploadButtons);

            upload.Click += async (_, _) =>
            {
                var path = await PickSkinAsync();
                if (path is null) return;
                if (!AccountService.LooksLikeSkin(path))
                {
                    SetStatus("这张图片不能当皮肤：需要 64×64 或 64×32 的 PNG。", error: true);
                    return;
                }

                var target = LumenRuntime.Settings.Find(accountId);
                if (target is null) return;
                var microsoftAccount = target.Type == "microsoft"
                    ? target
                    : LumenRuntime.Accounts.LocalMicrosoftFor(target);
                if (microsoftAccount is null)
                {
                    SetStatus("这个账户没有绑定 Microsoft 账号，无法上传到正版。", error: true);
                    return;
                }

                var slim = variant.SelectedIndex == 1;
                modal.SetBusy(true);
                SetStatus("正在上传到正版账号…");
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path);
                    var token = await MicrosoftTokenAsync(microsoftAccount);
                    try
                    {
                        await mojang.UploadAsync(token, bytes, slim);
                    }
                    catch (SkinAuthExpiredException)
                    {
                        // 令牌可能被别处刷新掉了：重新换一次再试，仍失败就把中文提示抛给用户。
                        await LumenRuntime.Microsoft.RefreshAsync(microsoftAccount);
                        token = LumenRuntime.Accounts.MicrosoftTokenFor(microsoftAccount);
                        await mojang.UploadAsync(token, bytes, slim);
                    }

                    await LumenRuntime.Accounts.ApplyLocalSkinAsync(target, path, slim ? "slim" : "classic");
                    changed = true;
                    UpdatePreview();
                    SetStatus("已上传到正版账号，游戏里会显示这张皮肤。", success: true);
                }
                catch (Exception ex)
                {
                    SetStatus(Human(ex), error: true);
                    AppLog.Warn("account", "上传正版皮肤失败：" + ex.Message);
                }
                finally
                {
                    modal.SetBusy(false);
                }
            };

            revert.Click += (_, _) =>
            {
                try
                {
                    LumenRuntime.Accounts.ClearSkin(record);
                    changed = true;
                    UpdatePreview();
                    SetStatus("已换回账号当前的官方皮肤。", success: true);
                }
                catch (Exception ex)
                {
                    SetStatus("换回官方皮肤失败，请稍后再试。", error: true);
                    AppLog.Warn("account", "清理本地皮肤失败：" + ex.Message);
                }
            };
        }

        modal.Body.Children.Add(status);
        Motion.AttachButtons(modal.Body);

        await modal.WaitAsync();
        return changed;
    }

    /// <summary>拿正版令牌；快过期就先刷新一次，避免上传时才 401。</summary>
    private static async Task<string> MicrosoftTokenAsync(AccountRecord microsoftAccount)
    {
        if (microsoftAccount.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds())
            await LumenRuntime.Microsoft.RefreshAsync(microsoftAccount);
        return LumenRuntime.Accounts.MicrosoftTokenFor(microsoftAccount);
    }

    private static async Task<string?> PickSkinAsync()
    {
        try
        {
            var window = MainWindow.Current;
            if (window is null) return null;
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".png");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            AppLog.Warn("account", "打开皮肤文件选择器失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 把头像塞进预览框；本地路径与网络地址都支持，缓存要忽略（换皮肤后同名文件内容变了）。
    /// 解码成功才亮图，失败退回首字母，不留空框。
    /// </summary>
    private static void TrySetPreview(Image image, FrameworkElement fallback, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        BitmapImage source;
        try
        {
            source = new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache
            };
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                source.UriSource = new Uri(url);
            else
                source.UriSource = new Uri(Path.GetFullPath(url));
        }
        catch (Exception)
        {
            return;
        }

        source.ImageOpened += (_, _) =>
        {
            image.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Collapsed;
        };
        source.ImageFailed += (_, _) =>
        {
            image.Visibility = Visibility.Collapsed;
            fallback.Visibility = Visibility.Visible;
        };
        image.Source = source;
    }

    private static Border Divider() => new()
    {
        Height = 1,
        Background = ThemeManager.Brush("BrushBorderSubtle", ThemeManager.CardBorder)
    };

    private static StackPanel Section(string title, string note)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.BrushText
        });
        panel.Children.Add(new TextBlock
        {
            Text = note,
            FontSize = 11.5,
            Foreground = ThemeManager.BrushTextTertiary,
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private static TextBox Field(string text, string placeholder) => new()
    {
        Text = text ?? "",
        PlaceholderText = placeholder,
        MinHeight = 44,
        CornerRadius = new CornerRadius(12),
        Background = ThemeManager.Brush("BrushField", ThemeManager.ControlFill),
        BorderBrush = ThemeManager.Brush("BrushFieldBorder", ThemeManager.ControlBorder),
        Foreground = ThemeManager.BrushText,
        PlaceholderForeground = ThemeManager.BrushTextTertiary,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(13, 11, 13, 11),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static Button ActionButton(string text, bool primary = false) => new()
    {
        Content = text,
        Style = (Style)Application.Current.Resources[primary ? "AccentButton" : "GhostButton"],
        Height = 40,
        MinWidth = 96,
        Padding = new Thickness(16, 0, 16, 0),
        FontSize = 12.5,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private static string Human(Exception ex)
    {
        var message = (ex.Message ?? "").Trim();
        return message.Length == 0 ? "操作没成功，请稍后再试。" : message;
    }
}

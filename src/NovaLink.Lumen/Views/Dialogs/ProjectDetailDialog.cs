using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Modrinth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>Rich Modrinth project detail used by 发现 and 库, mirroring the Electron project page.</summary>
public static class ProjectDetailDialog
{
    private static readonly Color Text = Color.FromArgb(0xFF, 0xF3, 0xF7, 0xFE);
    private static readonly Color Muted = Color.FromArgb(0xFF, 0x91, 0xA0, 0xB8);
    private static readonly Color Faint = Color.FromArgb(0xFF, 0x6E, 0x7E, 0x95);
    private static readonly Color Mint = Color.FromArgb(0xFF, 0x7C, 0xEB, 0xD6);

    public static async Task ShowAsync(XamlRoot root, ModrinthProject project, InstanceSummary? instance,
        string? installedVersion = null, bool readOnly = false)
    {
        if (GlassModal.Current is { IsClosed: false }) return;
        var modal = GlassModal.Show(project.Title, "by " + (string.IsNullOrWhiteSpace(project.Author) ? "未知作者" : project.Author), 760);
        modal.PrimaryText = "安装到实例";
        modal.SecondaryText = "在 Modrinth 打开";
        modal.SecondaryInvoked = async () =>
        {
            var slug = string.IsNullOrWhiteSpace(project.Slug) ? project.ProjectId : project.Slug;
            if (Uri.TryCreate("https://modrinth.com/" + project.ProjectType + "/" + slug, UriKind.Absolute, out var uri))
                await Launcher.LaunchUriAsync(uri);
        };

        var content = new StackPanel { Spacing = 14 };
        var scroll = new ScrollViewer
        {
            Content = content, MaxHeight = 545, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 6, 0)
        };
        modal.Body.Children.Add(scroll);

        content.Children.Add(BuildHero(project, instance, installedVersion));
        if (project.Gallery.Count > 0) content.Children.Add(BuildGallery(project, modal));
        var zhIntro = new TextBlock
        {
            Text = "正在翻译简介…", FontSize = 12, LineHeight = 21, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC7, 0xD3, 0xE6))
        };
        content.Children.Add(SectionTitle("中文简介"));
        content.Children.Add(zhIntro);
        _ = TranslateIntroAsync(zhIntro, Clean(project.Description + "\n\n" + project.Body));
        content.Children.Add(SectionTitle("English original"));
        content.Children.Add(new TextBlock
        {
            Text = Clean(project.Description + "\n\n" + project.Body),
            FontSize = 12, LineHeight = 21, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC7, 0xD3, 0xE6))
        });

        var dependencyText = new TextBlock
        {
            Text = "正在读取依赖…", FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Muted)
        };
        content.Children.Add(SectionTitle("必需依赖"));
        content.Children.Add(dependencyText);

        content.Children.Add(SectionTitle("版本与文件"));
        var versionTitle = new TextBlock
        {
            Text = "正在读取全部版本…", FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Text), TextTrimming = TextTrimming.CharacterEllipsis
        };
        var versionSub = new TextBlock
        {
            Text = "点击进入版本选择，加载时会显示骨架动画。",
            FontSize = 10, Foreground = new SolidColorBrush(Faint), TextWrapping = TextWrapping.Wrap
        };
        var versionCopy = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        versionCopy.Children.Add(versionTitle);
        versionCopy.Children.Add(versionSub);
        var versionGrid = new Grid { ColumnSpacing = 10 };
        versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        versionGrid.Children.Add(versionCopy);
        var versionChevron = new FontIcon
        {
            Glyph = "\uE76C", FontSize = 11, Foreground = new SolidColorBrush(Muted),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(versionChevron, 1);
        versionGrid.Children.Add(versionChevron);
        var versionButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = 58, Padding = new Thickness(13, 8, 13, 8),
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            IsEnabled = true,
            Content = versionGrid
        };
        var fileBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = 40,
            PlaceholderText = "选择文件", IsEnabled = false, Margin = new Thickness(0, 8, 0, 0)
        };
        var dependenciesToggle = new ToggleSwitch
        {
            Header = "同时安装必需依赖（推荐）", IsOn = true, Margin = new Thickness(0, 4, 0, 0)
        };
        var fileInfo = new TextBlock
        {
            FontSize = 10.5, Foreground = new SolidColorBrush(Faint), TextWrapping = TextWrapping.Wrap
        };
        var selectedFolder = instance is null
            ? ""
            : Path.Combine(instance.GameDir, ModrinthService.FolderFor(project.ProjectType));
        var folderText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(selectedFolder) ? "尚未选择保存位置" : selectedFolder,
            FontSize = 10, Foreground = new SolidColorBrush(string.IsNullOrWhiteSpace(selectedFolder) ? Faint : Muted),
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var folderButton = new Button
        {
            Content = "更改", Height = 30, MinWidth = 62, Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC7, 0xD3, 0xE6))
        };
        var folderGrid = new Grid { ColumnSpacing = 8 };
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderGrid.Children.Add(folderText);
        Grid.SetColumn(folderButton, 1);
        folderGrid.Children.Add(folderButton);
        var progress = new ProgressBar
        {
            Height = 5, IsIndeterminate = false, Value = 0,
            Foreground = new SolidColorBrush(Mint),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(3), Visibility = Visibility.Collapsed
        };
        var status = new TextBlock
        {
            Text = installedVersion is null ? "选择版本后即可安装。" : "当前已安装 · " + installedVersion,
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Muted)
        };
        content.Children.Add(versionButton);
        content.Children.Add(fileBox);
        content.Children.Add(fileInfo);
        content.Children.Add(SectionTitle("保存位置"));
        content.Children.Add(folderGrid);
        content.Children.Add(dependenciesToggle);
        content.Children.Add(progress);
        content.Children.Add(status);

        var versions = new List<ModrinthVersion>();
        Task<IReadOnlyList<ModrinthVersion>>? versionsTask = null;
        var currentVersion = default(ModrinthVersion);
        var currentFile = default(ModrinthFile);

        void RefreshFiles()
        {
            fileBox.Items.Clear();
            currentFile = null;
            if (currentVersion is null) return;
            foreach (var file in currentVersion.Files)
            {
                fileBox.Items.Add(new ComboBoxItem
                {
                    Content = file.Filename + " · " + FormatSize(file.Size) + (file.Primary ? " · 主要" : ""),
                    Tag = file
                });
            }
            if (fileBox.Items.Count > 0)
            {
                var index = currentVersion.Files.FindIndex(f => f.Primary);
                fileBox.SelectedIndex = Math.Max(0, index);
                currentFile = currentVersion.Files[Math.Max(0, index)];
            }
            fileBox.IsEnabled = fileBox.Items.Count > 0;
        }

        void RefreshVersionSummary()
        {
            if (currentVersion is null)
            {
                versionTitle.Text = "选择版本";
                versionSub.Text = "按游戏版本与加载器折叠浏览全部版本。";
                return;
            }
            var type = string.IsNullOrWhiteSpace(currentVersion.VersionType) ? "release" : currentVersion.VersionType;
            versionTitle.Text = currentVersion.VersionNumber + " · " + type;
            var compatible = VersionPickerDialog.IsCompatible(currentVersion, instance, project.ProjectType);
            versionSub.Text = "游戏 " + string.Join(" / ", currentVersion.GameVersions.Take(4))
                              + " · " + string.Join(" / ", currentVersion.Loaders)
                              + (compatible ? "" : " · 与当前实例不匹配");
        }

        versionButton.Click += async (_, _) =>
        {
            var picked = await VersionPickerDialog.ShowAsync(modal, versions, instance, project.ProjectType,
                currentVersion, installedVersion, async () =>
                {
                    versionsTask ??= LumenRuntime.Modrinth.VersionsAsync(project.ProjectId, "", "");
                    var loaded = (await versionsTask).ToList();
                    versions = loaded;
                    versionButton.IsEnabled = loaded.Count > 0;
                    return loaded;
                });
            if (picked is null) return;
            currentVersion = picked;
            RefreshFiles();
            RefreshVersionSummary();
            await LoadDependenciesAsync();
        };

        folderButton.Click += async (_, _) =>
        {
            var picked = await SaveLocationDialog.PickFolderAsync(selectedFolder);
            if (string.IsNullOrWhiteSpace(picked)) return;
            selectedFolder = picked;
            folderText.Text = picked;
            folderText.Foreground = new SolidColorBrush(Muted);
        };

        fileBox.SelectionChanged += (_, _) =>
        {
            currentFile = (fileBox.SelectedItem as ComboBoxItem)?.Tag as ModrinthFile;
            if (currentFile is not null)
                fileInfo.Text = currentFile.Filename + " · " + FormatSize(currentFile.Size);
        };

        async Task LoadVersionsAsync()
        {
            try
            {
                versionTitle.Text = "正在读取推荐版本…";
                versionSub.Text = "点击进入版本选择，加载全部版本时会显示骨架动画。";
                var game = project.ProjectType == "mod" ? instance?.Kernel ?? "" : "";
                var loader = project.ProjectType == "mod" ? LoaderTag(instance?.Loader) : "";
                var quick = await LumenRuntime.Modrinth.VersionsAsync(project.ProjectId, game, loader);
                if (quick.Count == 0)
                    quick = await LumenRuntime.Modrinth.VersionsAsync(project.ProjectId, "", "");
                versions = quick.ToList();
                versionButton.IsEnabled = versions.Count > 0;
                if (versions.Count == 0)
                {
                    versionTitle.Text = "没有可用版本";
                    versionSub.Text = "这个项目暂时没有公开下载文件。";
                    status.Text = "这个项目没有可下载版本。";
                    return;
                }

                var target = default(ModrinthVersion);
                if (!string.IsNullOrWhiteSpace(installedVersion))
                    target = versions.FirstOrDefault(v =>
                        string.Equals(v.VersionNumber, installedVersion, StringComparison.OrdinalIgnoreCase));
                target ??= versions
                    .Where(v => VersionPickerDialog.IsCompatible(v, instance, project.ProjectType))
                    .OrderBy(v => v.VersionType == "release" ? 0 : v.VersionType == "beta" ? 1 : 2)
                    .ThenByDescending(v => v.DatePublished)
                    .FirstOrDefault();
                target ??= versions.OrderByDescending(v => v.DatePublished).FirstOrDefault();
                if (target is not null)
                {
                    currentVersion = target;
                    RefreshFiles();
                    RefreshVersionSummary();
                    status.Text = "已选中推荐版本，点击上方可浏览全部版本。";
                    await LoadDependenciesAsync();
                }
            }
            catch (Exception ex)
            {
                versionButton.IsEnabled = false;
                versionTitle.Text = "读取版本失败";
                versionSub.Text = ex.Message;
                status.Text = "读取版本失败：" + ex.Message;
            }
        }

        async Task LoadDependenciesAsync()
        {
            try
            {
                if (currentVersion is null) return;
                var requirements = currentVersion.Dependencies
                    .Where(d => d.DependencyType == "required" && !string.IsNullOrWhiteSpace(d.ProjectId))
                    .Select(d => d.ProjectId!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (requirements.Count == 0) { dependencyText.Text = "无必需依赖。"; return; }
                var names = new List<string>();
                foreach (var id in requirements)
                {
                    try
                    {
                        var dep = await LumenRuntime.Modrinth.ProjectAsync(id);
                        if (dep is not null) names.Add(dep.Title);
                    }
                    catch { }
                }
                dependencyText.Text = names.Count > 0
                    ? "需要 " + names.Count + " 个依赖：" + string.Join("、", names)
                    : "需要 " + requirements.Count + " 个依赖，安装时会自动解析。";
            }
            catch (Exception ex) { dependencyText.Text = "依赖读取失败：" + ex.Message; }
        }

        async Task InstallAsync()
        {
            if (instance is null)
            {
                await MessageDialog.ShowAsync(root, "未选择实例", "请先在版本页创建一个实例，再安装内容。");
                return;
            }
            if (currentVersion is null || currentFile is null)
            {
                await MessageDialog.ShowAsync(root, "无法安装", "请选择可下载的版本与文件。");
                return;
            }
            var targetPath = await SaveLocationDialog.PickSaveFileAsync(selectedFolder, currentFile.Filename);
            if (string.IsNullOrWhiteSpace(targetPath)) return;
            var folder = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(folder)) folder = selectedFolder;
            selectedFolder = folder;
            folderText.Text = folder;
            folderText.Foreground = new SolidColorBrush(Muted);

            progress.Visibility = Visibility.Visible;
            progress.Value = 0;
            var reporter = new Progress<DownloadProgress>(p =>
            {
                progress.Value = Math.Clamp(p.Percent, 2, 96);
                status.Text = p.Stage + " · " + p.Message
                    + (p.BytesPerSecond > 0 ? " · " + (p.BytesPerSecond / 1048576.0).ToString("0.0") + " MB/s" : "");
            });
            try
            {
                Directory.CreateDirectory(folder);
                var path = await LumenRuntime.Modrinth.InstallToPathAsync(currentFile, targetPath, reporter);
                status.Text = "已安装 " + Path.GetFileName(path) + " 到 " + folder;
                progress.Value = 100;
                modal.PrimaryText = "再次安装";
                if (dependenciesToggle.IsOn)
                    await InstallDependenciesAsync(instance, currentVersion, status, progress, folder);
            }
            catch (Exception ex)
            {
                AppLog.Error("modrinth", "安装项目失败", ex);
                status.Text = "安装失败：" + ex.Message;
            }
        }

        if (readOnly)
        {
            modal.PrimaryText = "关闭";
            modal.PrimaryInvoked = () => { modal.Close(); return Task.CompletedTask; };
        }
        else
        {
            modal.PrimaryInvoked = InstallAsync;
        }
        await LoadVersionsAsync();
    }

    private static async Task InstallDependenciesAsync(InstanceSummary instance, ModrinthVersion version,
        TextBlock status, ProgressBar progress, string? targetFolder)
    {
        var requirements = version.Dependencies
            .Where(d => d.DependencyType == "required" && !string.IsNullOrWhiteSpace(d.ProjectId))
            .GroupBy(d => d.ProjectId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToList();
        var done = 0;
        foreach (var dependency in requirements)
        {
            try
            {
                var project = await LumenRuntime.Modrinth.ProjectAsync(dependency.ProjectId!);
                if (project is null) continue;
                var loader = project.ProjectType == "mod" ? LoaderTag(instance.Loader) : "";
                var versions = await LumenRuntime.Modrinth.VersionsAsync(project.ProjectId, instance.Kernel, loader);
                var candidate = versions.FirstOrDefault(v => v.VersionType == "release") ?? versions.FirstOrDefault();
                if (candidate is null) continue;
                var file = candidate.Files.FirstOrDefault(f => f.Primary) ?? candidate.Files.FirstOrDefault();
                if (file is null) continue;
                var folder = project.ProjectType == "mod" && !string.IsNullOrWhiteSpace(targetFolder)
                    ? targetFolder
                    : Path.Combine(instance.GameDir, ModrinthService.FolderFor(project.ProjectType));
                Directory.CreateDirectory(folder);
                done++;
                status.Text = "安装依赖 " + done + "/" + requirements.Count + " · " + project.Title;
                progress.Value = Math.Min(98, 80 + done * 18.0 / Math.Max(1, requirements.Count));
                await LumenRuntime.Modrinth.InstallFileAsync(project, candidate, file, folder);
            }
            catch (Exception ex)
            {
                AppLog.Warn("modrinth", "依赖安装失败：" + ex.Message);
            }
        }
        status.Text = requirements.Count == 0
            ? "安装完成，无额外依赖。"
            : "安装完成，已处理 " + done + "/" + requirements.Count + " 个依赖。";
        progress.Value = 100;
    }

    private static FrameworkElement BuildHero(ModrinthProject project, InstanceSummary? instance, string? installedVersion)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder = new Border
        {
            Width = 66, Height = 66, CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromArgb(0x24, 0x7C, 0xEB, 0xD6)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };
        if (!string.IsNullOrWhiteSpace(project.IconUrl))
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Width = 66, Height = 66 };
            image.Source = new BitmapImage(new Uri(project.IconUrl));
            iconBorder.Child = image;
        }
        else
        {
            iconBorder.Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(project.Title) ? "?" : project.Title[..1].ToUpperInvariant(),
                FontSize = 25, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Mint),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }
        grid.Children.Add(iconBorder);

        var copy = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock
        {
            Text = project.Title, FontSize = 21, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Text), TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(project.ZhTitle))
            copy.Children.Add(new TextBlock { Text = "中文名：" + project.ZhTitle, FontSize = 11, Foreground = new SolidColorBrush(Mint) });
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        foreach (var category in project.DisplayCategories.Concat(project.Categories).Distinct().Take(5))
            chips.Children.Add(Chip(CategoryLabel(category)));
        copy.Children.Add(chips);
        var stats = new TextBlock
        {
            Text = ShortNumber(project.Downloads) + " 下载 · " + ShortNumber(project.Followers) + " 关注 · "
                   + (project.License?.Name ?? "未标注许可") + (installedVersion is null ? "" : " · 已安装 " + installedVersion),
            FontSize = 10, Foreground = new SolidColorBrush(Faint), TextWrapping = TextWrapping.Wrap
        };
        copy.Children.Add(stats);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        var links = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        AddLink(links, "源码", project.SourceUrl);
        AddLink(links, "问题", project.IssuesUrl);
        AddLink(links, "Wiki", project.WikiUrl);
        Grid.SetColumn(links, 2);
        grid.Children.Add(links);
        return grid;
    }

    private static FrameworkElement BuildGallery(ModrinthProject project, GlassModal modal)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in project.Gallery.Take(5))
        {
            var image = new Image { Width = 150, Height = 84, Stretch = Stretch.UniformToFill };
            try { image.Source = new BitmapImage(new Uri(item.Url)); } catch { }
            var border = new Border
            {
                Width = 150, Height = 84, CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1), Child = image
            };
            border.Tapped += (_, _) => OpenGallery(modal, item.Url);
            panel.Children.Add(border);
        }
        return new ScrollViewer
        {
            Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto
        };
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text, FontSize = 9, CharacterSpacing = 160, Margin = new Thickness(0, 4, 0, -6),
        Foreground = new SolidColorBrush(Faint)
    };

    private static Border Chip(string text) => new()
    {
        CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 2, 7, 2),
        Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 9, Foreground = new SolidColorBrush(Muted) }
    };

    private static void AddLink(StackPanel panel, string label, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var button = new Button
        {
            Content = label, Height = 26, Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(8), FontSize = 9.5,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), Foreground = new SolidColorBrush(Muted)
        };
        button.Click += async (_, _) =>
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) await Launcher.LaunchUriAsync(uri);
        };
        panel.Children.Add(button);
    }

    public static string LoaderTag(string? loader) => loader?.ToLowerInvariant() switch
    {
        "fabric" => "fabric",
        "quilt" => "quilt",
        "forge" => "forge",
        "neoforge" => "neoforge",
        _ => ""
    };

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "未知大小";
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString("0.0") + " " + units[unit];
    }

    private static string ShortNumber(long value) => value switch
    {
        >= 100_000_000 => (value / 100_000_000.0).ToString("0.0") + "亿",
        >= 10_000 => (value / 10_000.0).ToString("0.0") + "万",
        _ => value.ToString()
    };

    private static async Task TranslateIntroAsync(TextBlock target, string source)
    {
        try
        {
            var translated = await LumenRuntime.Modrinth.TranslateToChineseAsync(source);
            target.Text = string.IsNullOrWhiteSpace(translated) ? "暂无中文简介。" : translated;
        }
        catch
        {
            target.Text = "暂无中文简介。";
        }
    }

    private static void OpenGallery(GlassModal modal, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x03, 0x07, 0x0D)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(48, 56, 48, 56)
        };
        image.Source = new BitmapImage(uri);
        overlay.Children.Add(image);
        var close = new Button
        {
            Content = "关闭预览", Height = 32, Padding = new Thickness(16, 0, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 52, 24, 0), CornerRadius = new CornerRadius(10), FontSize = 10.5
        };
        close.Click += (_, _) => modal.ClearOverlay();
        overlay.Children.Add(close);
        overlay.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay)) modal.ClearOverlay();
        };
        modal.ShowOverlay(overlay);
    }

    private static string CategoryLabel(string value) => value.ToLowerInvariant() switch
    {
        "optimization" => "优化",
        "technology" => "科技",
        "magic" => "魔法",
        "adventure" => "冒险",
        "decoration" => "装饰",
        "library" => "前置库",
        "worldgen" => "世界生成",
        "storage" => "存储",
        "equipment" => "装备",
        "mobs" => "生物",
        "utility" => "实用",
        "realistic" => "写实",
        "modern" => "现代",
        "medieval" => "中世纪",
        "themed" => "主题",
        "fonts" => "字体",
        "cartoon" => "卡通",
        "atmospheric" => "氛围",
        "vanilla" => "原版风",
        "low-end" => "低配",
        "kitchen-sink" => "整合",
        "quests" => "任务",
        "lightweight" => "轻量",
        _ => value
    };

    private static string Clean(string value)
    {
        // Decode first: Modrinth bodies contain escaped HTML (&lt;img&gt;), so tag removal
        // must happen after decoding or the raw markup leaks into the UI.
        var text = WebUtility.HtmlDecode(value ?? "");
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        for (var i = 0; i < 2; i++)
        {
            text = Regex.Replace(text, @"!\[[^\]]*\]\([^\)]*\)", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\[!\[[^\]]*\]\([^\)]*\)\]\([^\)]*\)", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]*\)", "$1", RegexOptions.Singleline);
            text = Regex.Replace(text, @"<[^>]*>", " ", RegexOptions.Singleline);
        }
        text = Regex.Replace(text, @"\[?\]\([^\)]*\)", " ");
        text = Regex.Replace(text, @"https?://\S+", " ");
        text = Regex.Replace(text, @"(?m)^\s{0,3}[#>*_~\x60-]+\s*", "");
        text = Regex.Replace(text, @"[#>*_~\x60]+", "");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}

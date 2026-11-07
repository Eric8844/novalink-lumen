using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NovaLink.Lumen;

/// <summary>Central light/dark palette. Brushes are mutated in place so StaticResource controls update without reloading pages.</summary>
public static class ThemeManager
{
    private static bool _initialized;

    public static bool IsLight { get; private set; }
    public static Color Accent { get; private set; } = Color.FromArgb(0xFF, 0x7C, 0xEB, 0xD6);
    public static event EventHandler? Changed;

    public static Color Background => IsLight ? Color.FromArgb(0xFF, 0xF2, 0xF6, 0xFA) : Color.FromArgb(0xFF, 0x04, 0x06, 0x0B);
    public static Color Text => IsLight ? Color.FromArgb(0xFF, 0x12, 0x1B, 0x27) : Color.FromArgb(0xFF, 0xF3, 0xF7, 0xFE);
    public static Color TextSecondary => IsLight ? Color.FromArgb(0xFF, 0x4D, 0x5F, 0x75) : Color.FromArgb(0xFF, 0x91, 0xA0, 0xB8);
    public static Color TextTertiary => IsLight ? Color.FromArgb(0xFF, 0x78, 0x8A, 0x9F) : Color.FromArgb(0xFF, 0x5E, 0x6B, 0x81);
    public static Color CardSurface => IsLight ? Color.FromArgb(0xFF, 0xF9, 0xFC, 0xFE) : Color.FromArgb(0xFA, 0x0A, 0x11, 0x1E);
    public static Color CardBorder => IsLight ? Color.FromArgb(0xD8, 0xC9, 0xD8, 0xE5) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
    public static Color Dim => IsLight ? Color.FromArgb(0x88, 0xD9, 0xE5, 0xEF) : Color.FromArgb(0xC4, 0x02, 0x04, 0x08);
    public static Color ControlFill => IsLight ? Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    public static Color ControlBorder => IsLight ? Color.FromArgb(0x28, 0x1B, 0x2B, 0x3A) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);

    public static void Initialize(string? theme, string? accent = null)
    {
        IsLight = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        Accent = ParseColor(accent) ?? DefaultAccent(IsLight);
        _initialized = true;
        ApplyResources();
    }

    public static void Apply(string? theme, string? accent = null)
    {
        var light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        var changed = !_initialized || light != IsLight;
        IsLight = light;
        if (ParseColor(accent) is { } parsed) Accent = parsed;
        _initialized = true;
        ApplyResources();
        MainWindow.Current?.ApplyTheme(light);
        Views.ShellPage.Current?.ApplyTheme(light);
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyAccent(string? accent)
    {
        if (ParseColor(accent) is not { } parsed) return;
        var changed = !parsed.Equals(Accent);
        Accent = parsed;
        ApplyResources();
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Returns the live theme brush for a resource key (so code-built dialogs follow light/dark
    /// switches without caching a frozen color), falling back to a solid brush of <paramref name="fallback"/>.
    /// </summary>
    public static Brush Brush(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources is { } r && TryFind(r, key, out var value) && value is Brush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }

    public static Brush BrushText => Brush("BrushText", Text);
    public static Brush BrushTextSecondary => Brush("BrushTextSecondary", TextSecondary);
    public static Brush BrushTextTertiary => Brush("BrushTextTertiary", TextTertiary);
    public static Brush BrushAccent => Brush("BrushMint", Accent);
    public static Brush BrushDanger => Brush("BrushDanger", IsLight ? Color.FromArgb(0xFF, 0xD1, 0x3F, 0x62) : Color.FromArgb(0xFF, 0xFF, 0x6B, 0x8A));
    public static Brush BrushCardBorder => Brush("BrushBorderSubtle", ControlBorder);
    public static Brush BrushGlassCard => Brush("BrushGlassCard", CardSurface);

    public static Color DefaultAccent(bool light) => light
        ? Color.FromArgb(0xFF, 0x0C, 0x94, 0x80)
        : Color.FromArgb(0xFF, 0x7C, 0xEB, 0xD6);

    private static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Equals("aurora", StringComparison.OrdinalIgnoreCase)) return null;
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            if (text.Length == 7)
                return Color.FromArgb(0xFF,
                    Convert.ToByte(text.Substring(1, 2), 16),
                    Convert.ToByte(text.Substring(3, 2), 16),
                    Convert.ToByte(text.Substring(5, 2), 16));
            if (text.Length == 9)
                return Color.FromArgb(
                    Convert.ToByte(text.Substring(1, 2), 16),
                    Convert.ToByte(text.Substring(3, 2), 16),
                    Convert.ToByte(text.Substring(5, 2), 16),
                    Convert.ToByte(text.Substring(7, 2), 16));
        }
        catch { }
        return null;
    }

    private static void ApplyResources()
    {
        var r = Application.Current.Resources;
        if (r is null) return;

        // Keep the picked hue, but nudge very bright accents darker in light mode so
        // accent-coloured text and 1px highlights stay readable on white glass.
        var accent = IsLight && Luminance(Accent) > 0.70 ? Shift(Accent, 0, 0.02, -0.20) : Accent;
        var accentLight = Shift(accent, -7, -0.04, 0.20);
        var accentAlt = Shift(accent, 24, 0.04, 0.10);
        var accentSky = Shift(accent, -32, 0.06, 0.24);

        SetSolid(r, "BrushBg", Background);
        SetSolid(r, "BrushText", Text);
        SetSolid(r, "BrushTextSecondary", TextSecondary);
        SetSolid(r, "BrushTextTertiary", TextTertiary);
        SetSolid(r, "BrushMint", accent);
        SetSolid(r, "BrushViolet", accentAlt);
        SetSolid(r, "BrushSky", accentSky);
        SetSolid(r, "BrushDanger", IsLight ? Color.FromArgb(0xFF, 0xD1, 0x3F, 0x62) : Color.FromArgb(0xFF, 0xFF, 0x6B, 0x8A));
        SetSolid(r, "BrushStroke", IsLight ? Color.FromArgb(0x1C, 0x15, 0x25, 0x36) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "BrushSurface", IsLight ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB3, 0x0A, 0x10, 0x1B));
        SetSolid(r, "BrushSurfaceRaised", IsLight ? Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xD0, 0x0E, 0x16, 0x24));

        SetSolid(r, "BrushFillSubtle", IsLight ? Color.FromArgb(0x0F, 0x12, 0x22, 0x32) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "BrushFillSoft", IsLight ? Color.FromArgb(0x0A, 0x12, 0x22, 0x32) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "BrushBorderSubtle", IsLight ? Color.FromArgb(0x1E, 0x15, 0x25, 0x36) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "BrushSliderTrack", IsLight ? Color.FromArgb(0x2E, 0x0E, 0x22, 0x33) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        // Amber used for "restart required" style notices is unreadable on a light surface, so the
        // token darkens in light mode instead of staying the dark-theme #FFC078.
        SetSolid(r, "BrushWarn", IsLight ? Color.FromArgb(0xFF, 0xB0, 0x6A, 0x00) : Color.FromArgb(0xFF, 0xFF, 0xC0, 0x78));
        SetSolid(r, "BrushWarnSoft", IsLight ? Color.FromArgb(0x26, 0xFF, 0xB8, 0x6B) : Color.FromArgb(0x33, 0xFF, 0xB8, 0x6B));
        SetSolid(r, "BrushField", IsLight ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "BrushFieldBorder", IsLight ? Color.FromArgb(0x26, 0x15, 0x25, 0x36) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "BrushHover", IsLight ? Color.FromArgb(0x10, 0x0E, 0x22, 0x33) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "BrushPressed", IsLight ? Color.FromArgb(0x1C, 0x0E, 0x22, 0x33) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "BrushSelected", WithAlpha(accent, IsLight ? (byte)0x2A : (byte)0x2E));
        SetSolid(r, "BrushSelectedHover", WithAlpha(accent, IsLight ? (byte)0x38 : (byte)0x3A));
        SetSolid(r, "BrushOnAccent", Luminance(accent) > 0.60
            ? Color.FromArgb(0xFF, 0x06, 0x10, 0x18)
            : Color.FromArgb(0xFF, 0xF7, 0xFB, 0xFF));
        SetSolid(r, "BrushCardTint", IsLight ? Color.FromArgb(0xF4, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x16, 0x21, 0x34));

        SetGradient(r, "BrushAccent", new[] { accent, accentLight, accentAlt });
        SetGradient(r, "BrushAccentSoft", new[] { WithAlpha(accent, 0x33), WithAlpha(accentAlt, 0x1F) });

        // 玻璃是一层薄白雾（Lens）：Win2D 已经把背景模糊好，XAML 这层只负责把对比拉稳，
        // 透明度压到 0.10~0.25，模糊面才透得出来；以前的 0.70~0.90 等于把模糊层全盖住。
        SetGradient(r, "BrushGlass", IsLight
            ? new[] { Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xA3, 0xF6, 0xFA, 0xFF), Color.FromArgb(0x8F, 0xEF, 0xF4, 0xFA) }
            : new[] { Color.FromArgb(0x4D, 0x0A, 0x10, 0x18), Color.FromArgb(0x3D, 0x08, 0x0D, 0x14), Color.FromArgb(0x2E, 0x06, 0x0A, 0x10) });
        SetGradient(r, "BrushGlassCard", IsLight
            ? new[] { Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x96, 0xF7, 0xFB, 0xFF), Color.FromArgb(0x85, 0xF1, 0xF6, 0xFB) }
            : new[] { Color.FromArgb(0x45, 0x0A, 0x10, 0x18), Color.FromArgb(0x36, 0x08, 0x0D, 0x14), Color.FromArgb(0x29, 0x06, 0x0A, 0x10) });
        SetGradient(r, "BrushGlassFaint", IsLight
            ? new[] { Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF) }
            : new[] { Color.FromArgb(0x2E, 0x08, 0x0D, 0x14), Color.FromArgb(0x16, 0x06, 0x0A, 0x10) });
        SetGradient(r, "BrushGlassRim", IsLight
            ? new[] { Color.FromArgb(0x6E, 0x1A, 0x2B, 0x3B), Color.FromArgb(0x38, 0x1A, 0x2B, 0x3B), Color.FromArgb(0x1C, 0x1A, 0x2B, 0x3B), Color.FromArgb(0x0C, 0x1A, 0x2B, 0x3B) }
            : new[] { Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) });
        // 内缘折射环 + 外缘暗边（Lens 的 .lg-optic-rim）：Win2D 侧用 AuroraBackdrop 调色板的对应数值绘制，
        // 这里同步令牌。浅色主题把白边换成冷灰蓝，否则白底上的亮边会糊成一片没有轮廓。
        SetGradient(r, "BrushGlassEdge", IsLight
            ? new[] { Color.FromArgb(0x52, 0x1A, 0x2B, 0x3B), Color.FromArgb(0x1C, 0x1A, 0x2B, 0x3B), Color.FromArgb(0x08, 0x1A, 0x2B, 0x3B) }
            : new[] { Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x21, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) });
        SetSolid(r, "BrushGlassEdgeOuter", IsLight ? Color.FromArgb(0x14, 0x1A, 0x2B, 0x3B) : Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        SetGradient(r, "BrushNavSelected", new[]
        {
            WithAlpha(accent, IsLight ? (byte)0x3A : (byte)0x3D),
            WithAlpha(accentAlt, IsLight ? (byte)0x1C : (byte)0x1F)
        });
        SetSolid(r, "BrushNavSelectedBorder", WithAlpha(accent, IsLight ? (byte)0x52 : (byte)0x4D));
        SetSolid(r, "BrushNavHover", IsLight ? Color.FromArgb(0x10, 0x0E, 0x22, 0x33) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        // Control theme resources live in the merged theme dictionary and are resolved when a
        // popup presenter is created. They used to point at the static {StaticResource ColorText},
        // which is frozen at the dark-mode value, so every menu/combo popup kept white text on a
        // white light-mode surface (invisible items). Keep them in sync with the active theme.
        SetSolid(r, "MenuFlyoutItemForeground", Text);
        SetSolid(r, "MenuFlyoutItemForegroundPointerOver", Text);
        SetSolid(r, "ComboBoxForeground", Text);
        SetSolid(r, "ComboBoxForegroundPointerOver", Text);
        SetSolid(r, "ComboBoxDropDownForeground", Text);
        SetSolid(r, "ComboBoxItemForegroundSelected", Text);
        SetSolid(r, "ComboBoxItemPillFillBrush", accent);
        SetColorKey(r, "ColorText", Text);
        SetColorKey(r, "ColorTextSecondary", TextSecondary);
        SetColorKey(r, "ColorTextTertiary", TextTertiary);
        SetColorKey(r, "ColorMint", accent);

        SetSolid(r, "MenuFlyoutPresenterBorderBrush", IsLight ? Color.FromArgb(0x26, 0x15, 0x25, 0x36) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "MenuFlyoutItemBackgroundPointerOver", IsLight ? Color.FromArgb(0x10, 0x0E, 0x22, 0x33) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "MenuFlyoutItemBackgroundPressed", IsLight ? Color.FromArgb(0x1C, 0x0E, 0x22, 0x33) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "ComboBoxDropDownBorderBrush", IsLight ? Color.FromArgb(0x26, 0x15, 0x25, 0x36) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "ComboBoxBackground", IsLight ? Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "ComboBoxBackgroundPointerOver", IsLight ? Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "ComboBoxBackgroundPressed", IsLight ? Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "ComboBoxBorderBrush", IsLight ? Color.FromArgb(0x26, 0x15, 0x25, 0x36) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "ComboBoxBorderBrushPointerOver", IsLight ? Color.FromArgb(0x4A, 0x15, 0x25, 0x36) : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "ComboBoxItemBackgroundPointerOver", IsLight ? Color.FromArgb(0x10, 0x0E, 0x22, 0x33) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        SetSolid(r, "ComboBoxItemBackgroundSelected", WithAlpha(accent, IsLight ? (byte)0x2A : (byte)0x2E));
        SetSolid(r, "ComboBoxItemBackgroundSelectedPointerOver", WithAlpha(accent, IsLight ? (byte)0x38 : (byte)0x3A));

        SetGradient(r, "BrushGlassHighlight", IsLight
            ? new[] { Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF) }
            : new[] { Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF) });

        // 弹窗顶部柔光：浅色下用冷一点的白，深色下用中性白，两端都淡到全透明。
        SetGradient(r, "BrushGlassTopBand", IsLight
            ? new[] { Color.FromArgb(0x46, 0xEC, 0xF4, 0xFF), Color.FromArgb(0x18, 0xEC, 0xF4, 0xFF), Color.FromArgb(0x00, 0xEC, 0xF4, 0xFF) }
            : new[] { Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF) });

        SetAcrylic(r, "BrushSearchGlass", IsLight ? Color.FromArgb(0xFF, 0xF7, 0xFA, 0xFD) : Color.FromArgb(0xFF, 0x0A, 0x11, 0x1D), IsLight ? 0.76 : 0.58);
        // Slightly more transparent in light mode so the frosted blur still reads as glass
        // instead of a flat white panel.
        SetAcrylic(r, "BrushGlassLiquid", IsLight ? Color.FromArgb(0xFF, 0xF4, 0xF8, 0xFC) : Color.FromArgb(0xFF, 0x0A, 0x11, 0x1E), IsLight ? 0.82 : 0.62);
        // Light mode used to sit at 0.88-0.90 opacity, which rendered menus as flat opaque white
        // sheets. Dropping it lets the frosted blur read through without hurting legibility.
        SetAcrylic(r, "ComboBoxDropDownBackground", IsLight ? Color.FromArgb(0xFF, 0xF7, 0xFA, 0xFD) : Color.FromArgb(0xFF, 0x0A, 0x11, 0x1E), IsLight ? 0.78 : 0.72);
        SetAcrylic(r, "MenuFlyoutPresenterBackground", IsLight ? Color.FromArgb(0xFF, 0xF7, 0xFA, 0xFD) : Color.FromArgb(0xFF, 0x0A, 0x11, 0x1E), IsLight ? 0.74 : 0.80);
    }

    private static bool TryFind(ResourceDictionary root, string key, out object? value)
    {
        if (root.ContainsKey(key))
        {
            value = root[key];
            return true;
        }
        foreach (var merged in root.MergedDictionaries)
            if (TryFind(merged, key, out value)) return true;
        foreach (var theme in root.ThemeDictionaries.Values)
            if (theme is ResourceDictionary dictionary && TryFind(dictionary, key, out value)) return true;
        value = null;
        return false;
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    internal static double Luminance(Color color)
        => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    internal static Color Shift(Color color, double hueDelta, double saturationDelta, double lightnessDelta)
    {
        RgbToHsl(color, out var h, out var s, out var l);
        h = (h + hueDelta / 360.0 + 1.0) % 1.0;
        s = Math.Clamp(s + saturationDelta, 0.0, 1.0);
        l = Math.Clamp(l + lightnessDelta, 0.0, 1.0);
        return HslToRgb(h, s, l);
    }

    private static void RgbToHsl(Color color, out double h, out double s, out double l)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2.0;
        if (Math.Abs(max - min) < 0.00001)
        {
            h = 0;
            s = 0;
            return;
        }
        var d = max - min;
        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6.0;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        if (s <= 0.00001)
        {
            var v = (byte)Math.Round(l * 255);
            return Color.FromArgb(0xFF, v, v, v);
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromArgb(0xFF, Channel(p, q, h + 1.0 / 3.0), Channel(p, q, h), Channel(p, q, h - 1.0 / 3.0));
    }

    private static byte Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        double value;
        if (t < 1.0 / 6.0) value = p + (q - p) * 6 * t;
        else if (t < 1.0 / 2.0) value = q;
        else if (t < 2.0 / 3.0) value = p + (q - p) * (2.0 / 3.0 - t) * 6;
        else value = p;
        return (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255);
    }

    /// <summary>
    /// Updates a raw <c>Color</c> resource. Brushes created from it are frozen copies, so every
    /// brush that derives from a color key must also be refreshed explicitly (see ApplyResources).
    /// </summary>
    private static void SetColorKey(ResourceDictionary r, string key, Color color)
    {
        if (TryFind(r, key, out var value) && value is Color) r[key] = color;
    }

    private static void SetSolid(ResourceDictionary r, string key, Color color)
    {
        if (!TryFind(r, key, out var value) || value is not SolidColorBrush brush) return;
        brush.Color = color;
    }

    private static void SetGradient(ResourceDictionary r, string key, Color[] colors)
    {
        if (!TryFind(r, key, out var value) || value is not LinearGradientBrush brush) return;
        for (var i = 0; i < brush.GradientStops.Count && i < colors.Length; i++)
            brush.GradientStops[i].Color = colors[i];
    }

    private static void SetAcrylic(ResourceDictionary r, string key, Color tint, double opacity)
    {
        if (!TryFind(r, key, out var value) || value is not AcrylicBrush brush) return;
        try
        {
            brush.TintColor = tint;
            brush.TintOpacity = opacity;
            brush.FallbackColor = Color.FromArgb(0xFF, tint.R, tint.G, tint.B);
        }
        catch { }
    }
}

using Windows.UI;

namespace NovaLink.Lumen.Controls;

public enum GlassQuality
{
    Auto,
    High,
    Balanced,
    Light,
    Saver,
    Off
}

/// <summary>
/// 液态玻璃参数集，对应迁移方案 3.7。默认值保持当前产品观感。
/// </summary>
public sealed class GlassSettings
{
    public GlassQuality Quality { get; set; } = GlassQuality.Auto;
    public double RefThickness { get; set; } = 1.0;
    public double RefFactor { get; set; } = 1.0;
    public double RefDispersion { get; set; } = 1.0;
    public double DispersionRange { get; set; } = 0.6;
    public double FresnelRange { get; set; } = 0.5;
    public double FresnelHardness { get; set; } = 0.55;
    public double FresnelFactor { get; set; } = 0.85;
    public double GlareRange { get; set; } = 0.5;
    public double GlareHardness { get; set; } = 0.6;
    public double GlareFactor { get; set; } = 0.7;
    public double GlareAngle { get; set; } = 35;
    public double BlurAmount { get; set; } = 12;
    public double Exposure { get; set; } = 1.0;
    public byte TintR { get; set; } = 0x28;
    public byte TintG { get; set; } = 0x34;
    public byte TintB { get; set; } = 0x40;
    public byte TintA { get; set; } = 0x14;
    public double ShapeRadius { get; set; } = 24;
    public double Dpr { get; set; } = 1.0;

    public Color Tint => Color.FromArgb(TintA, TintR, TintG, TintB);

    public GlassSettings Clone() => (GlassSettings)MemberwiseClone();
}

/// <summary>一张需要折射玻璃的卡片，坐标使用窗口 DIP。</summary>
public readonly record struct GlassPane(double X, double Y, double Width, double Height, double Radius, double Strength = 1.0);
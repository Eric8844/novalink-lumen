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
/// 玻璃材质参数集。v1.5.0 之后的 Lens 风格只保留「一次模糊 + 薄雾 + 边缘高光 + 镜面扫光」，
/// 折射强度/色散/位移厚度这几个旋钮已经删掉（渲染路径里不再有 DisplacementMapEffect）。
/// </summary>
public sealed class GlassSettings
{
    // 初值取启动时算好的挡位（glassQuality=auto 时会先检测硬件），见 HardwareProfile.cs 里的 GlassDefaults。
    public GlassQuality Quality { get; set; } = GlassDefaults.Quality;
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

/// <summary>一张需要玻璃底纹的卡片（窗口 DIP 坐标）。Animated 仅为兼容旧调用点保留，渲染已不区分；
/// Hover 0/1：鼠标停在这张卡上时边缘高光加亮（替代会跟手的光斑层）。</summary>
public readonly record struct GlassPane(double X, double Y, double Width, double Height, double Radius, double Strength = 1.0, bool Animated = false, double Hover = 0);
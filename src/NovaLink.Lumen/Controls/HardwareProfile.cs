using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// 自动画质挡位。由 CPU 核数、物理内存、显卡与分辨率综合评分得出；
/// 环境变量 NOVALINK_GLASS_TIER 可以强制指定（排障与低配机器用，取值 saver/performance/balanced/high）。
/// </summary>
public enum GlassTier
{
    High,
    Balanced,
    Performance,
    Saver
}

/// <summary>
/// 设置页可选的画质。保存进 settings.json 的字符串见 <see cref="GlassPresets.ToSetting"/>：
/// auto / high / balanced / performance / off，旧的 High / Balanced / Light / Saver / Off 也能读。
/// </summary>
public enum GlassPreset
{
    Auto,
    High,
    Balanced,
    Performance,
    Off
}

/// <summary>
/// 一次硬件检测的结果。<see cref="Steps"/> 是设置页滚动展示的中文步骤，
/// <see cref="Summary"/> 是结果 chip 里的短描述（核数 · 内存 · 显卡）。
/// </summary>
public sealed record HardwareReport(
    GlassTier Tier,
    int Score,
    string Cpu,
    string Gpu,
    long RamMb,
    int Width,
    int Height,
    IReadOnlyList<string> Steps)
{
    /// <summary>逻辑处理器数量（Environment.ProcessorCount，含超线程）。</summary>
    public int Cores { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>挡位的中文名，日志与设置页 chip 都用它。</summary>
    public string TierLabel => Tier switch
    {
        GlassTier.High => "高质量",
        GlassTier.Balanced => "平衡",
        GlassTier.Performance => "性能",
        _ => "省电"
    };

    /// <summary>内存按 1024 MB = 1 GB 四舍五入；读不到时如实说「内存未知」，不猜数字。</summary>
    public string GbText => RamMb <= 0 ? "内存未知" : Math.Round(RamMb / 1024.0) + " GB";

    /// <summary>结果 chip 用，例如「8 核 · 16 GB · NVIDIA GeForce RTX 3060」。</summary>
    public string Summary => Cores + " 核 · " + GbText + " · " + Gpu;

    /// <summary>日志用，例如「评分 9 · 16 GB · AMD Ryzen 7 5800H · RTX 3060 · 2560 × 1440」。</summary>
    public string Describe => "评分 " + Score + " · " + GbText + " · " + Cpu + " · " + Gpu + " · " + Width + " × " + Height;

    /// <summary>该挡位对应的玻璃参数档（启动链与设置页共用这套映射）。</summary>
    public GlassQuality Quality => HardwareProfile.ToQuality(Tier);
}

/// <summary>
/// 硬件检测与画质挡位映射。检测全部走 Win32 / 注册表，不依赖 WinUI，
/// 因此可以在主窗口创建之前调用；正常机器上耗时不到 300ms，失败也只是降档，不抛异常。
/// </summary>
public static class HardwareProfile
{
    /// <summary>强制指定挡位的环境变量名（saver/performance/balanced/high）。</summary>
    public const string OverrideVariable = "NOVALINK_GLASS_TIER";

    private const string UnknownGpu = "未知显卡";
    private const string UnknownCpu = "未知处理器";

    // 显卡类注册表路径：{4d36e968-e325-11ce-bfc1-08002be10318} 是显示适配器的固定 GUID。
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// 检测一次硬件并给出挡位。每一小步都通过 <paramref name="progress"/> 报一句中文，
    /// 设置页用它滚动展示「正在检测处理器…」这类文字；启动链传 null。
    /// </summary>
    public static HardwareReport Detect(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<string>(6);

        progress?.Report("正在检测处理器…");
        ct.ThrowIfCancellationRequested();
        var cores = Math.Max(1, Environment.ProcessorCount);
        var cpu = ReadCpuName();
        steps.Add("处理器：" + cpu + "（" + cores + " 核）");

        progress?.Report("正在检测显卡…");
        ct.ThrowIfCancellationRequested();
        var gpu = ReadGpuName();
        var discrete = LooksDiscrete(gpu);
        steps.Add("显卡：" + gpu + (discrete ? "（独立显卡）" : "（集成显卡或未识别）"));

        progress?.Report("正在检测内存…");
        ct.ThrowIfCancellationRequested();
        var ramMb = ReadTotalMemoryMb();
        steps.Add("内存：" + (ramMb <= 0 ? "读取失败" : Math.Round(ramMb / 1024.0) + " GB"));

        progress?.Report("正在计算挡位…");
        ct.ThrowIfCancellationRequested();
        var width = Math.Max(0, GetSystemMetrics(SystemMetricCxScreen));
        var height = Math.Max(0, GetSystemMetrics(SystemMetricCyScreen));
        steps.Add("分辨率：" + (width > 0 && height > 0 ? width + " × " + height : "未知"));

        var ramGb = ramMb / 1024.0;
        var score = Score(cores, ramGb, discrete);
        var tier = ResolveTier(cores, ramGb, discrete);

        // 环境变量优先级最高：低配机器或渲染出问题时不改代码也能锁挡位。
        var forced = ReadOverride();
        if (forced is { } forcedTier)
        {
            tier = forcedTier;
            steps.Add("挡位：" + TierLabel(tier) + "（环境变量 " + OverrideVariable + " 指定）");
        }
        else
        {
            steps.Add("挡位：" + TierLabel(tier) + "（评分 " + score + "）");
        }

        progress?.Report("检测完成 · 建议使用" + TierLabel(tier) + "档");
        return new HardwareReport(tier, score, cpu, gpu, ramMb, width, height, steps) { Cores = cores };
    }

    /// <summary>挡位对应的玻璃参数档：性能档用 Light（0.55 采样），省电档沿用它自己的 0.50。</summary>
    public static GlassQuality ToQuality(GlassTier tier) => tier switch
    {
        GlassTier.High => GlassQuality.High,
        GlassTier.Balanced => GlassQuality.Balanced,
        GlassTier.Performance => GlassQuality.Light,
        _ => GlassQuality.Saver
    };

    /// <summary>挡位中文名（日志、提示都用它，避免各处各写一套）。</summary>
    public static string TierLabel(GlassTier tier) => tier switch
    {
        GlassTier.High => "高质量",
        GlassTier.Balanced => "平衡",
        GlassTier.Performance => "性能",
        _ => "省电"
    };

    /// <summary>读取 NOVALINK_GLASS_TIER；没设置或不认识返回 null。</summary>
    public static GlassTier? ReadOverride()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(OverrideVariable);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return raw.Trim().ToLowerInvariant() switch
            {
                "high" or "quality" => GlassTier.High,
                "balanced" or "balance" => GlassTier.Balanced,
                "performance" or "perf" => GlassTier.Performance,
                "saver" or "save" => GlassTier.Saver,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 评分与判定
    // ------------------------------------------------------------------

    /// <summary>
    /// 挡位判定，规则故意写得直白（与设计文档一致）：
    /// 3 核及以下或内存不足 8 GB → 性能；8 核以上且 16 GB 以上 → 高质量；其余 → 平衡。
    /// 独立显卡只影响评分展示，不单独抬挡位（核显也能跑出很好的玻璃效果）。
    /// </summary>
    private static GlassTier ResolveTier(int cores, double ramGb, bool discrete)
    {
        var lowRam = ramGb > 0 && ramGb < 8;
        if (cores <= 3 || lowRam) return GlassTier.Performance;
        if (cores >= 8 && ramGb >= 16) return GlassTier.High;
        return GlassTier.Balanced;
    }

    /// <summary>0~10 的综合分，只用于展示与日志，不参与挡位判定。</summary>
    private static int Score(int cores, double ramGb, bool discrete)
    {
        int score;
        if (cores >= 12) score = 5;
        else if (cores >= 8) score = 4;
        else if (cores >= 6) score = 3;
        else if (cores >= 4) score = 2;
        else score = 1;

        if (ramGb >= 32) score += 4;
        else if (ramGb >= 16) score += 3;
        else if (ramGb >= 8) score += 2;
        else score += 1;

        if (discrete) score += 1;
        return Math.Clamp(score, 1, 10);
    }

    /// <summary>
    /// 粗略判断独显：按名字命中常见独显系列。只用于展示与评分，
    /// 判错不会影响挡位（挡位只看核数与内存）。
    /// </summary>
    private static bool LooksDiscrete(string gpu)
    {
        if (string.IsNullOrWhiteSpace(gpu)) return false;
        var name = gpu.ToLowerInvariant();
        if (name.Contains("intel") && !name.Contains("arc")) return false;
        return name.Contains("nvidia")
            || name.Contains("geforce")
            || name.Contains("rtx")
            || name.Contains("gtx")
            || name.Contains("quadro")
            || name.Contains("radeon")
            || name.Contains("rx ")
            || name.Contains("arc")
            || name.Contains("apple m");
    }

    // ------------------------------------------------------------------
    // 平台读数
    // ------------------------------------------------------------------

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
                return name.Trim();
        }
        catch
        {
            // 读注册表失败不影响挡位，退回「未知处理器」。
        }

        return UnknownCpu;
    }

    /// <summary>取第一张有 DriverDesc 的显示适配器（注册表子项名是 0000、0001 这类四位数字）。</summary>
    private static string ReadGpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (key is null) return UnknownGpu;
            foreach (var subName in key.GetSubKeyNames())
            {
                if (subName.Length != 4 || !int.TryParse(subName, out _)) continue;
                using var sub = key.OpenSubKey(subName);
                if (sub?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
                    return desc.Trim();
            }
        }
        catch
        {
            // 同上：拿不到显卡名只是少一行展示。
        }

        return UnknownGpu;
    }

    /// <summary>物理内存总量（MB）。GlobalMemoryStatusEx 返回的是含保留区的 totalPhys，正好是用户认知的「内存」。</summary>
    private static long ReadTotalMemoryMb()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhys > 0)
                return (long)(status.TotalPhys / (1024UL * 1024UL));
        }
        catch
        {
            // 取不到就返回 0，上层显示「内存未知」并按核数判挡。
        }

        return 0;
    }

    private const int SystemMetricCxScreen = 0;
    private const int SystemMetricCyScreen = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

/// <summary>
/// 设置页五挡与「保存字符串」之间的换算。老版本存过 High / Balanced / Light / Saver / Off，
/// 这里统一吸收：Light 与 Saver 都归到「性能」挡，读旧设置不会丢，也不会报错。
/// </summary>
public static class GlassPresets
{
    /// <summary>解析 settings.json 里的字符串；认不出来一律当「自动」。</summary>
    public static GlassPreset Parse(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "high" or "quality" => GlassPreset.High,
        "balanced" or "balance" => GlassPreset.Balanced,
        "performance" or "perf" or "light" or "saver" => GlassPreset.Performance,
        "off" or "none" or "closed" => GlassPreset.Off,
        _ => GlassPreset.Auto
    };

    /// <summary>写回 settings.json 的规范字符串。</summary>
    public static string ToSetting(GlassPreset preset) => preset switch
    {
        GlassPreset.High => "high",
        GlassPreset.Balanced => "balanced",
        GlassPreset.Performance => "performance",
        GlassPreset.Off => "off",
        _ => "auto"
    };

    /// <summary>由运行时挡位反推设置项（设置页按当前玻璃状态回显用）。</summary>
    public static GlassPreset FromQuality(GlassQuality quality) => quality switch
    {
        GlassQuality.High => GlassPreset.High,
        GlassQuality.Balanced => GlassPreset.Balanced,
        GlassQuality.Light or GlassQuality.Saver => GlassPreset.Performance,
        GlassQuality.Off => GlassPreset.Off,
        _ => GlassPreset.Auto
    };

    /// <summary>设置页胶囊上的短标签。</summary>
    public static string Label(GlassPreset preset) => preset switch
    {
        GlassPreset.High => "质量",
        GlassPreset.Balanced => "平衡",
        GlassPreset.Performance => "性能",
        GlassPreset.Off => "关闭",
        _ => "自动"
    };

    /// <summary>
    /// 设置项 → 玻璃参数档。自动挡需要 <paramref name="report"/>（硬件检测结果）；
    /// 没有检测结果时退回 Auto（AuroraBackdrop 会按窗口面积与帧率自己调，永远不会黑屏）。
    /// </summary>
    public static GlassQuality Quality(GlassPreset preset, HardwareReport? report) => preset switch
    {
        GlassPreset.High => GlassQuality.High,
        GlassPreset.Balanced => GlassQuality.Balanced,
        GlassPreset.Performance => GlassQuality.Light,
        GlassPreset.Off => GlassQuality.Off,
        _ => report is null ? GlassQuality.Auto : HardwareProfile.ToQuality(report.Tier)
    };
}

/// <summary>
/// 启动时算出的默认玻璃挡位。GlassSettings 的 Quality 初值读它，
/// 这样 AuroraBackdrop 在创建主窗口时（启动链已经跑完自动检测）就能直接拿到正确挡位，
/// 不用改 App / ShellPage 的装配顺序。
/// </summary>
public static class GlassDefaults
{
    /// <summary>本次启动要用的玻璃挡位；由 LumenRuntime.InitializeGlassProfile 在创建窗口前写入。</summary>
    public static GlassQuality Quality { get; set; } = GlassQuality.Auto;
}

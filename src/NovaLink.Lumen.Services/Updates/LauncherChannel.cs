namespace NovaLink.Lumen.Services.Updates;

/// <summary>
/// 发布通道。编译期由 csproj 的 NovaLinkChannel 属性决定（Preview 时会加 NOVALINK_PREVIEW 常量），
/// 运行期读不出别的值，也不会被配置文件或环境变量改掉。
/// </summary>
public enum LauncherChannel
{
    /// <summary>正式版：只从正式版仓库收纯数字版本。</summary>
    Stable,

    /// <summary>测试版：只从测试版仓库收带 -preview / -beta / -rc 的版本。</summary>
    Preview
}

/// <summary>
/// 通道与版本显示的唯一入口。更新器、关于页、安装器文案都从这里取值，避免各处写死。
/// 发布正式版必须用 -p:NovaLinkChannel=Stable 构建；漏加会让客户端自认测试版，
/// 只查测试版仓库，永远收不到正式版更新。
/// </summary>
public static class LauncherChannelInfo
{
    /// <summary>数值版本号（比较与 User-Agent 用，不带 Preview 字样）。</summary>
    public const string BaseVersion = "1.5.0";

    /// <summary>当前编译通道。</summary>
    public static LauncherChannel Current
    {
        get
        {
#if NOVALINK_PREVIEW
            return LauncherChannel.Preview;
#else
            return LauncherChannel.Stable;
#endif
        }
    }

    /// <summary>当前是不是测试版。</summary>
    public static bool IsPreview => Current == LauncherChannel.Preview;

    /// <summary>界面显示用版本号：正式版 "1.5.0"，测试版 "1.5.0 Preview"。</summary>
    public static string DisplayVersion => IsPreview ? BaseVersion + " Preview" : BaseVersion;

    /// <summary>通道中文名，用于徽章与日志。</summary>
    public static string ChannelLabel => IsPreview ? "测试版" : "正式版";

    /// <summary>
    /// 更新源仓库（owner/name）。测试版与正式版各一个仓库，互不交叉：
    /// 测试版仓库只放 pre-release 安装包与更新日志，正式版仓库只放正式版。
    /// </summary>
    public static string Repository => IsPreview
        ? "Eric8844/novalink-lumen-preview"
        : "Eric8844/novalink-lumen-releases";
}

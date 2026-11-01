using Jint;

namespace NovaLink.Lumen.Services.Plugins.Scripting;

/// <summary>
/// 类型 B（脚本插件）的 Jint 引擎工厂与沙箱参数。所有脚本插件共用同一套限制：
/// 关闭 CLR 互操作、限制语句数/内存/单次执行时间、禁止字符串编译（eval / new Function）。
/// </summary>
internal static class ScriptEngineFactory
{
    /// <summary>单次执行允许的最大语句数（约等于死循环保护的第一道闸）。</summary>
    internal const int MaxStatements = 500_000;

    /// <summary>单次执行允许分配的最大内存（32 MB）。</summary>
    internal const long MaxMemoryBytes = 32L * 1024 * 1024;

    /// <summary>单次执行的最长时间（2 秒）。</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(2000);

    /// <summary>递归深度上限，避免脚本用无限递归绕开语句计数前先爆栈。</summary>
    internal const int MaxRecursionDepth = 64;

    /// <summary>单个脚本文件的大小上限（2 MB），防止把整本大文件当脚本读进内存。</summary>
    internal const int MaxScriptFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 创建一个受限制的 Jint 引擎。
    /// 注意：Jint 4.x 关闭 CLR 访问只有一个有效开关 <c>options.Interop.Enabled = false</c>（没有 AllowClr 属性）；
    /// 仅设置 Interop 还不够 —— 还必须禁用字符串编译，否则脚本可用 eval / new Function 绕过限制。
    /// </summary>
    internal static Engine Create()
    {
        return new Engine(options =>
        {
            options.Interop.Enabled = false;
            options.MaxStatements(MaxStatements);
            options.LimitMemory(MaxMemoryBytes);
            options.TimeoutInterval(CallTimeout);
            options.LimitRecursion(MaxRecursionDepth);
            options.DisableStringCompilation(true);
        });
    }

    /// <summary>每次执行脚本（顶层脚本或回调）前重置语句数、内存与超时预算。</summary>
    internal static void ResetBudget(Engine engine) => engine.Constraints.Reset();
}

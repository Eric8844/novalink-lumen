using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace NovaLink.Lumen.Core.Plugins;

/// <summary>
/// 插件信任边界（纵深防御）。
///
/// 约定：插件程序集一律由可回收的 AssemblyLoadContext 加载，宿主自己的程序集都在默认上下文、
/// 且不可回收。敏感 API（解密账户凭据等）在入口处调用本守卫：
/// 取调用栈里第一个"宿主之外"的帧，如果它来自插件上下文，就直接拒绝。
///
/// 为什么这么做：插件与启动器同进程，理论上能反射到任何类型。我们做不到在内核层面把它关起来，
/// 但可以保证"通过公开 API 拿不到令牌"——插件直接调用解密方法会抛异常，而不是拿到明文。
/// 真正的强隔离（独立进程 + 明确 IPC 白名单）留给后续版本。
/// </summary>
public static class PluginTrustBoundary
{
    private static readonly Assembly GuardAssembly = typeof(PluginTrustBoundary).Assembly;

    /// <summary>该程序集是否由可回收的插件上下文加载（宿主程序集恒为 false）。</summary>
    public static bool IsPluginAssembly(Assembly assembly)
    {
        try
        {
            return AssemblyLoadContext.GetLoadContext(assembly) is { IsCollectible: true };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 校验调用方不是插件。operation 会被拼进异常文案，例如"读取启动器保存的账户凭据"。
    /// 注意：宿主的公共服务（如 LumenPluginHost）转调敏感 API 时属于受信任路径，会被放行；
    /// 插件直接调用敏感 API 时会被拒绝。
    /// </summary>
    public static void EnsureTrustedCaller(Assembly sensitiveAssembly, string operation)
    {
        if (!IsEnabled) return;

        try
        {
            foreach (var frame in new StackTrace(fNeedFileInfo: false).GetFrames())
            {
                var type = frame.GetMethod()?.DeclaringType;
                if (type is null) continue;
                var assembly = type.Assembly;

                // 跳过守卫自身与敏感 API 自己的栈帧，取第一层外部调用方。
                if (assembly == GuardAssembly || assembly == sensitiveAssembly) continue;

                if (IsPluginAssembly(assembly))
                    throw Denied(operation);

                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch
        {
            // 栈信息拿不到时不阻断宿主功能；插件侧另有 API 层面的收口。
        }
    }

    /// <summary>可为特定场景（如宿主内部测试）关闭守卫。</summary>
    public static bool IsEnabled { get; set; } = true;

    private static UnauthorizedAccessException Denied(string operation)
        => new("插件不能" + operation + "。插件只应通过宿主 API 工作，凭据与密钥不对插件开放。");
}

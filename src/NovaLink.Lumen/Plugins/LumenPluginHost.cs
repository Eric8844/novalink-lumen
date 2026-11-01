using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Hooks;

namespace NovaLink.Lumen.Plugins;

/// <summary>
/// IPluginHost 的真实实现：把启动器现有服务的能力以"最小面"暴露给插件。
/// 设计原则：
/// 1. 每个方法都是"能力不可用"比"崩溃"好 —— 服务尚未初始化时返回明确的失败值；
/// 2. 不把 AppPaths、SettingsService 之类的类型露给插件，插件只拿到字符串与只读快照；
/// 3. 所有异常在边界处吞掉并记日志（插件的错不该让启动器死）。
/// </summary>
public sealed class LumenPluginHost : IPluginHost
{
    private const string LogArea = "plugins";

    /// <summary>当前实例只读快照；由 <see cref="RefreshInstancesAsync"/> 预热，UI 页面也会顺带刷新。</summary>
    private static volatile IReadOnlyList<InstanceRef> _instances = [];

    public string LauncherVersion => LumenRuntime.LauncherVersion;

    public string DataRoot
    {
        get
        {
            try { return LumenRuntime.Paths?.Root ?? ""; }
            catch { return ""; }
        }
    }

    public string? CurrentAccountName => LumenRuntime.Settings.SelectedAccount?.Name;

    /// <summary>
    /// 只挑显示名、UUID 与头像给插件。AccountRecord 上的 AccessToken / RefreshToken /
    /// NovalinkToken / ForumToken / SkinPath 不会被复制出来，插件拿不到。
    /// </summary>
    public PluginAccount? CurrentAccount
    {
        get
        {
            try
            {
                var account = LumenRuntime.Settings.SelectedAccount;
                if (account is null) return null;

                var name = account.Microsoft?.Name;
                if (string.IsNullOrWhiteSpace(name)) name = account.NovalinkName;
                if (string.IsNullOrWhiteSpace(name)) name = account.Name;

                var avatar = account.Microsoft?.Avatar;
                if (string.IsNullOrWhiteSpace(avatar)) avatar = account.NovalinkAvatar;

                return new PluginAccount(
                    name ?? "",
                    account.Uuid ?? "",
                    avatar ?? "");
            }
            catch
            {
                return null;
            }
        }
    }

    public IReadOnlyList<InstanceRef> ListInstances() => _instances;

    /// <summary>后台预热实例快照（ShellPage 加载完实例列表后也会调用 <see cref="SetInstanceSnapshot"/>）。</summary>
    public static async Task RefreshInstancesAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await LumenRuntime.Instances.ScanAsync(ct);
            SetInstanceSnapshot(list);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "插件宿主预热实例列表失败：" + ex.Message);
        }
    }

    /// <summary>把当前实例列表同步给插件宿主（返回只读快照，插件拿不到 InstanceSummary 本体）。</summary>
    public static void SetInstanceSnapshot(IEnumerable<InstanceSummary> instances)
    {
        _instances = instances
            .Select(item => new InstanceRef(
                item.Id ?? "",
                string.IsNullOrWhiteSpace(item.Name) ? item.Id ?? "" : item.Name,
                string.IsNullOrWhiteSpace(item.VersionName) ? item.Kernel : item.VersionName,
                item.Loader ?? ""))
            .ToArray();
    }

    public async Task LaunchInstanceAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("实例 id 不能为空。", nameof(id));

        var instances = await LumenRuntime.Instances.ScanAsync(ct);
        SetInstanceSnapshot(instances);

        var summary = instances.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (summary is null)
            throw new InvalidOperationException("找不到实例：" + id);

        var account = LumenRuntime.Settings.SelectedAccount;
        if (account is null)
            throw new InvalidOperationException("还没有登录任何账户，无法启动游戏。");

        await LumenRuntime.Launch.LaunchAsync(summary, account, ct);
    }

    public async Task<PluginHttpResult> HttpGetAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new PluginHttpResult(0, "URL 不能为空。", false);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new PluginHttpResult(0, "只允许 http/https 绝对地址。", false);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (headers is not null)
            {
                foreach (var pair in headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                        AppLog.Warn(LogArea, "插件请求头被忽略：" + pair.Key);
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var response = await LumenRuntime.Http.Client.SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new PluginHttpResult((int)response.StatusCode, body, response.IsSuccessStatusCode);
        }
        catch (OperationCanceledException)
        {
            return new PluginHttpResult(0, "请求超时或已取消。", false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "插件 HTTP 请求失败：" + ex.Message);
            return new PluginHttpResult(0, ex.Message, false);
        }
    }

    public void Notify(string title, string message, string level = "info")
    {
        try
        {
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "插件通知" : title.Trim();
            var safeMessage = message ?? "";

            // 通知过滤钩子：插件可以屏蔽通知（例如自动更新提示）。
            if (!HookRegistry.ShouldShowNotification(safeTitle, safeMessage))
            {
                AppLog.Info(LogArea, "[已静音] " + safeTitle + "：" + safeMessage);
                return;
            }

            AppLog.Info(LogArea, "[" + level + "] " + safeTitle + "：" + safeMessage);
            Notice?.Invoke(safeTitle, safeMessage, level ?? "info");
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "插件通知失败：" + ex.Message);
        }
    }

    /// <summary>通知广播（P1 由 ShellPage 接到气泡上；现在至少保证有一条日志）。</summary>
    public static event Action<string, string, string>? Notice;
}

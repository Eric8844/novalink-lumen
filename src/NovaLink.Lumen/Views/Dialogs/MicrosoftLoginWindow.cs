using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>内嵌 WebView2 的 Microsoft 授权窗口，只负责拿到授权码。</summary>
public static class MicrosoftLoginWindow
{
    public static async Task<string?> ShowAsync(CancellationToken ct = default)
    {
        var window = new Window { Title = "Microsoft 登录 — NovaLink Lumen" };
        var web = new WebView2();
        window.Content = web;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 720));
        window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var url = LumenRuntime.Microsoft.AuthorizeUrl;
        web.Source = new Uri(url);

        void Finish(string? code)
        {
            if (tcs.Task.IsCompleted) return;
            tcs.TrySetResult(code);
            try { window.Close(); } catch { }
        }

        web.NavigationStarting += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Uri)) return;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
            if (!uri.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase)) return;
            if (!uri.AbsolutePath.Contains("oauth20_desktop.srf", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            var query = ParseQuery(uri.Query);
            if (query.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code))
            {
                Finish(code);
                return;
            }
            query.TryGetValue("error_description", out var error);
            if (string.IsNullOrWhiteSpace(error)) query.TryGetValue("error", out error);
            tcs.TrySetException(new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Microsoft 登录被取消" : error));
            try { window.Close(); } catch { }
        };

        window.Closed += (_, _) =>
        {
            if (!tcs.Task.IsCompleted) tcs.TrySetResult(null);
        };

        window.Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var reg = linked.Token.Register(() =>
        {
            if (!tcs.Task.IsCompleted) tcs.TrySetException(new InvalidOperationException("登录超时，请重试"));
            try { window.Close(); } catch { }
        });
        return await tcs.Task;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index < 0) continue;
            result[Uri.UnescapeDataString(pair[..index])] = Uri.UnescapeDataString(pair[(index + 1)..].Replace("+", " "));
        }
        return result;
    }
}

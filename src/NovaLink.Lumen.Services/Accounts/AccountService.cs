using System.Security.Cryptography;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services.NovaLink;
using NovaLink.Lumen.Services.Storage;

namespace NovaLink.Lumen.Services.Accounts;

public sealed class AccountService
{
    private readonly SettingsService _settings;
    private readonly SecretProtector _secret;
    private readonly NovaLinkClient _novalink;

    public const string MinecraftAvatar = "https://api.mineatar.io/face/";

    public event EventHandler? AccountsChanged;

    public AccountService(SettingsService settings, SecretProtector secret, NovaLinkClient novalink)
    {
        _settings = settings;
        _secret = secret;
        _novalink = novalink;
    }

    public IReadOnlyList<AccountPublic> PublicAccounts()
    {
        var boundUuids = _settings.Settings.Accounts
            .Where(a => a.Type == "novalink" && a.Microsoft is not null)
            .Select(a => (a.Microsoft!.Uuid ?? "").Replace("-", "").ToLowerInvariant())
            .Where(u => u.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _settings.Settings.Accounts
            .Where(a => !(a.Type == "microsoft" && boundUuids.Contains((a.Uuid ?? "").Replace("-", "").ToLowerInvariant())))
            .Select(ToPublic)
            .ToList();
    }

    public AccountPublic? SelectedPublic()
    {
        var selected = _settings.SelectedAccount;
        return selected is null ? null : ToPublic(selected);
    }

    public AccountRecord? Selected => _settings.SelectedAccount;

    public AccountPublic ToPublic(AccountRecord a)
    {
        var bound = a.Type == "novalink" && a.Microsoft is not null;
        var localMs = bound ? LocalMicrosoftFor(a) : null;
        var name = bound ? (a.Microsoft!.Name ?? a.Name) : a.Name;
        var uuid = bound ? (a.Microsoft!.Uuid ?? a.Uuid) : a.Uuid;
        var avatar = "";
        if (bound && uuid.Length > 0)
            avatar = AvatarForUuid(uuid);
        else if (a.Type == "microsoft")
            avatar = AvatarForUuid(a.Uuid);
        else if (a.Type == "novalink")
            avatar = string.IsNullOrWhiteSpace(a.NovalinkAvatar) ? (uuid.Length > 0 ? AvatarForUuid(uuid) : "") : _novalink.Absolute(a.NovalinkAvatar);
        else if (!string.IsNullOrWhiteSpace(a.SkinPath))
        {
            var migratedSkin = Path.Combine(_settings.DataRoot, "skins", Path.GetFileName(a.SkinPath));
            avatar = File.Exists(a.SkinPath) ? a.SkinPath : File.Exists(migratedSkin) ? migratedSkin : "";
        }
        var kind = bound ? "NovaLink · Microsoft" : a.Type switch
        {
            "microsoft" => "Microsoft",
            "novalink" => "NovaLink",
            _ => "离线账户"
        };
        var initial = string.IsNullOrWhiteSpace(name) ? "N" : name[..1].ToUpperInvariant();
        return new AccountPublic(
            a.Id, kind, name, uuid, kind, avatar, initial,
            bound, a.Id == _settings.Settings.SelectedAccountId);
    }

    public static string AvatarForUuid(string uuid)
        => MinecraftAvatar + uuid.Replace("-", "").ToLowerInvariant() + "?scale=4";

    public AccountRecord? LocalMicrosoftFor(AccountRecord account)
    {
        if (account.Microsoft is null) return null;
        return _settings.Settings.Accounts.FirstOrDefault(a =>
            a.Type == "microsoft" &&
            (a.Id == account.MicrosoftAccountId ||
             string.Equals(a.Uuid, account.Microsoft!.Uuid, StringComparison.OrdinalIgnoreCase)));
    }

    // 下面四个方法会取出明文令牌。插件一律拒绝（见 PluginTrustBoundary），
    // 宿主内部（登录、绑定、启动游戏、论坛）走的是受信任调用路径。
    public string NovalinkTokenFor(AccountRecord account)
    {
        PluginTrustBoundary.EnsureTrustedCaller(typeof(AccountService).Assembly, "读取 NovaLink 账户令牌");
        return _secret.Unprotect(account.NovalinkToken);
    }

    public string MicrosoftTokenFor(AccountRecord account)
    {
        PluginTrustBoundary.EnsureTrustedCaller(typeof(AccountService).Assembly, "读取 Microsoft 账户令牌");
        return _secret.Unprotect(account.AccessToken);
    }

    public string ForumTokenFor(AccountRecord account)
    {
        PluginTrustBoundary.EnsureTrustedCaller(typeof(AccountService).Assembly, "读取论坛令牌");
        return _secret.Unprotect(account.ForumToken);
    }

    public string MicrosoftRefreshTokenFor(AccountRecord account)
    {
        PluginTrustBoundary.EnsureTrustedCaller(typeof(AccountService).Assembly, "读取 Microsoft 刷新令牌");
        return _secret.Unprotect(account.RefreshToken);
    }

    public AccountPublic UpsertMicrosoft(string name, string uuid, string minecraftToken, string refreshToken, long expiresAt, string skinUrl)
    {
        var id = "ms-" + uuid.Replace("-", "").ToLowerInvariant();
        var account = _settings.Settings.Accounts.FirstOrDefault(a =>
            a.Id == id || (a.Type == "microsoft" && string.Equals(a.Uuid, uuid, StringComparison.OrdinalIgnoreCase)));
        account ??= new AccountRecord { Id = id, Type = "microsoft", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        account.Type = "microsoft";
        account.Name = name;
        account.Uuid = uuid;
        account.AccessToken = _secret.Protect(minecraftToken);
        account.RefreshToken = _secret.Protect(refreshToken);
        account.ExpiresAt = expiresAt;
        account.Microsoft = new MicrosoftProfile
        {
            Name = name,
            Uuid = uuid,
            Avatar = AvatarForUuid(uuid),
            Skin = skinUrl,
            BoundAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        account.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_settings.Settings.Accounts.Contains(account)) _settings.Settings.Accounts.Add(account);
        _settings.Settings.SelectedAccountId = account.Id;
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return ToPublic(account);
    }

    public void UpdateMicrosoftTokens(AccountRecord account, string minecraftToken, string refreshToken, long expiresAt)
    {
        account.AccessToken = _secret.Protect(minecraftToken);
        account.RefreshToken = _secret.Protect(refreshToken);
        account.ExpiresAt = expiresAt;
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StoreForumToken(AccountRecord account, string token)
    {
        account.ForumToken = _secret.Protect(token);
        _settings.Save();
    }

    public async Task<AccountPublic> CreateOfflineAsync(string rawName)
    {
        var name = (rawName ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
            throw new InvalidOperationException("用户名需为 3–16 位字母、数字或下划线");
        var existing = _settings.Settings.Accounts.FirstOrDefault(a => a.Type == "offline" && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Select(existing.Id);
            return ToPublic(existing);
        }
        var account = new AccountRecord
        {
            Id = "offline-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            Type = "offline",
            Name = name,
            Uuid = OfflineUuid.ForName(name),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        _settings.Settings.Accounts.Add(account);
        _settings.Settings.SelectedAccountId = account.Id;
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return ToPublic(account);
    }

    public async Task<AccountPublic> LoginNovaLinkAsync(string name, string password, CancellationToken ct = default)
    {
        var data = await _novalink.LoginAsync(name, password, ct);
        if (!data.Ok || data.User is null || string.IsNullOrWhiteSpace(data.Token))
            throw new InvalidOperationException(data.Msg ?? "NovaLink 登录失败");
        return await UpsertNovaLinkAsync(data.User, data.Token!, true, ct);
    }

    public async Task<AccountPublic> RegisterNovaLinkAsync(string name, string password, CancellationToken ct = default)
    {
        var data = await _novalink.RegisterAsync(name, password, ct);
        if (!data.Ok || data.User is null || string.IsNullOrWhiteSpace(data.Token))
            throw new InvalidOperationException(data.Msg ?? "NovaLink 注册失败");
        return await UpsertNovaLinkAsync(data.User, data.Token!, true, ct);
    }

    public async Task<AccountPublic> UpsertNovaLinkAsync(NovaLinkUser user, string token, bool select, CancellationToken ct = default)
    {
        var id = "novalink-" + user.Id;
        var account = _settings.Settings.Accounts.FirstOrDefault(a => a.Id == id);
        account ??= new AccountRecord { Id = id, Type = "novalink", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        account.Type = "novalink";
        account.Name = string.IsNullOrWhiteSpace(user.Name) ? account.Name : user.Name;
        account.NovalinkName = account.Name;
        account.NovalinkId = user.Id;
        account.NovalinkCode = user.UserCode;
        account.NovalinkAvatar = _novalink.Absolute(user.Avatar);
        account.NovalinkToken = _secret.Protect(token);
        account.Uuid = string.IsNullOrWhiteSpace(account.Uuid) ? OfflineUuid.ForName(account.Name) : account.Uuid;
        account.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var ms = user.Lumen?.Microsoft;
        if (ms is not null && !string.IsNullOrWhiteSpace(ms.Name) && !string.IsNullOrWhiteSpace(ms.Uuid))
        {
            account.Microsoft = new MicrosoftProfile
            {
                Name = ms.Name,
                Uuid = ms.Uuid,
                Avatar = string.IsNullOrWhiteSpace(ms.Avatar) ? AvatarForUuid(ms.Uuid) : _novalink.Absolute(ms.Avatar),
                Skin = _novalink.Absolute(ms.Skin),
                BoundAt = ms.BoundAt
            };
            var local = _settings.Settings.Accounts.FirstOrDefault(a => a.Type == "microsoft" && string.Equals(a.Uuid, ms.Uuid, StringComparison.OrdinalIgnoreCase));
            if (local is not null) account.MicrosoftAccountId = local.Id;
        }
        else
        {
            account.Microsoft = null;
            account.MicrosoftAccountId = null;
        }

        var skinUrl = user.Lumen?.Minecraft?.Skin;
        if (!string.IsNullOrWhiteSpace(skinUrl))
        {
            account.NovalinkSkinUrl = _novalink.Absolute(skinUrl);
            await DownloadSkinAsync(account, account.NovalinkSkinUrl, ct);
        }

        if (!_settings.Settings.Accounts.Contains(account)) _settings.Settings.Accounts.Add(account);
        if (select) _settings.Settings.SelectedAccountId = id;
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return ToPublic(account);
    }

    public AccountPublic BindMicrosoft(AccountRecord novalink, AccountRecord microsoft, bool upload = true)
    {
        if (novalink.Type != "novalink") throw new InvalidOperationException("只能绑定到 NovaLink 账户");
        novalink.Microsoft = new MicrosoftProfile
        {
            Name = microsoft.Name,
            Uuid = microsoft.Uuid,
            Avatar = AvatarForUuid(microsoft.Uuid),
            Skin = "",
            BoundAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        novalink.MicrosoftAccountId = microsoft.Id;
        _settings.Settings.SelectedAccountId = novalink.Id;
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return ToPublic(novalink);
    }

    public async Task<AccountPublic> BindMicrosoftAsync(AccountRecord novalink, AccountRecord microsoft, CancellationToken ct = default)
    {
        var token = NovalinkTokenFor(novalink);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("NovaLink 登录已过期，请重新登录");
        await _novalink.BindMicrosoftAsync(token, microsoft.Name, microsoft.Uuid, AvatarForUuid(microsoft.Uuid), "", ct);
        return BindMicrosoft(novalink, microsoft);
    }

    public async Task<AccountPublic> UnbindMicrosoftAsync(AccountRecord account, CancellationToken ct = default)
    {
        var token = NovalinkTokenFor(account);
        if (!string.IsNullOrWhiteSpace(token))
            await _novalink.UnbindMicrosoftAsync(token, ct).ConfigureAwait(false);
        account.Microsoft = null;
        account.MicrosoftAccountId = null;
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return ToPublic(account);
    }

    public void Select(string id)
    {
        var account = _settings.Find(id);
        if (account is null) return;
        _settings.Settings.SelectedAccountId = id;
        account.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(AccountRecord account, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 32) throw new InvalidOperationException("名称需为 1–32 个字符");
        if (account.Type == "offline")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
                throw new InvalidOperationException("离线用户名需为 3–16 位字母、数字或下划线");
            account.Name = name;
            account.Uuid = OfflineUuid.ForName(name);
        }
        else account.Name = name;
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(AccountRecord account)
    {
        _settings.Settings.Accounts.Remove(account);
        if (_settings.Settings.SelectedAccountId == account.Id)
            _settings.Settings.SelectedAccountId = _settings.Settings.Accounts.FirstOrDefault()?.Id;
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task DownloadSkinAsync(AccountRecord account, string url, CancellationToken ct)
    {
        try
        {
            var bytes = await _novalinkRequestBytes(url, ct);
            if (bytes.Length < 64) return;
            var path = Path.Combine(_settings.DataRoot, "skins",
                "nl-" + System.Text.RegularExpressions.Regex.Replace(account.Id, "[^a-zA-Z0-9_-]", "") + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct);
            account.SkinPath = path;
        }
        catch (Exception ex) { AppLog.Warn("account", "下载皮肤失败：" + ex.Message); }
    }

    private async Task<byte[]> _novalinkRequestBytes(string url, CancellationToken ct)
    {
        using var res = await new Http.HttpService().Client.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }
}

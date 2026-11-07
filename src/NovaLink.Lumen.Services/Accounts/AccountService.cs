using System.Security.Cryptography;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services.NovaLink;
using NovaLink.Lumen.Services.Skins;
using NovaLink.Lumen.Services.Storage;

namespace NovaLink.Lumen.Services.Accounts;

public sealed class AccountService
{
    private readonly SettingsService _settings;
    private readonly SecretProtector _secret;
    private readonly NovaLinkClient _novalink;
    private readonly LittleSkinAuthService _littleSkin;
    private readonly MojangSkinService _mojangSkin;

    public const string MinecraftAvatar = "https://api.mineatar.io/face/";

    public event EventHandler? AccountsChanged;

    public AccountService(SettingsService settings, SecretProtector secret, NovaLinkClient novalink)
    {
        _settings = settings;
        _secret = secret;
        _novalink = novalink;
        // 独立一个小客户端：Yggdrasil 认证不走插件的下载地址改写，避免皮肤站请求被插件劫持。
        _littleSkin = new LittleSkinAuthService(new Http.HttpService());
        // Mojang 皮肤接口同样独立：正版皮肤不经过插件的地址改写。
        _mojangSkin = new MojangSkinService(new Http.HttpService());
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
        var localFace = LocalFaceFor(a);
        if (bound && uuid.Length > 0)
            avatar = localFace.Length > 0 ? localFace : AvatarForUuid(uuid);
        else if (a.Type == "microsoft")
            avatar = localFace.Length > 0 ? localFace : AvatarForUuid(a.Uuid);
        else if (a.Type == "novalink")
            avatar = string.IsNullOrWhiteSpace(a.NovalinkAvatar) ? (uuid.Length > 0 ? AvatarForUuid(uuid) : "") : _novalink.Absolute(a.NovalinkAvatar);
        else if (a.Type == "littleskin")
        {
            // 头像用本地裁好的头部 PNG：离线也能显示，且和 3D 人物用的是同一张皮肤。
            var face = LittleSkinFacePath(a);
            avatar = face.Length > 0 && File.Exists(face) ? face : "";
        }
        else if (!string.IsNullOrWhiteSpace(a.SkinPath))
        {
            var migratedSkin = Path.Combine(_settings.DataRoot, "skins", Path.GetFileName(a.SkinPath));
            avatar = File.Exists(a.SkinPath) ? a.SkinPath : File.Exists(migratedSkin) ? migratedSkin : "";
        }
        var kind = bound ? "NovaLink · Microsoft" : a.Type switch
        {
            "microsoft" => "Microsoft",
            "novalink" => "NovaLink",
            "littleskin" => "LittleSkin",
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

    // ------------------------------------------------------------------ LittleSkin

    /// <summary>取出 LittleSkin 明文访问令牌（宿主内部使用；插件调用会被信任边界拒绝）。</summary>
    public string LittleSkinTokenFor(AccountRecord account)
    {
        PluginTrustBoundary.EnsureTrustedCaller(typeof(AccountService).Assembly, "读取 LittleSkin 账户令牌");
        return _secret.Unprotect(account.AccessToken);
    }

    /// <summary>
    /// 启动前拿一个可用的 LittleSkin 令牌：没过期直接返回，过期或缺失时用 clientToken 续期一次。
    /// 续期失败抛中文错误，由调用方决定提示重新登录还是退回离线。
    /// </summary>
    public async Task<string> EnsureLittleSkinTokenAsync(AccountRecord account, CancellationToken ct = default)
    {
        if (account.Type != "littleskin")
            throw new InvalidOperationException("这个账户不是 LittleSkin 账户。");

        var stored = _secret.Unprotect(account.AccessToken);
        var expiresSoon = account.ExpiresAt > 0 &&
                          account.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;
        if (!string.IsNullOrWhiteSpace(stored) && !expiresSoon) return stored;

        if (string.IsNullOrWhiteSpace(account.LittleSkinClientToken) || string.IsNullOrWhiteSpace(stored))
            throw new InvalidOperationException("LittleSkin 登录已失效，请到设置里重新登录。");

        var session = await _littleSkin.RefreshAsync(stored, account.LittleSkinClientToken!, ct);
        account.AccessToken = _secret.Protect(session.AccessToken);
        account.LittleSkinClientToken = session.ClientToken;
        account.ExpiresAt = session.ExpiresAt;
        _settings.Save();
        return session.AccessToken;
    }

    /// <summary>用 LittleSkin 邮箱/用户名与密码登录，成功后写入本地账户并设为启动账户。</summary>
    public async Task<AccountPublic> LoginLittleSkinAsync(string username, string password, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0) throw new InvalidOperationException("请填写 LittleSkin 邮箱或用户名。");
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("请填写密码。");

        var existing = _settings.Settings.Accounts.FirstOrDefault(a =>
            a.Type == "littleskin" && string.Equals(a.LittleSkinEmail, username, StringComparison.OrdinalIgnoreCase));
        var clientToken = string.IsNullOrWhiteSpace(existing?.LittleSkinClientToken)
            ? LittleSkinAuthService.NewClientToken()
            : existing!.LittleSkinClientToken!;

        var session = await _littleSkin.AuthenticateAsync(username, password, clientToken, ct);
        return await UpsertLittleSkinAsync(session, username, ct);
    }

    private async Task<AccountPublic> UpsertLittleSkinAsync(LittleSkinSession session, string email, CancellationToken ct)
    {
        var uuid = LittleSkinAuthService.NormalizeUuid(session.Uuid);
        if (uuid.Length == 0) throw new InvalidOperationException("LittleSkin 返回的账号信息不完整，请稍后再试。");
        var dashed = LittleSkinAuthService.FormatUuid(uuid);

        var account = _settings.Settings.Accounts.FirstOrDefault(a =>
                          a.Type == "littleskin" &&
                          string.Equals(LittleSkinAuthService.NormalizeUuid(a.Uuid), uuid, StringComparison.OrdinalIgnoreCase))
                      ?? _settings.Settings.Accounts.FirstOrDefault(a => a.Id == "littleskin-" + uuid);
        account ??= new AccountRecord
        {
            Id = "littleskin-" + uuid,
            Type = "littleskin",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        account.Type = "littleskin";
        if (!string.IsNullOrWhiteSpace(session.Name)) account.Name = session.Name;
        account.Uuid = dashed;
        account.AccessToken = _secret.Protect(session.AccessToken);
        account.LittleSkinEmail = email;
        account.LittleSkinClientToken = session.ClientToken;
        account.LittleSkinServer = LittleSkinAuthService.DefaultServer;
        account.ExpiresAt = session.ExpiresAt;
        account.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_settings.Settings.Accounts.Contains(account)) _settings.Settings.Accounts.Add(account);
        _settings.Settings.SelectedAccountId = account.Id;

        // 皮肤与头像尽量拿：失败只影响外观，不影响登录本身。
        try
        {
            var url = await _littleSkin.ResolveSkinUrlAsync(uuid, ct);
            if (!string.IsNullOrWhiteSpace(url)) await DownloadLittleSkinSkinAsync(account, url!, uuid, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("account", "读取 LittleSkin 皮肤失败：" + ex.Message);
        }

        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return ToPublic(account);
    }

    /// <summary>下载整张皮肤写进 SkinPath，再从头部 (8,8,8,8) 裁出 64x64 头像 PNG。</summary>
    private async Task DownloadLittleSkinSkinAsync(AccountRecord account, string url, string uuid, CancellationToken ct)
    {
        var bytes = await _littleSkin.DownloadSkinAsync(url, ct);
        if (bytes is null || !IsSkinPng(bytes))
        {
            AppLog.Warn("account", "LittleSkin 皮肤不是有效的 64x64/64x32 PNG，已忽略。");
            return;
        }

        var directory = Path.Combine(_settings.DataRoot, "skins");
        Directory.CreateDirectory(directory);
        var skinPath = Path.Combine(directory, "ls-" + uuid + ".png");
        await File.WriteAllBytesAsync(skinPath, bytes, ct);
        account.SkinPath = skinPath;

        var facePath = Path.Combine(directory, "ls-face-" + uuid + ".png");
        if (!await WriteSkinHeadAsync(skinPath, facePath, ct))
            AppLog.Warn("account", "生成 LittleSkin 头像失败，账户卡会显示首字母占位。");
    }

    /// <summary>PNG 签名 + IHDR 尺寸校验：只接受 64x64 或 64x32（与官方皮肤、3D 人物一致）。</summary>
    private static bool IsSkinPng(byte[] data) => MojangSkinService.IsSkinPng(data);

    private string LittleSkinFacePath(AccountRecord account) => LittleSkinFile(account, "ls-face-");

    private string LittleSkinFile(AccountRecord account, string prefix)
    {
        var key = LittleSkinAuthService.NormalizeUuid(account.Uuid);
        if (key.Length == 0)
            key = new string((account.Id ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        return key.Length == 0 ? "" : Path.Combine(_settings.DataRoot, "skins", prefix + key + ".png");
    }

    // ---------------------------------- 账户皮肤（离线用正版皮肤 / 本地 PNG / 正版上传） ----------------------------------

    /// <summary>本地皮肤文件：&lt;数据目录&gt;\skins\sk-&lt;账户 id&gt;.png（头像同目录 face-&lt;id&gt;.png）。</summary>
    private string AccountSkinFile(AccountRecord account, string prefix)
    {
        var safe = new string((account.Id ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(_settings.DataRoot, "skins", prefix + safe + ".png");
    }

    /// <summary>本地裁好的 64×64 头部 PNG；没生成过返回空串。</summary>
    private string LocalFaceFor(AccountRecord account)
    {
        var path = AccountSkinFile(account, "face-");
        return File.Exists(path) ? path : "";
    }

    /// <summary>给界面做预检：这个文件能不能当皮肤（读不到或不合法都返回 false）。</summary>
    public static bool LooksLikeSkin(string path)
    {
        try
        {
            return File.Exists(path) && IsSkinPng(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            AppLog.Warn("account", "校验本地皮肤失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>把本地 PNG 设为这个账户的皮肤，并重新裁一张头像。</summary>
    public async Task<AccountPublic> ApplyLocalSkinAsync(AccountRecord account, string sourcePath, string? variant = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidOperationException("找不到这个皮肤文件，请重新选择。");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(sourcePath, ct);
        }
        catch (Exception ex)
        {
            AppLog.Warn("account", "读取本地皮肤失败：" + ex.Message);
            throw new InvalidOperationException("读取皮肤文件失败，请换一张图片再试。");
        }

        if (!IsSkinPng(bytes))
            throw new InvalidOperationException("这不是有效的 Minecraft 皮肤：需要 64×64 或 64×32 的 PNG 图片。");

        await SaveAccountSkinAsync(account, bytes, variant, ct);
        return ToPublic(account);
    }

    /// <summary>离线账户：按正版用户名拉一张官方皮肤套用（只换外观，不改账户身份）。</summary>
    public async Task<AccountPublic> ApplyMojangSkinAsync(AccountRecord account, string playerName, CancellationToken ct = default)
    {
        var bytes = await _mojangSkin.FetchSkinAsync(playerName, ct);
        await SaveAccountSkinAsync(account, bytes, null, ct);
        return ToPublic(account);
    }

    private async Task SaveAccountSkinAsync(AccountRecord account, byte[] bytes, string? variant, CancellationToken ct)
    {
        var directory = Path.Combine(_settings.DataRoot, "skins");
        Directory.CreateDirectory(directory);

        var skinPath = AccountSkinFile(account, "sk-");
        await File.WriteAllBytesAsync(skinPath, bytes, ct);
        account.SkinPath = skinPath;

        if (variant is "slim" or "classic")
        {
            // 主页 3D 人物按这个判断纤细/经典模型，换皮肤时一并更新。
            account.Skins = [new SkinEntry { Id = account.Id, State = "ACTIVE", Variant = variant }];
        }

        var facePath = AccountSkinFile(account, "face-");
        if (!await WriteSkinHeadAsync(skinPath, facePath, ct))
            AppLog.Warn("account", "生成账户头像失败，账户卡会显示首字母占位。");

        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>恢复默认：清掉本地皮肤，正版账户会重新按 UUID 拉官方皮肤，离线账户回到内置史蒂夫。</summary>
    public AccountPublic ClearSkin(AccountRecord account)
    {
        account.SkinPath = null;
        account.Skins = null;
        var facePath = AccountSkinFile(account, "face-");
        try
        {
            if (File.Exists(facePath)) File.Delete(facePath);
        }
        catch (Exception ex)
        {
            AppLog.Warn("account", "删除本地头像失败：" + ex.Message);
        }

        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return ToPublic(account);
    }

    /// <summary>用系统解码器把皮肤头部放大到 64x64 写成头像 PNG（离屏渲染，不依赖 UI 线程）。</summary>
    private static async Task<bool> WriteSkinHeadAsync(string skinPath, string facePath, CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(skinPath, ct);
            if (!IsSkinPng(bytes)) return false;

            using var input = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            input.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(input);
            // 头部在左上角 8×8，64×32 的旧版皮肤一样能裁，所以高度只要求 ≥32。
            if (decoder.PixelWidth < 64 || decoder.PixelHeight < 32) return false;

            // 先裁出 8x8 的头部；WinRT 的 BitmapTransform 在给了 Bounds 时不会同时缩放，
            // 所以放大到 64x64 只能自己做（最近邻，保持像素风锐利）。
            var transform = new BitmapTransform
            {
                Bounds = new BitmapBounds { X = 8, Y = 8, Width = 8, Height = 8 }
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var head = pixels.DetachPixelData();
            if (head.Length < 8 * 8 * 4) return false;

            const int headSize = 8;
            const int faceSize = 64;
            var scaled = new byte[faceSize * faceSize * 4];
            for (var y = 0; y < faceSize; y++)
            {
                var sourceRow = (y * headSize / faceSize) * headSize;
                for (var x = 0; x < faceSize; x++)
                {
                    var source = (sourceRow + x * headSize / faceSize) * 4;
                    var target = (y * faceSize + x) * 4;
                    scaled[target] = head[source];
                    scaled[target + 1] = head[source + 1];
                    scaled[target + 2] = head[source + 2];
                    scaled[target + 3] = head[source + 3];
                }
            }

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, faceSize, faceSize, 96, 96, scaled);
            await encoder.FlushAsync();

            output.Seek(0);
            var face = new byte[(int)output.Size];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)output.Size);
                reader.ReadBytes(face);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(facePath)!);
            await File.WriteAllBytesAsync(facePath, face, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // WinRT 异常的 Message 经常是空的，日志里记完整字符串方便排查。
            AppLog.Warn("account", "裁剪 LittleSkin 头像失败：" + ex);
            return false;
        }
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

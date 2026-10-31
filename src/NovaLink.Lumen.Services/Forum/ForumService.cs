using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Accounts;
using NovaLink.Lumen.Services.Http;
using NovaLink.Lumen.Services.NovaLink;

namespace NovaLink.Lumen.Services.Forum;

/// <summary>NovaLink 论坛与动态：身份换取、话题、回复、动态与评论。</summary>
public sealed class ForumService
{
    private readonly NovaLinkClient _novalink;
    private readonly AccountService _accounts;

    public ForumService(NovaLinkClient novalink, AccountService accounts)
    {
        _novalink = novalink;
        _accounts = accounts;
    }

    public const string LoginHint = "请使用 NovaLink 或 Microsoft 账户登录后发言";

    public async Task<string?> TokenAsync(AccountRecord? account, CancellationToken ct = default)
    {
        if (account is null) return null;
        if (account.Type == "novalink")
        {
            var token = _accounts.NovalinkTokenFor(account);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        if (account.Type == "microsoft")
        {
            var cached = _accounts.ForumTokenFor(account);
            if (!string.IsNullOrWhiteSpace(cached)) return cached;
            var access = _accounts.MicrosoftTokenFor(account);
            if (string.IsNullOrWhiteSpace(access)) return null;
            var login = await _novalink.RequestAsync<ForumMsLoginResponse>("/api/forum/ms-login", HttpMethod.Post,
                new { accessToken = access }, null, ct);
            if (!string.IsNullOrWhiteSpace(login.Token))
            {
                _accounts.StoreForumToken(account, login.Token);
                return login.Token;
            }
            return null;
        }
        return null;
    }

    public async Task<ForumSessionResponse> SessionAsync(AccountRecord? account, CancellationToken ct = default)
    {
        var token = await TokenAsync(account, ct);
        if (string.IsNullOrWhiteSpace(token))
            return new ForumSessionResponse { Ok = true, Authenticated = false, Me = null };
        try
        {
            return await _novalink.RequestAsync<ForumSessionResponse>("/api/forum/session", HttpMethod.Get, null, token, ct);
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            return new ForumSessionResponse { Ok = false, Authenticated = true, Banned = true, Msg = ex.Message };
        }
    }

    public async Task<ForumTopicsResponse> TopicsAsync(AccountRecord? account, CancellationToken ct = default)
        => await _novalink.RequestAsync<ForumTopicsResponse>("/api/forum/topics", HttpMethod.Get, null, await TokenAsync(account, ct), ct);

    public async Task<ForumTopicResponse> TopicAsync(string topicId, AccountRecord? account, CancellationToken ct = default)
        => await _novalink.RequestAsync<ForumTopicResponse>("/api/forum/topic/" + Uri.EscapeDataString(topicId), HttpMethod.Get, null, await TokenAsync(account, ct), ct);

    public async Task<ForumFeedResponse> FeedAsync(AccountRecord? account, CancellationToken ct = default)
        => await _novalink.RequestAsync<ForumFeedResponse>("/api/forum/feed", HttpMethod.Get, null, await TokenAsync(account, ct), ct);

    public async Task<ForumCreateResponse> CreateTopicAsync(AccountRecord? account, string title, string body, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(account, ct);
        return await _novalink.RequestAsync<ForumCreateResponse>("/api/forum/create", HttpMethod.Post, new { title, body }, token, ct);
    }

    public async Task<ForumPostResponse> ReplyAsync(AccountRecord? account, string topicId, string body, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(account, ct);
        return await _novalink.RequestAsync<ForumPostResponse>("/api/forum/post", HttpMethod.Post, new { topic_id = topicId, body }, token, ct);
    }

    public async Task EditAsync(AccountRecord? account, string topicId, string? postId, string body, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(account, ct);
        await _novalink.RequestAsync<ForumPostResponse>("/api/forum/edit", HttpMethod.Post,
            new { topic_id = topicId, post_id = postId ?? "", body }, token, ct);
    }

    public async Task<ForumMomentResponse> CreateMomentAsync(AccountRecord? account, string body, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(account, ct);
        return await _novalink.RequestAsync<ForumMomentResponse>("/api/forum/feed", HttpMethod.Post, new { body }, token, ct);
    }

    public async Task<ForumMomentResponse> CommentMomentAsync(AccountRecord? account, string momentId, string body, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(account, ct);
        return await _novalink.RequestAsync<ForumMomentResponse>("/api/forum/feed/comment", HttpMethod.Post, new { post_id = momentId, body }, token, ct);
    }

    public async Task<ForumProfileResponse> ProfileAsync(string key, CancellationToken ct = default)
        => await _novalink.RequestAsync<ForumProfileResponse>("/api/forum/profile?key=" + Uri.EscapeDataString(key), HttpMethod.Get, null, null, ct);

    private async Task<string> RequireTokenAsync(AccountRecord? account, CancellationToken ct)
    {
        var token = await TokenAsync(account, ct);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException(LoginHint);
        return token;
    }
}

using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Services.Forum;

public sealed class ForumSessionResponse
{
    public bool Ok { get; set; }
    public bool Authenticated { get; set; }
    public bool Banned { get; set; }
    public string? Msg { get; set; }
    public ForumMe? Me { get; set; }
}

public sealed class ForumMe
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string Avatar { get; set; } = "";
    [JsonPropertyName("novalink_id")] public string NovalinkId { get; set; } = "";
    [JsonPropertyName("post_count")] public int PostCount { get; set; }
    [JsonPropertyName("can_create")] public bool CanCreate { get; set; }
    [JsonPropertyName("cooldown")] public double Cooldown { get; set; }
    [JsonPropertyName("feed_cooldown")] public double FeedCooldown { get; set; }
    [JsonPropertyName("comment_cooldown")] public double CommentCooldown { get; set; }
    public bool Banned { get; set; }
}

public sealed class ForumAuthor
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string Avatar { get; set; } = "";
}

public sealed class ForumTopicDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public ForumAuthor Author { get; set; } = new();
    public long Created { get; set; }
    public string Status { get; set; } = "approved";
    [JsonPropertyName("post_count")] public int PostCount { get; set; }
    [JsonPropertyName("last_activity")] public long LastActivity { get; set; }
    [JsonPropertyName("reject_reason")] public string RejectReason { get; set; } = "";
    public List<ForumPostDto> Posts { get; set; } = [];
}

public sealed class ForumPostDto
{
    public string Id { get; set; } = "";
    public string Body { get; set; } = "";
    public ForumAuthor Author { get; set; } = new();
    public long Created { get; set; }
    public bool Edited { get; set; }
}

public sealed class ForumMomentDto
{
    public string Id { get; set; } = "";
    public string Body { get; set; } = "";
    public ForumAuthor Author { get; set; } = new();
    public long Created { get; set; }
    public string Status { get; set; } = "approved";
    [JsonPropertyName("ai_reason")] public string AiReason { get; set; } = "";
    [JsonPropertyName("comment_count")] public int CommentCount { get; set; }
    public List<ForumCommentDto> Comments { get; set; } = [];
}

public sealed class ForumCommentDto
{
    public string Id { get; set; } = "";
    public string Body { get; set; } = "";
    public ForumAuthor Author { get; set; } = new();
    public long Created { get; set; }
    public string Status { get; set; } = "approved";
    [JsonPropertyName("ai_reason")] public string AiReason { get; set; } = "";
}

public sealed class ForumTopicsResponse
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public bool Authenticated { get; set; }
    public List<ForumTopicDto> Topics { get; set; } = [];
}

public sealed class ForumTopicResponse
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public ForumTopicDto? Topic { get; set; }
}

public sealed class ForumFeedResponse
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public bool Authenticated { get; set; }
    public List<ForumMomentDto> Feed { get; set; } = [];
}

public sealed class ForumMomentResponse
{
    public bool Ok { get; set; }
    public bool Pending { get; set; }
    public string? Msg { get; set; }
    [JsonPropertyName("ai_reason")] public string AiReason { get; set; } = "";
    [JsonPropertyName("retry_after")] public double RetryAfter { get; set; }
    public ForumMomentDto? Moment { get; set; }
    public ForumCommentDto? Comment { get; set; }
}

public sealed class ForumCreateResponse
{
    public bool Ok { get; set; }
    public bool Pending { get; set; }
    public string? Msg { get; set; }
    [JsonPropertyName("ai_reason")] public string AiReason { get; set; } = "";
    [JsonPropertyName("post_count")] public int PostCount { get; set; }
    public ForumTopicDto? Topic { get; set; }
}

public sealed class ForumPostResponse
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public ForumPostDto? Post { get; set; }
}

public sealed class ForumProfileResponse
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public ForumProfileDto? Profile { get; set; }
}

public sealed class ForumProfileDto
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string Avatar { get; set; } = "";
    [JsonPropertyName("post_count")] public int PostCount { get; set; }
}

public sealed class ForumMsLoginResponse
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public string Token { get; set; } = "";
}

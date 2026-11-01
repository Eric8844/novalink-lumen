namespace NovaLink.Lumen.Core.Models;

public sealed record NewsItem(string Id, string Title, string Version, string Category, string Date, string ImageUrl, string ShortText, string Link = "", string ContentPath = "");
public sealed record ForumTopic(string Id, string Title, string Author, string AuthorAvatar, string CreatedAt, int Replies, int Views, string Preview, string Kind);
public sealed record Moment(string Id, string Author, string AuthorAvatar, string AuthorKind, string CreatedAt, string Content, IReadOnlyList<ForumComment> Comments);
public sealed record ForumComment(string Id, string Author, string AuthorAvatar, string CreatedAt, string Content);
public sealed record ForumProfile(string Key, string Name, string Avatar, string Kind, int Topics, int Moments, int Comments);
public sealed record JavaRuntime(string Path, int Major, string Version, string Vendor);
public sealed record DownloadProgress(string Stage, double Percent, long Received, long Total, double BytesPerSecond, string Message);
public sealed record ManifestVersion(string Id, string Type, string Url, string Sha1, DateTimeOffset ReleaseTime);
public sealed record VersionInstallRequest(string VersionId, string? Name = null, bool Isolated = false);

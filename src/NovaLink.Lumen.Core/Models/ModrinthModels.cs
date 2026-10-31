using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Core.Models;

public sealed class ModrinthSearchResult
{
    [JsonPropertyName("hits")] public List<ModrinthProject> Hits { get; set; } = new();
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public sealed class ModrinthProject
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("display_categories")] public List<string> DisplayCategories { get; set; } = new();
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = new();
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("followers")] public long Followers { get; set; }
    [JsonPropertyName("follows")] public long Follows { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("date_modified")] public DateTimeOffset? DateModified { get; set; }
    [JsonPropertyName("published")] public DateTimeOffset? Published { get; set; }
    [JsonPropertyName("updated")] public DateTimeOffset? Updated { get; set; }
    [JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }
    [JsonPropertyName("client_side")] public string ClientSide { get; set; } = "required";
    [JsonPropertyName("server_side")] public string ServerSide { get; set; } = "optional";
    [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
    [JsonPropertyName("issues_url")] public string? IssuesUrl { get; set; }
    [JsonPropertyName("wiki_url")] public string? WikiUrl { get; set; }
    [JsonPropertyName("license")] public ModrinthLicense? License { get; set; }
    [JsonPropertyName("gallery")] public List<ModrinthGalleryItem> Gallery { get; set; } = new();
    public string ZhTitle { get; set; } = "";
}

[JsonConverter(typeof(ModrinthGalleryItemConverter))]
public sealed class ModrinthGalleryItem
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("featured")] public bool Featured { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public sealed class ModrinthGalleryItemConverter : JsonConverter<ModrinthGalleryItem>
{
    public override ModrinthGalleryItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new ModrinthGalleryItem { Url = reader.GetString() ?? "" };
        if (reader.TokenType != JsonTokenType.StartObject) return null;
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        return new ModrinthGalleryItem
        {
            Url = root.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
            Featured = root.TryGetProperty("featured", out var featured) && featured.ValueKind == JsonValueKind.True,
            Title = root.TryGetProperty("title", out var title) ? title.GetString() : null,
            Description = root.TryGetProperty("description", out var description) ? description.GetString() : null
        };
    }

    public override void Write(Utf8JsonWriter writer, ModrinthGalleryItem value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, new { url = value.Url, featured = value.Featured, title = value.Title, description = value.Description }, options);
}

[JsonConverter(typeof(ModrinthLicenseConverter))]
public sealed class ModrinthLicense
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class ModrinthLicenseConverter : JsonConverter<ModrinthLicense>
{
    public override ModrinthLicense? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString() ?? "";
            return new ModrinthLicense { Id = value, Name = value };
        }
        if (reader.TokenType != JsonTokenType.StartObject) return null;
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        return new ModrinthLicense
        {
            Id = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Url = root.TryGetProperty("url", out var url) ? url.GetString() : null
        };
    }

    public override void Write(Utf8JsonWriter writer, ModrinthLicense value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, new { id = value.Id, name = value.Name, url = value.Url }, options);
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "release";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = new();
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();
    [JsonPropertyName("date_published")] public DateTimeOffset? DatePublished { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = new();
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "required";
}

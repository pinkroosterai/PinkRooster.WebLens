using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinkRooster.WebLens.Search;

// Wire DTOs are permissive on purpose: SearXNG's JSON changes between versions, and one odd field must not fail a page.
// Anything with a polymorphic or unstable shape is kept as JsonElement and interpreted by the mapper.

internal sealed class SearxResponse
{
    [JsonPropertyName("query")] public string? Query { get; set; }
    [JsonPropertyName("results")] public List<SearxResult>? Results { get; set; }
    [JsonPropertyName("answers")] public List<JsonElement>? Answers { get; set; }
    [JsonPropertyName("corrections")] public List<JsonElement>? Corrections { get; set; }
    [JsonPropertyName("suggestions")] public List<JsonElement>? Suggestions { get; set; }
    [JsonPropertyName("infoboxes")] public List<JsonElement>? Infoboxes { get; set; }
    [JsonPropertyName("unresponsive_engines")] public List<JsonElement>? UnresponsiveEngines { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class SearxResult
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("engine")] public string? Engine { get; set; }
    [JsonPropertyName("engines")] public List<string>? Engines { get; set; }
    [JsonPropertyName("score")] public JsonElement Score { get; set; }
    [JsonPropertyName("publishedDate")] public JsonElement PublishedDate { get; set; }
    [JsonPropertyName("pubdate")] public JsonElement PubDate { get; set; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
    [JsonPropertyName("img_src")] public string? ImgSrc { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class SearxConfig
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("engines")] public List<SearxConfigEngine>? Engines { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class SearxConfigEngine
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("shortcut")] public string? Shortcut { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(SearxResponse))]
[JsonSerializable(typeof(SearxConfig))]
internal sealed partial class SearxJsonContext : JsonSerializerContext;

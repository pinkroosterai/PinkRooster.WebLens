using System.Text.Json;
using System.Text.Json.Serialization;
using PinkRooster.WebLens.Client.Models;

namespace PinkRooster.WebLens.Client.Serialization;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(FetchRequest))]
[JsonSerializable(typeof(FetchResponse))]
[JsonSerializable(typeof(WebLensProblemDetails))]
[JsonSerializable(typeof(TimeRange))]
[JsonSerializable(typeof(SafeSearch))]
[JsonSerializable(typeof(SearchResultItem))]
[JsonSerializable(typeof(AnswerItem))]
[JsonSerializable(typeof(InfoboxItem))]
[JsonSerializable(typeof(EngineFailure))]
[JsonSerializable(typeof(SearchMeta))]
[JsonSerializable(typeof(FetchOptions))]
[JsonSerializable(typeof(PageMetadata))]
[JsonSerializable(typeof(FetchDiagnostics))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
public sealed partial class WebLensJsonSerializerContext : JsonSerializerContext;

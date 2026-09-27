using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinkRooster.WebLens.Api.Contracts;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(SearchRequestDto))]
[JsonSerializable(typeof(SearchResponseDto))]
[JsonSerializable(typeof(FetchRequestDto))]
[JsonSerializable(typeof(FetchResponseDto))]
[JsonSerializable(typeof(McpSearchResultDto))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;

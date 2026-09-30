using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinkRooster.WebLens.Client.Models;

/// <summary>
/// RFC 9457 Problem Details object returned by WebLens error endpoints.
/// </summary>
public sealed record WebLensProblemDetails
{
    public string? Type { get; init; }

    public string? Title { get; init; }

    public int? Status { get; init; }

    public string? Detail { get; init; }

    public string? Instance { get; init; }

    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }

    public string? ErrorKind { get; init; }

    public int? RetryAfterSeconds { get; init; }

    public int? TargetStatus { get; init; }

    public string? TraceId { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}

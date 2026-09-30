using System.Reflection;
using System.Text.Json;
using PinkRooster.WebLens.Client.Models;
using PinkRooster.WebLens.Client.Serialization;

namespace PinkRooster.WebLens.Client.Tests;

public class SerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = WebLensJsonSerializerContext.Default,
    };

    private static string FindOpenApiSnapshotPath()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(current, "tests", "PinkRooster.WebLens.Api.Tests", "Snapshots", "openapi-v1.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        throw new FileNotFoundException("Could not find openapi-v1.json snapshot file.");
    }

    [Fact]
    public void Source_generated_context_provides_type_info_for_all_client_models()
    {
        var context = WebLensJsonSerializerContext.Default;

        Assert.NotNull(context.GetTypeInfo(typeof(SearchRequest)));
        Assert.NotNull(context.GetTypeInfo(typeof(SearchResponse)));
        Assert.NotNull(context.GetTypeInfo(typeof(FetchRequest)));
        Assert.NotNull(context.GetTypeInfo(typeof(FetchResponse)));
        Assert.NotNull(context.GetTypeInfo(typeof(WebLensProblemDetails)));
        Assert.NotNull(context.GetTypeInfo(typeof(TimeRange)));
        Assert.NotNull(context.GetTypeInfo(typeof(SafeSearch)));
        Assert.NotNull(context.GetTypeInfo(typeof(SearchResultItem)));
        Assert.NotNull(context.GetTypeInfo(typeof(AnswerItem)));
        Assert.NotNull(context.GetTypeInfo(typeof(InfoboxItem)));
        Assert.NotNull(context.GetTypeInfo(typeof(EngineFailure)));
        Assert.NotNull(context.GetTypeInfo(typeof(SearchMeta)));
        Assert.NotNull(context.GetTypeInfo(typeof(FetchOptions)));
        Assert.NotNull(context.GetTypeInfo(typeof(PageMetadata)));
        Assert.NotNull(context.GetTypeInfo(typeof(FetchDiagnostics)));
    }

    [Fact]
    public void OpenApi_snapshot_properties_match_client_model_definitions()
    {
        var snapshotPath = FindOpenApiSnapshotPath();
        using var stream = File.OpenRead(snapshotPath);
        using var doc = JsonDocument.Parse(stream);

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        AssertModelMatchesSchema<SearchRequest>(schemas.GetProperty("SearchRequestDto"));
        AssertModelMatchesSchema<SearchResponse>(schemas.GetProperty("SearchResponseDto"));
        AssertModelMatchesSchema<SearchResultItem>(schemas.GetProperty("SearchResultItem"));
        AssertModelMatchesSchema<AnswerItem>(schemas.GetProperty("AnswerItem"));
        AssertModelMatchesSchema<InfoboxItem>(schemas.GetProperty("InfoboxItem"));
        AssertModelMatchesSchema<EngineFailure>(schemas.GetProperty("EngineFailure"));
        AssertModelMatchesSchema<SearchMeta>(schemas.GetProperty("SearchMeta"));

        AssertModelMatchesSchema<FetchRequest>(schemas.GetProperty("FetchRequestDto"));
        AssertModelMatchesSchema<FetchOptions>(schemas.GetProperty("FetchOptionsDto"));
        AssertModelMatchesSchema<FetchResponse>(schemas.GetProperty("FetchResponseDto"));
        AssertModelMatchesSchema<PageMetadata>(schemas.GetProperty("PageMetadata"));
        AssertModelMatchesSchema<FetchDiagnostics>(schemas.GetProperty("FetchDiagnostics"));
    }

    private static void AssertModelMatchesSchema<T>(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties))
        {
            return;
        }

        var modelProps = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var property in properties.EnumerateObject())
        {
            var openApiPropName = property.Name;
            Assert.True(
                modelProps.ContainsKey(openApiPropName),
                $"Model {typeof(T).Name} is missing property '{openApiPropName}' defined in OpenAPI schema.");
        }
    }

    [Fact]
    public void SearchRequest_round_trips_cleanly()
    {
        var request = new SearchRequest("test query")
        {
            Page = 2,
            Limit = 25,
            TimeRange = TimeRange.Month,
            SafeSearch = SafeSearch.Moderate,
            Categories = ["general", "news"],
            Engines = ["google", "duckduckgo"],
            Language = "en-US",
        };

        var json = JsonSerializer.Serialize(request, WebLensJsonSerializerContext.Default.SearchRequest);
        var deserialized = JsonSerializer.Deserialize(json, WebLensJsonSerializerContext.Default.SearchRequest);

        Assert.NotNull(deserialized);
        Assert.Equal("test query", deserialized.Query);
        Assert.Equal(2, deserialized.Page);
        Assert.Equal(25, deserialized.Limit);
        Assert.Equal(TimeRange.Month, deserialized.TimeRange);
        Assert.Equal(SafeSearch.Moderate, deserialized.SafeSearch);
        Assert.Equal(["general", "news"], deserialized.Categories);
        Assert.Equal(["google", "duckduckgo"], deserialized.Engines);
        Assert.Equal("en-US", deserialized.Language);
    }

    [Fact]
    public void SearchResponse_round_trips_cleanly()
    {
        var response = new SearchResponse
        {
            Query = "what is pink rooster",
            Results =
            [
                new SearchResultItem(
                    Url: "https://example.com/pink-rooster",
                    Title: "Pink Rooster Overview",
                    Snippet: "Pink Rooster is an intelligent software suite.",
                    Category: "general",
                    Engines: ["brave", "google"],
                    Score: 0.95,
                    PublishedAt: new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
                    ThumbnailUrl: "https://example.com/thumb.jpg",
                    ImageUrl: "https://example.com/image.jpg")
            ],
            Answers = [new AnswerItem("Pink Rooster is an intelligent suite.")],
            Suggestions = ["pink rooster weblens"],
            Corrections = [],
            Infoboxes = [new InfoboxItem("Pink Rooster", "Suite of AI tools", "https://example.com/wiki")],
            Meta = new SearchMeta(
                Page: 1,
                ResultCount: 1,
                Partial: false,
                EngineFailures: [new EngineFailure("searxng-fail", "Timeout")],
                DroppedItems: 0,
                ElapsedMs: 142,
                Cached: true)
        };

        var json = JsonSerializer.Serialize(response, WebLensJsonSerializerContext.Default.SearchResponse);
        var deserialized = JsonSerializer.Deserialize(json, WebLensJsonSerializerContext.Default.SearchResponse);

        Assert.NotNull(deserialized);
        Assert.Equal(response.Query, deserialized.Query);
        Assert.Single(deserialized.Results);
        var item = deserialized.Results[0];
        Assert.Equal("https://example.com/pink-rooster", item.Url);
        Assert.Equal("Pink Rooster Overview", item.Title);
        Assert.Equal(0.95, item.Score);
        Assert.Equal(["brave", "google"], item.Engines);
        Assert.Single(deserialized.Answers);
        Assert.Equal("Pink Rooster is an intelligent suite.", deserialized.Answers[0].Text);
        Assert.Single(deserialized.Infoboxes);
        Assert.Equal("Pink Rooster", deserialized.Infoboxes[0].Title);
        Assert.True(deserialized.Meta.Cached);
        Assert.Equal(142, deserialized.Meta.ElapsedMs);
    }

    [Fact]
    public void FetchRequest_and_Response_round_trips_cleanly()
    {
        var request = new FetchRequest("https://example.com/docs")
        {
            Options = new FetchOptions
            {
                ContentSelector = "article.main",
                ReadySelector = "div#loaded",
                ExcludeSelectors = [".nav", ".footer"],
                IncludeLinks = true,
                IncludeImages = false,
                MaxChars = 50000,
                TimeoutSeconds = 30,
                BypassCache = true,
            }
        };

        var reqJson = JsonSerializer.Serialize(request, WebLensJsonSerializerContext.Default.FetchRequest);
        var reqDeserialized = JsonSerializer.Deserialize(reqJson, WebLensJsonSerializerContext.Default.FetchRequest);

        Assert.NotNull(reqDeserialized);
        Assert.Equal("https://example.com/docs", reqDeserialized.Url);
        Assert.NotNull(reqDeserialized.Options);
        Assert.Equal("article.main", reqDeserialized.Options.ContentSelector);
        Assert.True(reqDeserialized.Options.BypassCache);
        Assert.Equal(50000, reqDeserialized.Options.MaxChars);

        var response = new FetchResponse
        {
            RequestedUrl = "https://example.com/docs",
            FinalUrl = "https://example.com/docs/v2",
            Title = "Documentation",
            Markdown = "# Documentation\n\nWelcome to docs.",
            Truncated = false,
            Metadata = new PageMetadata(
                Author: "Rooster Author",
                Language: "en",
                PublishedAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                SiteName: "Example Docs",
                Excerpt: "Documentation summary"),
            Diagnostics = new FetchDiagnostics(
                TargetStatus: 200,
                ExtractionStrategy: "semantic-main",
                QualityScore: 0.98,
                Attempts: 1,
                ReadinessTimedOut: false,
                SourceHtmlBytes: 15420,
                MarkdownChars: 38,
                RenderMs: 120,
                ExtractMs: 15,
                ConvertMs: 8,
                Cached: false,
                NavigationMs: 95,
                ReadinessMs: 25,
                Renderer: "http")
        };

        var respJson = JsonSerializer.Serialize(response, WebLensJsonSerializerContext.Default.FetchResponse);
        var respDeserialized = JsonSerializer.Deserialize(respJson, WebLensJsonSerializerContext.Default.FetchResponse);

        Assert.NotNull(respDeserialized);
        Assert.Equal("https://example.com/docs", respDeserialized.RequestedUrl);
        Assert.Equal("https://example.com/docs/v2", respDeserialized.FinalUrl);
        Assert.Equal("Documentation", respDeserialized.Title);
        Assert.Equal("# Documentation\n\nWelcome to docs.", respDeserialized.Markdown);
        Assert.False(respDeserialized.Truncated);
        Assert.Equal("Rooster Author", respDeserialized.Metadata.Author);
        Assert.Equal("http", respDeserialized.Diagnostics.Renderer);
        Assert.Equal(200, respDeserialized.Diagnostics.TargetStatus);
    }

    [Fact]
    public void ProblemDetails_deserializes_RFC9457_payload_and_extensions()
    {
        const string problemJson = """
            {
                "type": "urn:weblens:problem:validation",
                "title": "The request is not valid.",
                "status": 400,
                "detail": "Query must not be empty.",
                "instance": "/v1/search",
                "errorKind": "Validation",
                "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                "retryAfterSeconds": 5,
                "errors": {
                    "query": ["The Query field is required."]
                }
            }
            """;

        var problem = JsonSerializer.Deserialize(problemJson, WebLensJsonSerializerContext.Default.WebLensProblemDetails);

        Assert.NotNull(problem);
        Assert.Equal("urn:weblens:problem:validation", problem.Type);
        Assert.Equal("The request is not valid.", problem.Title);
        Assert.Equal(400, problem.Status);
        Assert.Equal("Query must not be empty.", problem.Detail);
        Assert.Equal("/v1/search", problem.Instance);
        Assert.Equal("Validation", problem.ErrorKind);
        Assert.Equal(5, problem.RetryAfterSeconds);
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", problem.TraceId);
        Assert.NotNull(problem.Errors);
        Assert.True(problem.Errors.ContainsKey("query"));
        Assert.Equal(["The Query field is required."], problem.Errors["query"]);
    }
}

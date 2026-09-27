using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PinkRooster.WebLens.Fetch;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>
/// The real host. Either the search service is replaced by a fake (<see cref="SearchBehavior"/>), or the real search module
/// runs against a fake network (<see cref="Network"/>). Configure both before the first request.
/// </summary>
public sealed class WebLensFactory : WebApplicationFactory<Program>
{
    public Func<SearchQuery, CancellationToken, Task<SearchResponse>>? SearchBehavior { get; set; }

    public Func<FetchRequest, CancellationToken, Task<FetchResult>>? FetchBehavior { get; set; }

    /// <summary>What the stub services' cache holds: null (the default) is a miss, so the call is charged and runs the behaviour.</summary>
    public Func<SearchQuery, SearchResponse?>? SearchCache { get; set; }

    public Func<FetchRequest, FetchResult?>? FetchCache { get; set; }

    public HttpMessageHandler? Network { get; set; }

    /// <summary>Not Development by default: the developer exception page would replace the problem+json 500.</summary>
    public string Environment { get; set; } = "Testing";

    public Dictionary<string, string?> Settings { get; } = [];

    public List<SearchQuery> SeenQueries { get; } = [];

    public List<FetchRequest> SeenFetches { get; } = [];

    /// <summary>Both scopes. Sent by <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> unless a test says otherwise.</summary>
    public const string AllScopesKey = "test-key-all";

    /// <summary>The <c>search</c> scope only.</summary>
    public const string SearchOnlyKey = "test-key-search-only";

    /// <summary>Every log line of the host, at every level, when a test asks for them.</summary>
    public CapturedLogs? Logs { get; set; }

    /// <summary>The key sent by default; null sends none.</summary>
    public string? ApiKey { get; set; } = AllScopesKey;

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (ApiKey is { } key)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        if (Logs is { } logs)
        {
            // A rule for this provider beats appsettings' "Default: Information", which would otherwise hide Debug and Trace.
            builder.ConfigureLogging(logging => logging.AddProvider(logs).AddFilter<CapturedLogs>(null, LogLevel.Trace));
        }

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["WebLens:Search:Instances:0:Name"] = "a",
                ["WebLens:Search:Instances:0:BaseUri"] = "https://a.test",
                ["WebLens:Search:Transport:ConnectRetryBackoff"] = "00:00:00",

                // These tests never reach a real browser; the fetch module's browser tests cover that.
                ["WebLens:Fetch:Browser:LaunchOnStart"] = "false",

                // SHA-256 of AllScopesKey and SearchOnlyKey.
                ["WebLens:Api:Keys:0:Id"] = "all",
                ["WebLens:Api:Keys:0:Sha256"] = "31a65195ae16798d1e0d6d435b997168cc1cc4175b7f8a46c1484ed962f7c041",
                ["WebLens:Api:Keys:0:Scopes:0"] = "search",
                ["WebLens:Api:Keys:0:Scopes:1"] = "fetch",
                ["WebLens:Api:Keys:1:Id"] = "search-only",
                ["WebLens:Api:Keys:1:Sha256"] = "20c8137a65dd49cff22b1637295d225e1c274aedd642edd6e9539d1b6239c139",
                ["WebLens:Api:Keys:1:Scopes:0"] = "search",
            };

            // Only Development and Testing may run without the egress proxy; elsewhere give the host one to point at.
            // Nothing here fetches, so it never has to answer.
            if (Environment is "Development" or "Testing")
            {
                values["WebLens:Fetch:Security:RequireEgressProxy"] = "false";
            }
            else
            {
                values["WebLens:Fetch:Security:EgressProxy:Server"] = "http://127.0.0.1:4750";

                // Required outside Development and Testing. Nothing listens on port 1, so L2 fails fast and L1 serves.
                values["WebLens:Api:ValkeyConnectionString"] = "127.0.0.1:1";
            }

            foreach (var (key, value) in Settings)
            {
                values[key] = value;
            }

            config.AddInMemoryCollection(values);
        });

        builder.ConfigureTestServices(services =>
        {
            if (SearchBehavior is { } behavior)
            {
                services.RemoveAll<ISearchService>();
                services.AddSingleton<ISearchService>(new DelegateSearchService((query, ct) =>
                {
                    SeenQueries.Add(query);
                    return behavior(query, ct);
                }, query => SearchCache?.Invoke(query)));
            }

            if (FetchBehavior is { } fetchBehavior)
            {
                services.RemoveAll<IWebFetchService>();
                services.AddSingleton<IWebFetchService>(new DelegateFetchService((request, ct) =>
                {
                    SeenFetches.Add(request);
                    return fetchBehavior(request, ct);
                }, request => FetchCache?.Invoke(request)));
            }

            if (Network is { } network)
            {
                // "WebLens.Search" is the module's named HttpClient.
                services.AddHttpClient("WebLens.Search").ConfigurePrimaryHttpMessageHandler(() => network);
            }
        });
    }

    private sealed class DelegateFetchService(Func<FetchRequest, CancellationToken, Task<FetchResult>> behavior, Func<FetchRequest, FetchResult?> cached) : IWebFetchService
    {
        public Task<FetchResult> FetchMarkdownAsync(FetchRequest request, CancellationToken ct) => behavior(request, ct);

        public Task<FetchResult?> TryGetCachedAsync(FetchRequest request, CancellationToken ct) => Task.FromResult(cached(request));
    }

    private sealed class DelegateSearchService(Func<SearchQuery, CancellationToken, Task<SearchResponse>> behavior, Func<SearchQuery, SearchResponse?> cached) : ISearchService
    {
        public Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken ct) => behavior(query, ct);

        public Task<SearchResponse?> TryGetCachedAsync(SearchQuery query, CancellationToken ct) => Task.FromResult(cached(query));
    }
}

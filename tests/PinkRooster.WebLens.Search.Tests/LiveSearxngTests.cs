using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PinkRooster.WebLens.Search.Tests;

/// <summary>
/// Runs against a real SearXNG instance. Skipped unless <c>WEBLENS_SEARXNG_URL</c> is set, for example
/// <c>WEBLENS_SEARXNG_URL=http://localhost:8080 dotnet test --project tests/PinkRooster.WebLens.Search.Tests</c>.
/// </summary>
public class LiveSearxngTests
{
    private const string Variable = "WEBLENS_SEARXNG_URL";

    private static ServiceProvider Build(params (string Name, string Uri, int Priority)[] instances)
    {
        var values = new Dictionary<string, string?>
        {
            ["Transport:AllowInsecureHttp"] = "true",
            ["Routing:Policy"] = "PriorityFailover",
            ["Transport:ConnectRetryBackoff"] = "00:00:00",
            ["Timeouts:Total"] = "00:00:20",
            ["Timeouts:Attempt"] = "00:00:10",
        };
        for (var i = 0; i < instances.Length; i++)
        {
            values[$"Instances:{i}:Name"] = instances[i].Name;
            values[$"Instances:{i}:BaseUri"] = instances[i].Uri;
            values[$"Instances:{i}:Priority"] = instances[i].Priority.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebLensSearch(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_real_search_returns_results()
    {
        var url = Environment.GetEnvironmentVariable(Variable);
        Assert.SkipUnless(!string.IsNullOrEmpty(url), $"Set {Variable} to run the live SearXNG tests.");

        using var provider = Build(("live", url!, 0));

        var response = await provider.GetRequiredService<ISearchService>().SearchAsync(new SearchQuery("dotnet") { Limit = 5 }, TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Results);
        Assert.All(response.Results, r => Assert.StartsWith("http", r.Url));
        Assert.Equal(InstanceStateKind.Healthy, provider.GetRequiredService<HealthRegistry>().Get("live").Kind);
    }

    [Fact]
    public async Task With_one_instance_down_the_search_still_succeeds_and_the_dead_one_is_marked()
    {
        var url = Environment.GetEnvironmentVariable(Variable);
        Assert.SkipUnless(!string.IsNullOrEmpty(url), $"Set {Variable} to run the live SearXNG tests.");

        // Nothing listens on port 9 (discard) of loopback, so this instance refuses connections. It has the better priority.
        using var provider = Build(("dead", "http://127.0.0.1:9", 0), ("live", url!, 1));
        var health = provider.GetRequiredService<HealthRegistry>();
        Assert.Equal(InstanceStateKind.Unknown, health.Get("dead").Kind);

        var response = await provider.GetRequiredService<ISearchService>().SearchAsync(new SearchQuery("dotnet") { Limit = 5 }, TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Results);
        Assert.Equal(InstanceStateKind.Degraded, health.Get("dead").Kind);
        Assert.Equal(InstanceStateKind.Healthy, health.Get("live").Kind);
    }

    [Fact]
    public async Task An_external_bang_is_refused_by_a_real_instance()
    {
        var url = Environment.GetEnvironmentVariable(Variable);
        Assert.SkipUnless(!string.IsNullOrEmpty(url), $"Set {Variable} to run the live SearXNG tests.");

        using var provider = Build(("live", url!, 0));

        var ex = await Assert.ThrowsAsync<SearchException>(() =>
            provider.GetRequiredService<ISearchService>().SearchAsync(new SearchQuery("!!ddg cats"), TestContext.Current.CancellationToken));

        Assert.Equal(SearchErrorKind.QueryUnsupported, ex.Kind);
    }

    [Fact]
    public async Task An_engine_with_a_space_in_its_name_works_in_underscore_form()
    {
        var url = Environment.GetEnvironmentVariable(Variable);
        Assert.SkipUnless(!string.IsNullOrEmpty(url), $"Set {Variable} to run the live SearXNG tests.");

        using var provider = Build(("live", url!, 0));

        var response = await provider.GetRequiredService<ISearchService>().SearchAsync(
            new SearchQuery("cats") { Engines = ["google_cse"] }, TestContext.Current.CancellationToken);

        Assert.All(response.Results, r => Assert.Contains("google cse", r.Engines));
    }
}

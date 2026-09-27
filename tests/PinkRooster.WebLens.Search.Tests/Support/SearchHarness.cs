using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace PinkRooster.WebLens.Search.Tests;

/// <summary>The real search module wired to a fake network and a fake clock.</summary>
internal sealed class SearchHarness : IDisposable
{
    private readonly ServiceProvider _provider;

    private SearchHarness(ServiceProvider provider, FakeSearxServer server, FakeTimeProvider time)
    {
        _provider = provider;
        Server = server;
        Time = time;
    }

    public FakeSearxServer Server { get; }
    public FakeTimeProvider Time { get; }
    public ISearchService Search => _provider.GetRequiredService<ISearchService>();
    public HealthRegistry Health => _provider.GetRequiredService<HealthRegistry>();
    public CapabilityCache Capabilities => _provider.GetRequiredService<CapabilityCache>();
    public InstanceSelector Selector => _provider.GetRequiredService<InstanceSelector>();

    /// <summary>Instances are named "a", "b", "c"... and live at https://a.test, https://b.test, ...</summary>
    public static SearchHarness Create(int instances = 2, Dictionary<string, string?>? settings = null, FakeSearxServer? server = null, TimeProvider? clock = null)
    {
        server ??= new FakeSearxServer();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

        var values = new Dictionary<string, string?>
        {
            ["Transport:ConnectRetryBackoff"] = "00:00:00",
            ["Routing:Policy"] = "PriorityFailover",

            // Off unless a test is about the cache: most tests count upstream requests.
            ["Cache:Enabled"] = "false",
        };
        for (var i = 0; i < instances; i++)
        {
            var name = ((char)('a' + i)).ToString();
            values[$"Instances:{i}:Name"] = name;
            values[$"Instances:{i}:BaseUri"] = $"https://{name}.test";
            values[$"Instances:{i}:Priority"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock ?? fakeTime);
        services.AddWebLensSearch(configuration);
        services.AddHttpClient(SearchServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => server);
        return new SearchHarness(services.BuildServiceProvider(), server, fakeTime);
    }

    public static SearchQuery Query(string text = "cats") => new(text);

    public void Dispose() => _provider.Dispose();
}

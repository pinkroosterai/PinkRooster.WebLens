using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PinkRooster.WebLens.Fetch.BrowserTests;

/// <summary>The real fetch module in real Chromium, against the fixture site. Readiness is shortened so tests stay quick.</summary>
internal sealed class FetchHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private FetchHarness(ServiceProvider provider, FixtureSite site, CapturedLogs logs)
    {
        _provider = provider;
        Site = site;
        Logs = logs;
    }

    public FixtureSite Site { get; }
    public CapturedLogs Logs { get; }
    public IWebFetchService Fetch => _provider.GetRequiredService<IWebFetchService>();
    public BrowserHost Browsers => _provider.GetRequiredService<BrowserHost>();
    public CapacityGate Capacity => _provider.GetRequiredService<CapacityGate>();

    /// <summary>
    /// In the Testing environment, without the proxy and with private networks allowed, since the fixture site is on
    /// loopback. The SSRF tests narrow that down themselves; <paramref name="extraPorts"/> joins the fixture's port in
    /// <c>AllowedPorts</c>.
    /// </summary>
    public static async Task<FetchHarness> CreateAsync(Dictionary<string, string?>? settings = null, params int[] extraPorts) =>
        CreateForSite(await FixtureSite.StartAsync(), settings, extraPorts);

    /// <summary>For a fixture site started beforehand, when something else (the egress proxy) must know its port first.</summary>
    public static Task<FetchHarness> CreateForSiteAsync(FixtureSite site, Dictionary<string, string?>? settings = null, params int[] extraPorts) =>
        Task.FromResult(CreateForSite(site, settings, extraPorts));

    private static FetchHarness CreateForSite(FixtureSite site, Dictionary<string, string?>? settings, int[] extraPorts)
    {
        var values = new Dictionary<string, string?>
        {
            ["Security:RequireEgressProxy"] = "false",
            ["Security:AllowPrivateNetworks"] = "true",
            ["Security:AllowedPorts:0"] = site.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Browser:LaunchOnStart"] = "false",

            // This host restricts unprivileged user namespaces, so Chromium's sandbox cannot start outside a container.
            ["Browser:ChromiumSandbox"] = "false",

            // Off unless a test is about the cache: most tests count what the fixture site received.
            ["Cache:Enabled"] = "false",
            ["Navigation:ReadinessTimeout"] = "00:00:03",
            ["Navigation:DomQuietPeriod"] = "00:00:00.200",
            ["Budget:RetryBackoff"] = "00:00:00.050",

            // These tests are about the browser path; HttpFirstTests turn the HTTP-first attempt on.
            ["HttpFirst:Enabled"] = "false",
        };
        for (var i = 0; i < extraPorts.Length; i++)
        {
            values[$"Security:AllowedPorts:{i + 1}"] = extraPorts[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        var logs = new CapturedLogs();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment());
        services.AddWebLensFetch(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return new FetchHarness(services.BuildServiceProvider(), site, logs);
    }

    public Task<FetchResult> FetchAsync(string path, Func<FetchRequest, FetchRequest>? configure = null, CancellationToken? ct = null)
    {
        var request = new FetchRequest(path.StartsWith("http", StringComparison.Ordinal) ? path : Site.Url(path));
        return Fetch.FetchMarkdownAsync(configure is null ? request : configure(request), ct ?? TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await Site.DisposeAsync();
    }
}

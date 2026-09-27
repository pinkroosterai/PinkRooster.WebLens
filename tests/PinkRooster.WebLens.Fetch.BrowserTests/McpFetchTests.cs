using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PinkRooster.WebLens.Fetch.BrowserTests;

/// <summary>
/// The MCP <c>fetch</c> tool in the whole host with real Chromium: a long page read in parts
/// joins to exactly what <c>POST /v1/fetch</c> returns, from one browser navigation, and the SSRF refusals hold through MCP
/// as they do through HTTP (the L1 URL guard and the redirect-chain check; the proxy's part is EgressProxyTests).
/// </summary>
public class McpFetchTests
{
    private const string Key = "test-key-all";

    [Fact]
    public async Task A_long_page_read_in_parts_joins_to_the_v1_markdown_from_one_navigation()
    {
        await using var site = await FixtureSite.StartAsync();
        await using var host = new Host(site.Port);
        await using var client = await host.ConnectAsync();

        var parts = new List<string>();
        var start = 0;
        string? fingerprint = null;
        while (true)
        {
            var text = await CallFetchAsync(client, new() { ["url"] = site.Url("/long"), ["start"] = start, ["fingerprint"] = fingerprint });
            parts.Add(text[(text.IndexOf("\n---\n", StringComparison.Ordinal) + 5)..]);
            var lines = text.Split('\n');
            var more = lines.SingleOrDefault(l => l.StartsWith("More follows", StringComparison.Ordinal));
            if (more is null)
            {
                break;
            }

            fingerprint = lines.Single(l => l.StartsWith("Fingerprint: ", StringComparison.Ordinal))["Fingerprint: ".Length..];
            start = int.Parse(more.Split("start=")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        }

        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", Key);
        var response = await http.PostAsJsonAsync("/v1/fetch", new { url = site.Url("/long") }, TestContext.Current.CancellationToken);
        var markdown = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("markdown").GetString();

        Assert.True(parts.Count >= 3, $"expected a page of several parts, got {parts.Count}");
        Assert.All(parts, p => Assert.True(p.Length <= 20_000));
        Assert.Equal(markdown, string.Concat(parts));
        Assert.Equal(1, site.Hits("/long"));
    }

    [Fact]
    public async Task A_private_target_is_refused_through_mcp_before_any_browser_work()
    {
        await using var site = await FixtureSite.StartAsync();
        await using var target = await FixtureSite.StartAsync("127.0.0.2");
        await using var host = new Host(site.Port, target.Port, deniedRange: "127.0.0.2/32");
        await using var client = await host.ConnectAsync();

        var result = await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["url"] = target.Url("/health/details") }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.StartsWith("urn:weblens:problem:target-not-allowed:", Text(result), StringComparison.Ordinal);
        Assert.Equal(0, target.TotalHits);
    }

    [Fact]
    public async Task A_redirect_to_a_private_target_returns_nothing_from_it_through_mcp()
    {
        await using var site = await FixtureSite.StartAsync();
        await using var target = await FixtureSite.StartAsync("127.0.0.2");
        await using var host = new Host(site.Port, target.Port, deniedRange: "127.0.0.2/32");
        await using var client = await host.ConnectAsync();

        var url = site.Url("/redirect-to?url=" + Uri.EscapeDataString(target.Url("/health/details")));
        var result = await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["url"] = url }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.StartsWith("urn:weblens:problem:target-not-allowed:", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-HEALTH-DETAILS", Text(result), StringComparison.Ordinal);
    }

    private static async Task<string> CallFetchAsync(McpClient client, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync("fetch", arguments, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError != true, Text(result));
        return Text(result);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>
    /// The real host in the Testing environment with the fetch module set up as <see cref="FetchHarness"/> sets it up (no
    /// proxy, loopback allowed, sandbox off on this host), but with the cache on: paging relies on it.
    /// </summary>
    private sealed class Host(int sitePort, int? extraPort = null, string? deniedRange = null) : WebApplicationFactory<Program>
    {
        public async Task<McpClient> ConnectAsync()
        {
            var http = CreateClient();
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Key },
                },
                http,
                ownsHttpClient: true);
            return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["WebLens:Search:Instances:0:Name"] = "a",
                    ["WebLens:Search:Instances:0:BaseUri"] = "https://a.test",
                    ["WebLens:Api:Keys:0:Id"] = "all",
                    ["WebLens:Api:Keys:0:Sha256"] = "31a65195ae16798d1e0d6d435b997168cc1cc4175b7f8a46c1484ed962f7c041",
                    ["WebLens:Api:Keys:0:Scopes:0"] = "search",
                    ["WebLens:Api:Keys:0:Scopes:1"] = "fetch",
                    ["WebLens:Fetch:Security:RequireEgressProxy"] = "false",
                    ["WebLens:Fetch:Security:AllowPrivateNetworks"] = "true",
                    ["WebLens:Fetch:Security:AllowedPorts:0"] = sitePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["WebLens:Fetch:Browser:LaunchOnStart"] = "false",
                    ["WebLens:Fetch:Browser:ChromiumSandbox"] = "false",
                    ["WebLens:Fetch:Navigation:ReadinessTimeout"] = "00:00:03",
                    ["WebLens:Fetch:Navigation:DomQuietPeriod"] = "00:00:00.200",
                    ["WebLens:Fetch:Cache:Enabled"] = "true",
                };
                if (extraPort is { } port)
                {
                    values["WebLens:Fetch:Security:AllowedPorts:1"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (deniedRange is not null)
                {
                    values["WebLens:Fetch:Security:DeniedRanges:0"] = deniedRange;
                }

                config.AddInMemoryCollection(values);
            });
        }
    }
}

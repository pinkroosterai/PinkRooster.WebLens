using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch.Tests;

public class FetchOptionsValidatorTests
{
    private static string? Failure(Dictionary<string, string?> settings, string environment = "Testing")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment(environment));
        services.AddWebLensFetch(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        try
        {
            _ = provider.GetRequiredService<IOptions<FetchOptions>>().Value;
            return null;
        }
        catch (OptionsValidationException ex)
        {
            return ex.Message;
        }
    }

    [Fact]
    public void The_defaults_need_an_egress_proxy()
    {
        Assert.Contains("RequireEgressProxy is set but no EgressProxy:Server", Failure([]));
        Assert.Null(Failure(new() { ["Security:EgressProxy:Server"] = "http://proxy.internal:4750" }));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void The_ssrf_relaxations_refuse_to_load_outside_development_and_testing(string environment)
    {
        Assert.Contains("RequireEgressProxy may be false only in Development or Testing", Failure(new() { ["Security:RequireEgressProxy"] = "false" }, environment));
        Assert.Contains("ChromiumSandbox may be false only in Development or Testing",
            Failure(new() { ["Security:EgressProxy:Server"] = "http://proxy.internal:4750", ["Browser:ChromiumSandbox"] = "false" }, environment));
        Assert.Contains("AllowPrivateNetworks may be true only in Development or Testing",
            Failure(new() { ["Security:EgressProxy:Server"] = "http://proxy.internal:4750", ["Security:AllowPrivateNetworks"] = "true" }, environment));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Development_and_testing_may_run_without_the_proxy_and_reach_private_networks(string environment)
    {
        Assert.Null(Failure(new() { ["Security:RequireEgressProxy"] = "false", ["Security:AllowPrivateNetworks"] = "true" }, environment));
    }

    [Theory]
    [InlineData("Browser:MaxConcurrentContexts", "0", "MaxConcurrentContexts")]
    [InlineData("Budget:NavigationAttempts", "0", "NavigationAttempts")]
    [InlineData("Budget:QueueTimeout", "00:00:00", "every timeout must be positive")]
    [InlineData("Navigation:NavigationTimeout", "00:00:25", "must not exceed Budget:OverallTimeout")]
    [InlineData("Capacity:MaxConcurrentPerOrigin", "0", "MaxConcurrentPerOrigin")]
    [InlineData("Security:EgressProxy:Server", "not a uri", "EgressProxy:Server")]
    [InlineData("Security:EgressProxy:Username", "u", "credentials are set but Server is not")]
    [InlineData("Extraction:DefaultExcludeSelectors:0", "div[", "not a valid CSS selector")]
    [InlineData("SiteProfiles:0:ContentSelector", "div[", "not a valid CSS selector")]
    [InlineData("Diagnostics:DumpOnFailure", "true", "DumpDirectory")]
    [InlineData("Security:DeniedRanges:0", "10.0.0.0/33", "not a CIDR range")]
    [InlineData("Security:AllowedPorts:0", "70000", "AllowedPorts")]
    [InlineData("HttpFirst:Timeout", "00:00:00", "HttpFirst:Timeout")]
    [InlineData("HttpFirst:Timeout", "00:00:30", "HttpFirst:Timeout")]
    public void Bad_settings_refuse_to_start(string key, string value, string message)
    {
        var failure = Failure(new() { [key] = value, ["SiteProfiles:0:Host"] = "example.org", ["Security:RequireEgressProxy"] = "false" });

        Assert.NotNull(failure);
        Assert.Contains(message, failure);
    }

    [Fact]
    public void A_complete_proxy_is_accepted()
    {
        Assert.Null(Failure(new()
        {
            ["Security:EgressProxy:Server"] = "http://proxy.internal:3128",
            ["Security:EgressProxy:Username"] = "weblens",
            ["Security:EgressProxy:Password"] = "secret",
        }));
    }
}

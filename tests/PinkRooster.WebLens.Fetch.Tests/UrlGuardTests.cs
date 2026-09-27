using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch.Tests;

/// <summary>The L1 URL guard, including the known bypass encodings (decimal, octal, hex and IPv4-mapped IPv6 forms). L1 is not the boundary; these prove it is a good first fence.</summary>
public class UrlGuardTests
{
    private sealed class FakeResolver(Dictionary<string, string[]> names) : IHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
            names.TryGetValue(host, out var addresses)
                ? Task.FromResult(addresses.Select(IPAddress.Parse).ToArray())
                : throw new SocketException((int)SocketError.HostNotFound);
    }

    private static UrlGuard Guard(Dictionary<string, string?>? security = null, Dictionary<string, string[]>? names = null)
    {
        var options = new FetchOptions();
        foreach (var (key, value) in security ?? [])
        {
            switch (key)
            {
                case "AllowPrivateNetworks": options.Security.AllowPrivateNetworks = bool.Parse(value!); break;
                case "DeniedRanges": options.Security.DeniedRanges.Add(value!); break;
                case "DeniedHostSuffixes": options.Security.DeniedHostSuffixes.Add(value!); break;
                case "AllowedPorts": options.Security.AllowedPorts.Add(int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture)); break;
            }
        }

        return new UrlGuard(Options.Create(options), new FakeResolver(names ?? new() { ["example.org"] = ["93.184.215.14"] }));
    }

    private static async Task<FetchException> RefusedAsync(UrlGuard guard, string url) =>
        await Assert.ThrowsAsync<FetchException>(() => guard.CheckAsync(new Uri(url), TestContext.Current.CancellationToken));

    public static TheoryData<string> BypassEncodings =>
    [
        "http://127.0.0.1/", "http://2130706433/", "http://0x7f000001/", "http://0177.0.0.1/", "http://127.1/", "http://0x7f.0.0.1/",
        "http://127.0.0.1./", "http://[::1]/", "http://[0:0:0:0:0:0:0:1]/", "http://[::ffff:127.0.0.1]/", "http://[::ffff:7f00:1]/",
        "http://localhost/", "http://LOCALHOST./", "http://ⓛocalhost/", "http://①②⑦.0.0.1/", "http://0/", "http://0.0.0.0/",
        "http://169.254.169.254/latest/meta-data/", "http://[fe80::1]/", "http://10.1.2.3/", "http://172.16.0.1/", "http://192.168.1.1/",
        "http://100.64.0.1/", "http://198.18.0.1/", "http://224.0.0.1/", "http://[fc00::1]/", "http://[64:ff9b::7f00:1]/", "http://[2002:7f00:1::]/",
    ];

    [Theory]
    [MemberData(nameof(BypassEncodings))]
    public async Task Private_loopback_and_special_addresses_are_refused_in_every_spelling(string url)
    {
        var ex = await RefusedAsync(Guard(), url);

        Assert.Equal(FetchErrorKind.TargetNotAllowed, ex.Kind);
        Assert.DoesNotContain("127", ex.Message);
        Assert.NotNull(ex.DenialReason);
    }

    [Theory]
    [InlineData("http://user:pass@example.org/")]
    [InlineData("http://127.0.0.1%2f@example.org/")]
    [InlineData("http://example.org:22/")]
    [InlineData("http://example.org:8080/")]
    [InlineData("https://example.org:25/")]
    public async Task Credentials_and_other_ports_are_refused(string url)
    {
        Assert.Equal(FetchErrorKind.TargetNotAllowed, (await RefusedAsync(Guard(), url)).Kind);
    }

    [Theory]
    [InlineData("ftp://example.org/")]
    [InlineData("file:///etc/passwd")]
    public void Non_http_schemes_are_refused(string url)
    {
        Assert.NotNull(Guard().CheckWithoutDns(new Uri(url)));
    }

    [Fact]
    public async Task A_public_name_on_the_default_ports_is_allowed()
    {
        var guard = Guard();

        await guard.CheckAsync(new Uri("http://example.org/"), TestContext.Current.CancellationToken);
        await guard.CheckAsync(new Uri("https://example.org/path?q=1"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_name_is_refused_when_any_of_its_addresses_is_private()
    {
        var guard = Guard(names: new() { ["mixed.test"] = ["93.184.215.14", "10.0.0.5"], ["rebind.test"] = ["::ffff:127.0.0.1"] });

        Assert.Equal(FetchErrorKind.TargetNotAllowed, (await RefusedAsync(guard, "http://mixed.test/")).Kind);
        Assert.Equal(FetchErrorKind.TargetNotAllowed, (await RefusedAsync(guard, "http://rebind.test/")).Kind);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_unavailable_not_refused()
    {
        Assert.Equal(FetchErrorKind.TargetUnavailable, (await RefusedAsync(Guard(), "http://no-such-host.test/")).Kind);
    }

    [Fact]
    public async Task Configured_ranges_and_suffixes_are_refused_even_when_private_networks_are_allowed()
    {
        var guard = Guard(
            new() { ["AllowPrivateNetworks"] = "true", ["DeniedRanges"] = "172.20.0.0/16", ["DeniedHostSuffixes"] = ".internal" },
            new() { ["searxng.test"] = ["172.20.0.3"], ["db.internal"] = ["93.184.215.14"] });

        await guard.CheckAsync(new Uri("http://127.0.0.1/"), TestContext.Current.CancellationToken);
        Assert.Equal(FetchErrorKind.TargetNotAllowed, (await RefusedAsync(guard, "http://searxng.test/")).Kind);
        Assert.Equal(FetchErrorKind.TargetNotAllowed, (await RefusedAsync(guard, "http://172.20.9.9/")).Kind);
        Assert.Equal(FetchErrorKind.TargetNotAllowed, (await RefusedAsync(guard, "http://db.internal/")).Kind);
        Assert.Equal(FetchErrorKind.TargetNotAllowed, (await RefusedAsync(guard, "http://internal/")).Kind);
    }

    [Fact]
    public async Task Allowed_ports_replace_the_defaults()
    {
        var guard = Guard(new() { ["AllowedPorts"] = "8443" });

        await guard.CheckAsync(new Uri("https://example.org:8443/"), TestContext.Current.CancellationToken);
        Assert.Equal(FetchErrorKind.TargetNotAllowed, (await RefusedAsync(guard, "https://example.org/")).Kind);
    }

    [Fact]
    public void The_launch_environment_carries_nothing_but_the_allowed_names()
    {
        Environment.SetEnvironmentVariable("WEBLENS_TEST_SECRET", "hunter2");
        try
        {
            var environment = BrowserHost.LaunchEnvironment();

            Assert.DoesNotContain("WEBLENS_TEST_SECRET", environment.Keys);
            Assert.All(environment.Keys, name => Assert.Contains(name, BrowserHost.LaunchEnvironmentNames));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBLENS_TEST_SECRET", null);
        }
    }
}

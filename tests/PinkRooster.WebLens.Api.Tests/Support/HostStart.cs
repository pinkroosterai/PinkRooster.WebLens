using System.Reflection;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>
/// Runs the real entry point (Program's top-level statements) with settings as command-line arguments, and returns why
/// it refused to start. Start-up refusals are tested this way, not through WebApplicationFactory: when a host fails to
/// start, the factory's own start can hit the container the app thread already disposed, and report an
/// ObjectDisposedException instead of the reason (seen under parallel load).
/// </summary>
internal static class HostStart
{
    public static async Task<Exception> FailureAsync(string environment, Dictionary<string, string> settings)
    {
        var values = new Dictionary<string, string>
        {
            ["urls"] = "http://127.0.0.1:0",
            ["WebLens:Search:Instances:0:Name"] = "a",
            ["WebLens:Search:Instances:0:BaseUri"] = "https://a.test",
            ["WebLens:Fetch:Browser:LaunchOnStart"] = "false",
            ["WebLens:Api:Keys:0:Id"] = "all",
            ["WebLens:Api:Keys:0:Sha256"] = "31a65195ae16798d1e0d6d435b997168cc1cc4175b7f8a46c1484ed962f7c041",
            ["WebLens:Api:Keys:0:Scopes:0"] = "search",
            ["WebLens:Api:ValkeyConnectionString"] = "127.0.0.1:1",
        };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        string[] args = ["--environment", environment, .. values.Select(kv => $"--{kv.Key}={kv.Value}")];
        var entryPoint = typeof(Program).Assembly.EntryPoint!;

        // Validation happens before the server binds, so a refused start returns quickly. A host that starts instead
        // would run until the process ends; the timeout turns that into a failed test rather than a hang.
        var run = Task.Run(() => entryPoint.Invoke(null, [args]));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(finished == run, "The host started instead of refusing.");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => run);
        return ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
    }
}

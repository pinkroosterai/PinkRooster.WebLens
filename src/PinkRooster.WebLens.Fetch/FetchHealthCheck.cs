using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// Fetch can work when the browser is up and the egress proxy is reachable. Every context busy for longer
/// than a threshold is "degraded": reported, but it does not take a full, working service out of rotation. Cached state only.
/// </summary>
internal sealed class FetchHealthCheck(BrowserHost browsers, EgressProbe probe, CapacityGate capacity) : IHealthCheck
{
    public const string Name = "fetch";

    private static readonly TimeSpan SaturationThreshold = TimeSpan.FromSeconds(30);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var generation = browsers.Current;
        var data = new Dictionary<string, object>
        {
            ["browserGeneration"] = generation?.Number ?? 0,
            ["browserUp"] = generation?.IsAlive ?? false,
            ["contextsInUse"] = capacity.InUse,
        };
        if (probe.ProxyReachable is { } reachable)
        {
            data["egressProxyReachable"] = reachable;
        }

        if (generation is not { IsAlive: true })
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The browser is not up.", data: data));
        }

        if (probe.ProxyReachable is false)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The egress proxy is not reachable.", data: data));
        }

        return Task.FromResult(probe.SaturatedFor > SaturationThreshold
            ? HealthCheckResult.Degraded("Every browser context has been busy for a while.", data: data)
            : HealthCheckResult.Healthy("The browser is up.", data));
    }
}

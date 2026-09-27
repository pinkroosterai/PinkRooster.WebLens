using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

/// <summary>
/// Search can work when at least one enabled instance is eligible. Reads the health registry only; a
/// health request never makes an outbound call. Instance names and states are for <c>/health/details</c>, never URLs.
/// </summary>
internal sealed class SearchHealthCheck(HealthRegistry health, IOptions<SearchOptions> options) : IHealthCheck
{
    public const string Name = "search";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var instances = options.Value.Instances.Where(i => i.Enabled).Select(i => i.Name).ToList();
        var data = instances.ToDictionary(name => name, name => (object)health.Get(name).Kind.ToString());
        var eligible = instances.Count(health.IsEligible);

        return Task.FromResult(eligible > 0
            ? HealthCheckResult.Healthy($"{eligible} of {instances.Count} instance(s) eligible.", data)
            : HealthCheckResult.Unhealthy("No search instance is eligible.", data: data));
    }
}

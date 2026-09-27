using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

/// <param name="Candidates">Instances to try, in order.</param>
/// <param name="UnofferedField">
/// Set when every enabled instance is known to lack a requested engine or category: the <see cref="SearchQuery"/> property at
/// fault. Retrying cannot help, so the caller reports an invalid query rather than an outage.
/// </param>
internal sealed record Selection(IReadOnlyList<SearchInstance> Candidates, string? UnofferedField);

/// <summary>Turns health and capability knowledge into an ordered candidate list for one search.</summary>
internal sealed class InstanceSelector
{
    private readonly IReadOnlyList<SearchInstance> _instances;
    private readonly HealthRegistry _health;
    private readonly CapabilityCache _capabilities;
    private readonly SearchOptions _options;
    private int _rotation = -1;

    public InstanceSelector(IOptions<SearchOptions> options, HealthRegistry health, CapabilityCache capabilities)
    {
        _options = options.Value;
        _health = health;
        _capabilities = capabilities;
        _instances = _options.Instances.Where(i => i.Enabled).Select(SearchInstance.From).ToList();
    }

    public IReadOnlyList<SearchInstance> Instances => _instances;

    public Selection Select(NormalizedQuery query)
    {
        // The capability snapshot is refreshed lazily and never awaited here; without a fresh one the request is sent anyway.
        foreach (var instance in _instances)
        {
            _capabilities.RefreshInBackground(instance);
        }

        // Capability before health: when the only instance offering an engine is down, that is an outage (503), not a bad query (400).
        var capable = new List<SearchInstance>();
        string? unoffered = null;
        foreach (var instance in _instances)
        {
            if (Unoffered(instance, query) is { } field)
            {
                unoffered ??= field;
                continue;
            }

            capable.Add(instance);
        }

        if (capable.Count == 0)
        {
            return new Selection([], unoffered);
        }

        var eligible = capable.Where(i => _health.IsEligible(i.Name)).ToList();

        var ordered = _options.Routing.Policy switch
        {
            RoutingPolicy.PriorityFailover => eligible.OrderBy(i => i.Priority).ToList(),
            RoutingPolicy.RoundRobin => Rotate(eligible, NextRotation()),
            _ => HealthAware(eligible),
        };

        return new Selection(ordered.Take(_options.Routing.MaxInstanceAttempts).ToList(), null);
    }

    private List<SearchInstance> HealthAware(List<SearchInstance> eligible)
    {
        var rotation = NextRotation();
        var result = new List<SearchInstance>();
        foreach (var tier in eligible.GroupBy(i => i.Priority).OrderBy(g => g.Key))
        {
            var preferred = tier.Where(i => _health.Get(i.Name).Kind != InstanceStateKind.Degraded).ToList();
            var degraded = tier.Where(i => _health.Get(i.Name).Kind == InstanceStateKind.Degraded).ToList();
            result.AddRange(Rotate(preferred, rotation));
            result.AddRange(degraded);
        }

        return result;
    }

    private int NextRotation() => Interlocked.Increment(ref _rotation) & int.MaxValue;

    private static List<SearchInstance> Rotate(List<SearchInstance> items, int rotation)
    {
        if (items.Count < 2)
        {
            return items;
        }

        var start = rotation % items.Count;
        return [.. items.Skip(start), .. items.Take(start)];
    }

    /// <summary>The requested property a fresh capability snapshot says this instance lacks, or null.</summary>
    private string? Unoffered(SearchInstance instance, NormalizedQuery query)
    {
        if (query.Engines.Count == 0 && query.Categories.Count == 0)
        {
            return null;
        }

        if (_options.Capabilities.MissingCapabilityBehavior == MissingCapabilityBehavior.SendAnyway)
        {
            return null;
        }

        if (_capabilities.GetFresh(instance.Name) is not { } snapshot)
        {
            return null;
        }

        if (query.Engines.Any(e => !snapshot.Engines.Contains(CapabilityCache.NormalizeEngineName(e))))
        {
            return nameof(SearchQuery.Engines);
        }

        return query.Categories.Any(c => !snapshot.Categories.Contains(c)) ? nameof(SearchQuery.Categories) : null;
    }
}

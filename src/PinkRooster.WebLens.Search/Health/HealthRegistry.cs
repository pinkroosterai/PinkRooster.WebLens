using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

/// <summary>
/// Per-instance health. The only circuit breaker in the search path: a resilience handler's breaker on the one named
/// <c>HttpClient</c> would open a single circuit across every instance.
/// Closed states (<c>Unknown</c>, <c>Healthy</c>, <c>Degraded</c>) take every report. Blocked states (an open circuit or a
/// timed cooldown) ignore reports from attempts that were already in flight, and are left only through a single half-open
/// probe once their time is up, so an expiring cooldown does not release every waiting request at once.
/// </summary>
internal sealed partial class HealthRegistry
{
    private const int MaxBreakMultiplier = 4;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private readonly Dictionary<string, StateBox> _states;
    private readonly TimeProvider _time;
    private readonly RoutingOptions _routing;
    private readonly CapabilityOptions _capabilities;
    private readonly ILogger<HealthRegistry> _logger;

    // No search outlives its total budget, so a probe permit held longer than that belongs to an attempt that is gone.
    private readonly TimeSpan _probeLease;

    public HealthRegistry(IOptions<SearchOptions> options, TimeProvider time, ILogger<HealthRegistry> logger)
    {
        _time = time;
        _logger = logger;
        _routing = options.Value.Routing;
        _capabilities = options.Value.Capabilities;
        _probeLease = options.Value.Timeouts.Total;
        _states = options.Value.Instances.ToDictionary(i => i.Name, _ => new StateBox(), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StateBox
    {
        public InstanceState State = InstanceState.Initial;
    }

    public InstanceState Get(string instance) => Volatile.Read(ref _states[instance].State);

    /// <summary>Whether a search may consider this instance right now. Does not take the half-open probe permit.</summary>
    public bool IsEligible(string instance)
    {
        var state = Get(instance);
        var now = _time.GetUtcNow();
        return state.Kind switch
        {
            InstanceStateKind.HalfOpen => !state.ProbeInFlight || Expired(state, now),
            var kind when IsBlocked(kind) => Expired(state, now),
            _ => true,
        };
    }

    /// <summary>The earliest moment a currently ineligible instance may be tried again, or null when none is waiting.</summary>
    public DateTimeOffset? EarliestRecovery()
    {
        DateTimeOffset? earliest = null;
        foreach (var name in _states.Keys)
        {
            var state = Get(name);
            if (!IsEligible(name) && state.Until is { } until && (earliest is null || until < earliest))
            {
                earliest = until;
            }
        }

        return earliest;
    }

    /// <summary>
    /// Called right before an attempt. When the instance is recovering (half-open, or blocked with its time up), only one
    /// attempt may go ahead: it gets <paramref name="isProbe"/> and must end with a report or <see cref="ReleaseProbe"/>.
    /// </summary>
    public bool TryBeginAttempt(string instance, out bool isProbe)
    {
        isProbe = false;
        var box = _states[instance];
        while (true)
        {
            var current = Volatile.Read(ref box.State);
            var now = _time.GetUtcNow();

            InstanceState next;
            if (current.Kind == InstanceStateKind.HalfOpen)
            {
                if (current.ProbeInFlight && !Expired(current, now))
                {
                    return false;
                }

                next = current with { ProbeInFlight = true, Until = now + _probeLease };
            }
            else if (IsBlocked(current.Kind))
            {
                if (!Expired(current, now))
                {
                    return false;
                }

                next = new InstanceState(InstanceStateKind.HalfOpen, now + _probeLease, BreakCount: current.BreakCount, ProbeInFlight: true);
            }
            else
            {
                return true;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref box.State, next, current), current))
            {
                LogIfChanged(instance, current, next);
                isProbe = true;
                return true;
            }
        }
    }

    public void ReportSuccess(string instance) =>
        Update(instance, current => IsBlocked(current.Kind) ? current : new InstanceState(InstanceStateKind.Healthy));

    public void ReportTransportFailure(string instance)
    {
        var now = _time.GetUtcNow();
        Update(instance, current =>
        {
            if (IsBlocked(current.Kind))
            {
                return current;
            }

            if (current.Kind == InstanceStateKind.HalfOpen)
            {
                return OpenCircuit(now, current.BreakCount + 1, _routing.CircuitFailureThreshold);
            }

            var failures = current.Kind is InstanceStateKind.Degraded ? current.ConsecutiveFailures + 1 : 1;
            return failures >= _routing.CircuitFailureThreshold
                ? OpenCircuit(now, current.BreakCount, failures)
                : new InstanceState(InstanceStateKind.Degraded, ConsecutiveFailures: failures, BreakCount: current.BreakCount);
        });
    }

    public void ReportRateLimited(string instance, TimeSpan? retryAfter)
    {
        var cooldown = retryAfter is { } ra && ra > TimeSpan.Zero ? (ra > MaxRetryAfter ? MaxRetryAfter : ra) : _routing.RateLimitedDefaultCooldown;
        Timed(instance, InstanceStateKind.RateLimited, cooldown);
    }

    public void ReportJsonUnsupported(string instance) => Timed(instance, InstanceStateKind.JsonUnsupported, _capabilities.Ttl);

    public void ReportProtocolIncompatible(string instance) => Timed(instance, InstanceStateKind.ProtocolIncompatible, _capabilities.Ttl);

    public void ReportAccessDenied(string instance) => Timed(instance, InstanceStateKind.AccessDenied, _routing.AccessDeniedCooldown);

    /// <summary>The probe ended without saying anything about the instance's health (bad query, caller left). Only its holder calls this.</summary>
    public void ReleaseProbe(string instance) =>
        Update(instance, current => current.Kind == InstanceStateKind.HalfOpen ? current with { ProbeInFlight = false, Until = null } : current);

    private static bool IsBlocked(InstanceStateKind kind) => kind is InstanceStateKind.OpenCircuit or InstanceStateKind.RateLimited
        or InstanceStateKind.JsonUnsupported or InstanceStateKind.ProtocolIncompatible or InstanceStateKind.AccessDenied;

    private static bool Expired(InstanceState state, DateTimeOffset now) => state.Until is { } until && now >= until;

    private InstanceState OpenCircuit(DateTimeOffset now, int breakCount, int failures)
    {
        var multiplier = Math.Min(breakCount + 1, MaxBreakMultiplier);
        return new InstanceState(InstanceStateKind.OpenCircuit, now + (_routing.BreakDuration * multiplier), failures, breakCount);
    }

    private void Timed(string instance, InstanceStateKind kind, TimeSpan duration)
    {
        var until = _time.GetUtcNow() + duration;
        Update(instance, current => IsBlocked(current.Kind) ? current : new InstanceState(kind, until, BreakCount: current.BreakCount));
    }

    private void Update(string instance, Func<InstanceState, InstanceState> change)
    {
        var box = _states[instance];
        while (true)
        {
            var current = Volatile.Read(ref box.State);
            var next = change(current);
            if (ReferenceEquals(next, current))
            {
                return;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref box.State, next, current), current))
            {
                LogIfChanged(instance, current, next);
                return;
            }
        }
    }

    private void LogIfChanged(string instance, InstanceState from, InstanceState to)
    {
        if (from.Kind != to.Kind)
        {
            LogStateChanged(IsBlocked(to.Kind) ? LogLevel.Warning : LogLevel.Information, instance, from.Kind, to.Kind, to.Until);
        }
    }

    [LoggerMessage(Message = "Search instance {Instance}: {From} -> {To} (until {Until})")]
    private partial void LogStateChanged(LogLevel level, string instance, InstanceStateKind from, InstanceStateKind to, DateTimeOffset? until);
}

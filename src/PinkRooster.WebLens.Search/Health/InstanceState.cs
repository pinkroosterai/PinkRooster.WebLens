namespace PinkRooster.WebLens.Search;

internal enum InstanceStateKind
{
    Unknown,
    Healthy,
    Degraded,
    OpenCircuit,
    HalfOpen,
    RateLimited,
    JsonUnsupported,
    ProtocolIncompatible,
    AccessDenied,
}

/// <summary>
/// Immutable; the registry swaps whole records so no lock is held across network calls. <see cref="Until"/> is when a
/// blocked state may be probed again, or, while <see cref="ProbeInFlight"/>, when the probe permit lapses.
/// </summary>
internal sealed record InstanceState(
    InstanceStateKind Kind,
    DateTimeOffset? Until = null,
    int ConsecutiveFailures = 0,
    int BreakCount = 0,
    bool ProbeInFlight = false)
{
    public static InstanceState Initial { get; } = new(InstanceStateKind.Unknown);
}

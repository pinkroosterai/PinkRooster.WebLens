using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>One origin's permits and circuits. Guarded by the registry's lock, except the semaphore.</summary>
internal sealed class OriginState(string origin, int permits)
{
    public string Origin { get; } = origin;
    public SemaphoreSlim Permits { get; } = new(permits, permits);
    public int InUse;
    public long LastUsedTicks;
    public int ConsecutiveFailures;
    public DateTimeOffset? FailureOpenUntil;
    public DateTimeOffset? BlockOpenUntil;
    public BlockKind BlockKind;
    public string? BlockProvider;
    public bool ProbeInFlight;

    public bool IsIdle(DateTimeOffset now) =>
        InUse == 0 && !ProbeInFlight && !(FailureOpenUntil > now) && !(BlockOpenUntil > now);
}

/// <summary>A fetch's hold on its origin's entry. Disposing it returns a probe permit the fetch never reported on.</summary>
internal sealed class OriginHandle(OriginRegistry registry, OriginState state) : IDisposable
{
    private int _disposed;

    public OriginState State { get; } = state;
    public bool HoldsProbe { get; set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            registry.Exit(this);
        }
    }
}

/// <summary>
/// Per-origin concurrency and circuits, keyed by <c>scheme://host:port</c>. Bounded, because callers choose
/// the keys: when full, the least recently used idle entry goes; when none is idle, the fetch is refused rather than letting
/// the registry grow. Both circuits close on their own after their duration and then allow a single probe.
/// </summary>
internal sealed partial class OriginRegistry(IOptions<FetchOptions> options, TimeProvider time, ILogger<OriginRegistry> logger)
{
    private readonly CapacityOptions _options = options.Value.Capacity;
    private readonly Dictionary<string, OriginState> _origins = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _origins.Count;
            }
        }
    }

    /// <summary>
    /// Takes a hold on the origin and checks its circuits: an open circuit fails fast without a browser. When a circuit's
    /// time is up, exactly one fetch goes through as the probe; the rest keep failing fast until it reports.
    /// </summary>
    public OriginHandle Enter(string origin)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (!_origins.TryGetValue(origin, out var state))
            {
                if (_origins.Count >= _options.OriginRegistryMaxEntries && !EvictOne(now))
                {
                    throw new FetchException(FetchErrorKind.CapacityExceeded, "The service is tracking too many sites to take another.") { RetryAfter = TimeSpan.FromSeconds(5) };
                }

                state = new OriginState(origin, _options.MaxConcurrentPerOrigin);
                _origins[origin] = state;
            }

            state.LastUsedTicks = now.UtcTicks;
            var handle = new OriginHandle(this, state);
            CheckCircuits(state, handle, now);
            state.InUse++;
            return handle;
        }
    }

    public void ReportSuccess(OriginHandle handle) => Update(handle, state =>
    {
        state.ConsecutiveFailures = 0;
        if (state.FailureOpenUntil is not null || state.BlockOpenUntil is not null)
        {
            LogClosed(state.Origin);
        }

        state.FailureOpenUntil = null;
        state.BlockOpenUntil = null;
    });

    public void ReportTransientFailure(OriginHandle handle) => Update(handle, state =>
    {
        state.ConsecutiveFailures++;
        if (handle.HoldsProbe || state.ConsecutiveFailures >= _options.FailureThreshold)
        {
            state.FailureOpenUntil = time.GetUtcNow() + _options.FailureBreakDuration;
            LogOpened(state.Origin, "failure", _options.FailureBreakDuration);
        }
    });

    public void ReportBlock(OriginHandle handle, BlockKind kind, string? provider) => Update(handle, state =>
    {
        state.BlockOpenUntil = time.GetUtcNow() + _options.BlockBreakDuration;
        state.BlockKind = kind;
        state.BlockProvider = provider;
        LogOpened(state.Origin, "block", _options.BlockBreakDuration);
    });

    internal void Exit(OriginHandle handle)
    {
        lock (_gate)
        {
            handle.State.InUse--;
            if (handle.HoldsProbe)
            {
                handle.State.ProbeInFlight = false;
            }
        }
    }

    private void Update(OriginHandle handle, Action<OriginState> change)
    {
        lock (_gate)
        {
            change(handle.State);
            if (handle.HoldsProbe)
            {
                handle.State.ProbeInFlight = false;
                handle.HoldsProbe = false;
            }
        }
    }

    private static void CheckCircuits(OriginState state, OriginHandle handle, DateTimeOffset now)
    {
        if (state.BlockOpenUntil is { } blockUntil)
        {
            if (now < blockUntil || state.ProbeInFlight)
            {
                // The same answer the challenge gave, without provoking another one.
                throw state.BlockKind == BlockKind.Captcha
                    ? new FetchException(FetchErrorKind.CaptchaRequired, "The target site recently asked for a CAPTCHA.") { Provider = state.BlockProvider }
                    : new FetchException(FetchErrorKind.BotChallenge, "The target site recently served a bot challenge.") { Provider = state.BlockProvider };
            }

            state.ProbeInFlight = true;
            handle.HoldsProbe = true;
            return;
        }

        if (state.FailureOpenUntil is { } failureUntil)
        {
            if (now < failureUntil || state.ProbeInFlight)
            {
                var remaining = failureUntil - now;
                throw new FetchException(FetchErrorKind.TargetUnavailable, "The target site has been failing; it is not being tried for now.")
                {
                    RetryAfter = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1),
                };
            }

            state.ProbeInFlight = true;
            handle.HoldsProbe = true;
        }
    }

    private bool EvictOne(DateTimeOffset now)
    {
        OriginState? oldest = null;
        foreach (var state in _origins.Values)
        {
            if (state.IsIdle(now) && (oldest is null || state.LastUsedTicks < oldest.LastUsedTicks))
            {
                oldest = state;
            }
        }

        if (oldest is null)
        {
            return false;
        }

        _origins.Remove(oldest.Origin);
        oldest.Permits.Dispose();
        return true;
    }

    // Host only (the registry key has no path or query), never the full URL.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Origin {Origin}: {Circuit} circuit opened for {Duration}")]
    private partial void LogOpened(string origin, string circuit, TimeSpan duration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Origin {Origin}: circuits closed")]
    private partial void LogClosed(string origin);
}

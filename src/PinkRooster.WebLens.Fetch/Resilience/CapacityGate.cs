using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>One held permit; disposing returns it once.</summary>
internal sealed class Permit(SemaphoreSlim semaphore) : IDisposable
{
    private int _released;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            semaphore.Release();
        }
    }
}

/// <summary>A fetch's per-origin and global permits, released together (global first).</summary>
internal sealed class CapacityLease(Permit origin, Permit global) : IDisposable
{
    public void Dispose()
    {
        global.Dispose();
        origin.Dispose();
    }
}

/// <summary>
/// Both concurrency bounds, taken before any browser context exists: per-origin FIRST, then global, so requests queued
/// for one busy origin hold nothing scarce. Waiting is bounded by <c>QueueTimeout</c> and by the fetch's remaining budget;
/// either running out is <see cref="FetchErrorKind.CapacityExceeded"/>. There is no unbounded queue. The HTTP-first attempt
/// holds only the origin permit (it is a request to the site, not a browser context); a fetch that goes on to the browser
/// then takes the global one.
/// </summary>
internal sealed class CapacityGate(IOptions<FetchOptions> options, TimeProvider time) : IDisposable
{
    private readonly FetchOptions _options = options.Value;
    private readonly SemaphoreSlim _global = new(options.Value.Browser.MaxConcurrentContexts, options.Value.Browser.MaxConcurrentContexts);

    /// <summary>Global permits in use. For health and tests.</summary>
    public int InUse => _options.Browser.MaxConcurrentContexts - _global.CurrentCount;

    public async Task<CapacityLease> AcquireAsync(OriginState origin, TimeSpan remainingBudget, CancellationToken ct)
    {
        var originPermit = await AcquireOriginAsync(origin, remainingBudget, ct);
        try
        {
            return new CapacityLease(originPermit, await AcquireBrowserAsync(remainingBudget, ct));
        }
        catch
        {
            originPermit.Dispose();
            throw;
        }
    }

    /// <summary>A slot on this origin: taken first, and all an HTTP-first attempt needs.</summary>
    public Task<Permit> AcquireOriginAsync(OriginState origin, TimeSpan remainingBudget, CancellationToken ct) =>
        WaitAsync(origin.Permits, remainingBudget, "This site already has as many fetches running as the service allows.", ct);

    /// <summary>A browser slot: taken after the origin's, only by a fetch that will render.</summary>
    public Task<Permit> AcquireBrowserAsync(TimeSpan remainingBudget, CancellationToken ct) =>
        WaitAsync(_global, remainingBudget, "Every browser slot is busy.", ct);

    private async Task<Permit> WaitAsync(SemaphoreSlim semaphore, TimeSpan remainingBudget, string busy, CancellationToken ct)
    {
        var queueTimeout = _options.Budget.QueueTimeout;
        var wait = remainingBudget < queueTimeout ? remainingBudget : queueTimeout;
        using var deadline = new CancellationTokenSource(wait > TimeSpan.Zero ? wait : TimeSpan.FromTicks(1), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        try
        {
            await semaphore.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FetchException(FetchErrorKind.CapacityExceeded, busy) { RetryAfter = queueTimeout };
        }

        return new Permit(semaphore);
    }

    public void Dispose() => _global.Dispose();
}

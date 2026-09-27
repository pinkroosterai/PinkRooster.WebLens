namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// Playwright calls take no <see cref="CancellationToken"/>. Waiting is abandoned on cancellation instead; the call itself
/// ends when the fetch closes its context, and its late failure is observed here so it never surfaces as unobserved.
/// </summary>
internal static class PlaywrightCalls
{
    public static async Task<T> WaitAsync<T>(Task<T> call, CancellationToken ct)
    {
        try
        {
            return await call.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = call.ContinueWith(static t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }

    public static async Task WaitAsync(Task call, CancellationToken ct)
    {
        try
        {
            await call.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = call.ContinueWith(static t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }
}

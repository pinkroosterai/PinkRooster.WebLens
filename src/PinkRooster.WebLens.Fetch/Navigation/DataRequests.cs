using Microsoft.Playwright;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// The page's data requests still in flight: fetch, XHR and script downloads. A short page is not
/// ready while one is pending, so an app still loading its code or data is waited for.
/// Fed by the page's request events, which Playwright raises on its own thread.
/// </summary>
internal sealed class DataRequests
{
    private readonly Lock _gate = new();
    private readonly HashSet<IRequest> _pending = [];
    private TaskCompletionSource _idle = Idle();

    /// <summary>Starts counting. Call before navigation, so the page's first requests are seen.</summary>
    public void Attach(IPage page)
    {
        page.Request += (_, request) =>
        {
            if (request.ResourceType is "fetch" or "xhr" or "script")
            {
                Add(request);
            }
        };
        page.RequestFinished += (_, request) => Remove(request);
        page.RequestFailed += (_, request) => Remove(request);
    }

    /// <summary>Completes when no data request is in flight; already complete when none is.</summary>
    public Task WhenIdle()
    {
        lock (_gate)
        {
            return _idle.Task;
        }
    }

    private void Add(IRequest request)
    {
        lock (_gate)
        {
            if (_pending.Add(request) && _pending.Count == 1)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void Remove(IRequest request)
    {
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            if (_pending.Remove(request) && _pending.Count == 0)
            {
                idle = _idle;
            }
        }

        idle?.TrySetResult();
    }

    private static TaskCompletionSource Idle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.SetResult();
        return idle;
    }
}

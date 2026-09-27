using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// Waits for a page to be ready, until the deadline: the ready selector, then a
/// quiet DOM, after which the page is ready when it has enough rendered text or, for a short page, when no data request
/// is in flight. Otherwise it waits for those requests and for quiet again. Running out of time is not an error:
/// extraction proceeds with what rendered, and diagnostics say readiness timed out.
/// </summary>
internal sealed class ReadinessStrategy(IOptions<FetchOptions> options, TimeProvider time)
{
    // Resolves true once the DOM's content has not changed for `quiet` ms, or false when `max` ms pass first. Attribute-only
    // changes (carousels, animations toggling classes) do not count: they would keep such a page from ever being quiet.
    private const string QuietScript = """
        ([quiet, max]) => new Promise(resolve => {
            let timer;
            const finish = result => { observer.disconnect(); clearTimeout(timer); clearTimeout(limit); resolve(result); };
            const observer = new MutationObserver(() => { clearTimeout(timer); timer = setTimeout(() => finish(true), quiet); });
            observer.observe(document, { subtree: true, childList: true, characterData: true });
            timer = setTimeout(() => finish(true), quiet);
            const limit = setTimeout(() => finish(false), max);
        })
        """;

    private const string TextLengthScript = "() => document.body?.innerText?.length ?? 0";

    private const string ScrollScript = """
        async ([max]) => {
            let scrolled = 0;
            while (scrolled < max) {
                const before = window.scrollY;
                window.scrollBy(0, window.innerHeight);
                await new Promise(r => setTimeout(r, 100));
                if (window.scrollY === before) break;
                scrolled += window.scrollY - before;
            }
        }
        """;

    private readonly NavigationOptions _options = options.Value.Navigation;

    /// <summary>True when the page became ready, false when the deadline came first.</summary>
    public async Task<bool> WaitAsync(IPage page, DataRequests dataRequests, string? readySelector, TimeSpan deadline, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        TimeSpan Left() => deadline - time.GetElapsedTime(started);

        try
        {
            if (readySelector is not null)
            {
                // "css=" keeps Playwright from reading the caller's string as one of its other selector languages.
                await PlaywrightCalls.WaitAsync(
                    page.WaitForSelectorAsync("css=" + readySelector, new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = Ms(Left()) }),
                    ct);
            }

            while (true)
            {
                if (Left() <= TimeSpan.Zero)
                {
                    return false;
                }

                var quiet = await PlaywrightCalls.WaitAsync(
                    page.EvaluateAsync<bool>(QuietScript, new[] { _options.DomQuietPeriod.TotalMilliseconds, Left().TotalMilliseconds }),
                    ct);
                if (!quiet)
                {
                    return false;
                }

                if (_options.MinimumRenderedTextCharacters <= 0
                    || await PlaywrightCalls.WaitAsync(page.EvaluateAsync<int>(TextLengthScript), ct) >= _options.MinimumRenderedTextCharacters)
                {
                    break;
                }

                // A short page: finished, unless it is still loading code or data that may yet render its content.
                var idle = dataRequests.WhenIdle();
                if (idle.IsCompleted)
                {
                    break;
                }

                await idle.WaitAsync(Left() > TimeSpan.Zero ? Left() : TimeSpan.Zero, time, ct);
            }

            if (_options.AutoScroll && Left() > TimeSpan.Zero)
            {
                await PlaywrightCalls.WaitAsync(page.EvaluateAsync(ScrollScript, new[] { _options.MaxAutoScrollDistance }), ct);
            }

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static float Ms(TimeSpan span) => (float)Math.Max(1, span.TotalMilliseconds);
}

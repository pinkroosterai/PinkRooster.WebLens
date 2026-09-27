using System.Diagnostics;

namespace PinkRooster.WebLens.Fetch.BrowserTests;

public class CapacityAndRecoveryTests
{
    [Fact]
    public async Task Excess_fetches_are_refused_promptly_when_every_context_is_busy()
    {
        await using var h = await FetchHarness.CreateAsync(new()
        {
            ["Browser:MaxConcurrentContexts"] = "1",
            ["Capacity:MaxConcurrentPerOrigin"] = "4",
            ["Budget:QueueTimeout"] = "00:00:00.500",
        });
        using var hold = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var busy = h.FetchAsync("/hang", ct: hold.Token);
        while (h.Capacity.InUse == 0)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync("/article"));
        clock.Stop();

        Assert.Equal(FetchErrorKind.CapacityExceeded, ex.Kind);
        Assert.NotNull(ex.RetryAfter);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"refusal took {clock.Elapsed}");

        await hold.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => busy);
    }

    [Fact]
    public async Task A_browser_that_crashes_between_fetches_is_replaced()
    {
        await using var h = await FetchHarness.CreateAsync();
        await h.FetchAsync("/article");
        var first = h.Browsers.Current!;

        await first.Browser.CloseAsync();
        var result = await h.FetchAsync("/article");

        Assert.Contains("Saltmere", result.Title);
        Assert.False(first.IsAlive);
        Assert.Equal(first.Number + 1, h.Browsers.Current!.Number);
    }

    [Fact]
    public async Task A_browser_that_crashes_during_a_fetch_is_replaced_and_the_fetch_retried()
    {
        await using var h = await FetchHarness.CreateAsync();
        await h.FetchAsync("/article");
        var first = h.Browsers.Current!;

        var fetch = h.FetchAsync("/slow");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await first.Browser.CloseAsync();
        var result = await fetch;

        Assert.Contains("Slow but fine", result.Title);
        Assert.Equal(2, result.Diagnostics.Attempts);
        Assert.Equal(first.Number + 1, h.Browsers.Current!.Number);
    }

    [Fact]
    public async Task The_browser_is_recycled_after_the_configured_number_of_contexts()
    {
        await using var h = await FetchHarness.CreateAsync(new() { ["Browser:RecycleAfterContexts"] = "2" });

        await h.FetchAsync("/article");
        var normal = System.Diagnostics.Stopwatch.StartNew();
        await h.FetchAsync("/article");
        normal.Stop();
        var before = h.Browsers.Current!;

        // The fetch that finds the browser due for recycling uses it and launches the next one in the background
        // (recycle-ahead): it must not wait for a launch. The launch still competes for CPU, measured at
        // +130 to +300 ms against +1.5 to 2.5 s for an inline launch, hence the 500 ms bound.
        var crossing = System.Diagnostics.Stopwatch.StartNew();
        await h.FetchAsync("/article");
        crossing.Stop();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (h.Browsers.Current!.Number == before.Number && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(crossing.Elapsed <= normal.Elapsed + TimeSpan.FromMilliseconds(500), $"normal {normal.ElapsedMilliseconds} ms, crossing {crossing.ElapsedMilliseconds} ms");
        Assert.Equal(before.Number + 1, h.Browsers.Current!.Number);
        await h.FetchAsync("/article");
    }
}

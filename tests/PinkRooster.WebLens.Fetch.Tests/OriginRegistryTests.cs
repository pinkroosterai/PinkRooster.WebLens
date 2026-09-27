namespace PinkRooster.WebLens.Fetch.Tests;

public class OriginRegistryTests
{
    private const string A = "https://a.test:443";

    private static Pipeline Create(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Capacity:FailureThreshold"] = "2",
            ["Capacity:FailureBreakDuration"] = "00:01:00",
            ["Capacity:BlockBreakDuration"] = "00:05:00",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return Pipeline.Create(settings);
    }

    private static void Fail(Pipeline p, int times)
    {
        for (var i = 0; i < times; i++)
        {
            using var handle = p.Origins.Enter(A);
            p.Origins.ReportTransientFailure(handle);
        }
    }

    [Fact]
    public void Consecutive_transient_failures_open_the_failure_circuit_with_its_remaining_time()
    {
        using var p = Create();
        Fail(p, 2);
        p.Time.Advance(TimeSpan.FromSeconds(20));

        var ex = Assert.Throws<FetchException>(() => p.Origins.Enter(A));

        Assert.Equal(FetchErrorKind.TargetUnavailable, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(40), ex.RetryAfter);
    }

    [Fact]
    public void After_the_break_one_probe_goes_through_and_its_success_closes_the_circuit()
    {
        using var p = Create();
        Fail(p, 2);
        p.Time.Advance(TimeSpan.FromMinutes(1));

        using (var probe = p.Origins.Enter(A))
        {
            Assert.True(probe.HoldsProbe);
            Assert.Throws<FetchException>(() => p.Origins.Enter(A));
            p.Origins.ReportSuccess(probe);
        }

        using var next = p.Origins.Enter(A);
        Assert.False(next.HoldsProbe);
    }

    [Fact]
    public void A_failed_probe_reopens_the_circuit()
    {
        using var p = Create();
        Fail(p, 2);
        p.Time.Advance(TimeSpan.FromMinutes(1));

        using (var probe = p.Origins.Enter(A))
        {
            p.Origins.ReportTransientFailure(probe);
        }

        Assert.Throws<FetchException>(() => p.Origins.Enter(A));
    }

    [Fact]
    public void A_probe_that_never_reports_returns_its_permit_when_released()
    {
        using var p = Create();
        Fail(p, 2);
        p.Time.Advance(TimeSpan.FromMinutes(1));

        p.Origins.Enter(A).Dispose();

        using var again = p.Origins.Enter(A);
        Assert.True(again.HoldsProbe);
    }

    [Fact]
    public void A_block_opens_the_block_circuit_and_repeats_the_same_answer_without_a_browser()
    {
        using var p = Create();
        using (var handle = p.Origins.Enter(A))
        {
            p.Origins.ReportBlock(handle, BlockKind.Captcha, "hcaptcha");
        }

        var ex = Assert.Throws<FetchException>(() => p.Origins.Enter(A));

        Assert.Equal(FetchErrorKind.CaptchaRequired, ex.Kind);
        Assert.Equal("hcaptcha", ex.Provider);
        p.Time.Advance(TimeSpan.FromMinutes(5));
        using var probe = p.Origins.Enter(A);
        Assert.True(probe.HoldsProbe);
    }

    [Fact]
    public void A_full_registry_evicts_the_least_recently_used_idle_origin()
    {
        using var p = Create(new() { ["Capacity:OriginRegistryMaxEntries"] = "2" });
        p.Origins.Enter("https://one.test:443").Dispose();
        p.Time.Advance(TimeSpan.FromSeconds(1));
        p.Origins.Enter("https://two.test:443").Dispose();

        p.Origins.Enter("https://three.test:443").Dispose();

        Assert.Equal(2, p.Origins.Count);
    }

    [Fact]
    public void A_full_registry_with_nothing_idle_refuses_rather_than_grows()
    {
        using var p = Create(new() { ["Capacity:OriginRegistryMaxEntries"] = "2" });
        using var one = p.Origins.Enter("https://one.test:443");
        using var two = p.Origins.Enter("https://two.test:443");

        var ex = Assert.Throws<FetchException>(() => p.Origins.Enter("https://three.test:443"));

        Assert.Equal(FetchErrorKind.CapacityExceeded, ex.Kind);
        Assert.Equal(2, p.Origins.Count);
    }

    [Fact]
    public void An_origin_with_an_open_circuit_is_never_evicted()
    {
        using var p = Create(new() { ["Capacity:OriginRegistryMaxEntries"] = "1" });
        Fail(p, 2);

        Assert.Throws<FetchException>(() => p.Origins.Enter("https://other.test:443"));
    }
}

public class CapacityGateTests
{
    private static Pipeline Create() => Pipeline.Create(new()
    {
        ["Browser:MaxConcurrentContexts"] = "2",
        ["Capacity:MaxConcurrentPerOrigin"] = "1",
        ["Budget:QueueTimeout"] = "00:00:02",
    });

    [Fact]
    public async Task A_busy_origin_refuses_after_the_queue_timeout_holding_no_global_permit()
    {
        using var p = Create();
        using var origin = p.Origins.Enter("https://a.test:443");
        using var first = await p.Capacity.AcquireAsync(origin.State, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var waiting = p.Capacity.AcquireAsync(origin.State, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, p.Capacity.InUse);
        p.Time.Advance(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<FetchException>(() => waiting);
        Assert.Equal(FetchErrorKind.CapacityExceeded, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(2), ex.RetryAfter);
    }

    [Fact]
    public async Task A_full_browser_refuses_and_returns_the_origin_permit()
    {
        using var p = Create();
        using var a = p.Origins.Enter("https://a.test:443");
        using var b = p.Origins.Enter("https://b.test:443");
        using var c = p.Origins.Enter("https://c.test:443");
        using var first = await p.Capacity.AcquireAsync(a.State, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var second = await p.Capacity.AcquireAsync(b.State, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var waiting = p.Capacity.AcquireAsync(c.State, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        p.Time.Advance(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<FetchException>(() => waiting);
        Assert.Equal(1, c.State.Permits.CurrentCount);
    }

    [Fact]
    public async Task The_wait_never_outlives_the_remaining_budget()
    {
        using var p = Create();
        using var origin = p.Origins.Enter("https://a.test:443");
        using var first = await p.Capacity.AcquireAsync(origin.State, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var waiting = p.Capacity.AcquireAsync(origin.State, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        p.Time.Advance(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<FetchException>(() => waiting);
    }

    [Fact]
    public async Task Released_permits_let_the_next_fetch_in()
    {
        using var p = Create();
        using var origin = p.Origins.Enter("https://a.test:443");
        var first = await p.Capacity.AcquireAsync(origin.State, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var waiting = p.Capacity.AcquireAsync(origin.State, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        first.Dispose();

        using var second = await waiting;
        Assert.Equal(1, p.Capacity.InUse);
    }
}

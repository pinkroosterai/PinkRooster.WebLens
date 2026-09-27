using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search.Tests;

public class AddWebLensSearchTests
{
    [Fact]
    public void Registers_the_search_service()
    {
        using var harness = SearchHarness.Create();

        Assert.NotNull(harness.Search);
    }

    [Fact]
    public void Wraps_the_search_in_the_limit_decorator()
    {
        using var harness = SearchHarness.Create();

        Assert.IsType<LimitingSearchService>(harness.Search);
    }

    [Fact]
    public void Startup_validation_rejects_a_configuration_without_instances()
    {
        using var harness = SearchHarness.Create(instances: 0);

        var ex = Assert.Throws<OptionsValidationException>(() => harness.Selector);

        Assert.Contains("at least one enabled instance", string.Join(' ', ex.Failures));
    }
}

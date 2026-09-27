namespace PinkRooster.WebLens.Search.Tests;

public class QueryNormalizerTests
{
    private static string Field(NormalizedQuery query, string name) => query.Fields.Single(f => f.Key == name).Value;

    [Fact]
    public void Trims_the_text_and_forces_json()
    {
        var query = QueryNormalizer.Normalize(new SearchQuery("  cats  "), 512);

        Assert.Equal("cats", Field(query, "q"));
        Assert.Equal("json", Field(query, "format"));
        Assert.Equal("1", Field(query, "pageno"));
        Assert.DoesNotContain(query.Fields, f => f.Key == "theme");
    }

    [Fact]
    public void Compiles_engines_into_bang_tokens()
    {
        var query = QueryNormalizer.Normalize(new SearchQuery("cats") { Engines = ["google_cse", "brave", "GOOGLE_CSE"] }, 512);

        Assert.Equal("!google_cse !brave cats", Field(query, "q"));
        Assert.Equal(["google_cse", "brave"], query.Engines);
    }

    [Fact]
    public void Maps_the_optional_parameters_to_their_searxng_names()
    {
        var query = QueryNormalizer.Normalize(
            new SearchQuery("cats")
            {
                Categories = ["general", "news"],
                Language = "en-GB",
                Page = 3,
                TimeRange = SearchTimeRange.Month,
                SafeSearch = SearchSafeSearch.Strict,
            },
            512);

        Assert.Equal("general,news", Field(query, "categories"));
        Assert.Equal("en-GB", Field(query, "language"));
        Assert.Equal("3", Field(query, "pageno"));
        Assert.Equal("month", Field(query, "time_range"));
        Assert.Equal("2", Field(query, "safesearch"));
    }

    [Fact]
    public void Passes_bang_syntax_typed_by_the_caller_through()
    {
        var query = QueryNormalizer.Normalize(new SearchQuery("!wp cats"), 512);

        Assert.Equal("!wp cats", Field(query, "q"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty_text(string text) =>
        Assert.Equal(SearchErrorKind.InvalidQuery, Assert.Throws<SearchException>(() => QueryNormalizer.Normalize(new SearchQuery(text), 512)).Kind);

    [Fact]
    public void Rejects_text_over_the_maximum() =>
        Assert.Equal(SearchErrorKind.InvalidQuery, Assert.Throws<SearchException>(() => QueryNormalizer.Normalize(new SearchQuery(new string('x', 11)), 10)).Kind);

    [Theory]
    [InlineData("!wp")]
    [InlineData("a b")]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("google cse")]
    public void Rejects_engine_tokens_that_could_inject_search_syntax(string token) =>
        Assert.Equal(
            SearchErrorKind.InvalidQuery,
            Assert.Throws<SearchException>(() => QueryNormalizer.Normalize(new SearchQuery("cats") { Engines = [token] }, 512)).Kind);

    [Theory]
    [InlineData("en us")]
    [InlineData("en_US")]
    [InlineData("<script>")]
    public void Rejects_malformed_languages(string language) =>
        Assert.Equal(
            SearchErrorKind.InvalidQuery,
            Assert.Throws<SearchException>(() => QueryNormalizer.Normalize(new SearchQuery("cats") { Language = language }, 512)).Kind);

    [Fact]
    public void Rejects_page_zero() =>
        Assert.Equal(SearchErrorKind.InvalidQuery, Assert.Throws<SearchException>(() => QueryNormalizer.Normalize(new SearchQuery("cats") { Page = 0 }, 512)).Kind);
}

using AngleSharp.Dom;

namespace PinkRooster.WebLens.Fetch.Tests;

/// <summary>App shells: what the HTTP-first path must hand to the browser.</summary>
public class AppShellTests
{
    private const int MinimumRenderedText = 200;

    private static IDocument Parse(string html) => ContentExtractor.Parse(html);

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("words", count));

    [Fact]
    public void An_empty_framework_root_with_a_script_is_a_shell()
    {
        var html = """<html><body><div id="root"></div><noscript>You need to enable JavaScript to run this app.</noscript><script src="/app.js"></script></body></html>""";

        Assert.True(AppShell.IsShell(Parse(html), MinimumRenderedText));
    }

    [Fact]
    public void A_hydration_payload_with_little_text_is_a_shell_even_with_a_long_script()
    {
        var html = $"""<html><body><div id="__next">Loading</div><script id="__NEXT_DATA__" type="application/json">{"{\"props\":\"" + Words(200) + "\"}"}</script></body></html>""";

        Assert.True(AppShell.IsShell(Parse(html), MinimumRenderedText));
    }

    [Fact]
    public void A_server_rendered_page_with_scripts_is_not_a_shell()
    {
        var html = $"""<html><body><article><h1>Title</h1><p>{Words(80)}</p></article><script id="__NEXT_DATA__" type="application/json">{"{}"}</script></body></html>""";

        Assert.False(AppShell.IsShell(Parse(html), MinimumRenderedText));
    }

    [Fact]
    public void A_short_page_without_script_is_not_a_shell()
    {
        var html = """<html><body><h1>Placeholder Domain</h1><p>This name is kept for use in examples.</p></body></html>""";

        Assert.False(AppShell.IsShell(Parse(html), MinimumRenderedText));
    }

    [Theory]
    [InlineData("Please enable JavaScript to use this site.", true)]
    [InlineData("JavaScript is required to view this page.", true)]
    [InlineData("This article explains how closures work in Python and Ruby.", false)]
    public void Content_that_asks_for_javascript_is_recognised(string text, bool asks)
    {
        var content = Parse($"<html><body><main><p>{text}</p></main></body></html>").QuerySelector("main")!;

        Assert.Equal(asks, AppShell.AsksForScript(content, 200));
    }

    [Fact]
    public void A_long_article_that_mentions_enabling_javascript_is_not_a_request_for_it()
    {
        var content = Parse($"<html><body><main><p>To enable JavaScript in Firefox, open the settings. {Words(250)}</p></main></body></html>").QuerySelector("main")!;

        Assert.False(AppShell.AsksForScript(content, 200));
    }
}

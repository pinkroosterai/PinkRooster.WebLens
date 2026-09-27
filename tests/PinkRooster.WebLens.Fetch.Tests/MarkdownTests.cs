using AngleSharp.Dom;

namespace PinkRooster.WebLens.Fetch.Tests;

public class MarkdownTests
{
    private static readonly Uri Base = new("https://example.org/docs/page");

    private static ConvertedMarkdown Convert(string html, bool includeLinks = true, bool includeImages = false, int maxChars = 100_000, Dictionary<string, string?>? settings = null)
    {
        using var pipeline = Pipeline.Create(settings);
        var content = ContentExtractor.Parse($"<html><body><div id=\"content\">{html}</div></body></html>").QuerySelector("#content")!;
        return pipeline.Converter.Convert(content, Base, includeLinks, includeImages, maxChars);
    }

    [Fact]
    public void Raw_html_never_reaches_the_output_but_code_keeps_its_angle_brackets()
    {
        var markdown = Convert("""
            <p>Text with <kbd>Ctrl</kbd> and <u>under</u> and <custom-tag data-x="1">custom</custom-tag>.</p>
            <details><summary>More</summary><p>Hidden detail</p></details>
            <div style="color:red"><span class="x">Styled</span></div>
            <pre><code class="language-html">&lt;div class="x"&gt;&lt;/div&gt;</code></pre>
            <p>Inline <code>&lt;T&gt;</code> code.</p>
            """).Markdown;

        var outsideCode = string.Join('\n', markdown.Split("```").Where((_, i) => i % 2 == 0)).Replace("`<T>`", "", StringComparison.Ordinal);
        Assert.DoesNotMatch("<[a-zA-Z/]", outsideCode);
        Assert.Contains("<div class=\"x\"></div>", markdown);
        Assert.Contains("`<T>`", markdown);
        Assert.Contains("Ctrl", markdown);
        Assert.Contains("custom", markdown);
    }

    [Fact]
    public void Relative_links_become_absolute_against_the_final_url()
    {
        var markdown = Convert("<p><a href=\"../guide/intro\">Intro</a> and <a href=\"/root\">root</a> and <a href=\"https://other.test/x\">other</a></p>").Markdown;

        Assert.Contains("[Intro](https://example.org/guide/intro)", markdown);
        Assert.Contains("[root](https://example.org/root)", markdown);
        Assert.Contains("[other](https://other.test/x)", markdown);
    }

    [Fact]
    public void Without_links_only_their_text_stays()
    {
        var markdown = Convert("<p>Read <a href=\"/a\">the guide</a> now.</p>", includeLinks: false).Markdown;

        Assert.Contains("Read the guide now.", markdown);
        Assert.DoesNotContain("](", markdown);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("#section")]
    public void Script_data_and_fragment_links_keep_only_their_text(string href)
    {
        var markdown = Convert($"<p>Click <a href=\"{href}\">here</a>.</p>").Markdown;

        Assert.Contains("Click here.", markdown);
        Assert.DoesNotContain("javascript", markdown);
    }

    [Fact]
    public void Images_are_dropped_by_default_keeping_their_alt_text()
    {
        var markdown = Convert("<p>Before <img src=\"/a.png\" alt=\"A chart\"> after <img src=\"/b.png\"></p>").Markdown;

        Assert.Contains("A chart", markdown);
        Assert.DoesNotContain("a.png", markdown);
        Assert.DoesNotContain("b.png", markdown);
    }

    [Fact]
    public void Kept_images_point_at_absolute_urls()
    {
        var markdown = Convert("<p><img src=\"img/a.png\" alt=\"A chart\"></p>", includeImages: true).Markdown;

        Assert.Contains("![A chart](https://example.org/docs/img/a.png)", markdown);
    }

    [Fact]
    public void Tables_become_pipe_tables_and_code_keeps_its_language()
    {
        var markdown = Convert("<table><thead><tr><th>A</th><th>B</th></tr></thead><tbody><tr><td>1</td><td>2</td></tr></tbody></table><pre><code class=\"language-csharp\">var x = 1;</code></pre>").Markdown;

        Assert.Contains("| A | B |", markdown);
        Assert.Contains("```csharp", markdown);
    }

    [Fact]
    public void Truncation_is_reported()
    {
        var html = string.Concat(Enumerable.Range(0, 50).Select(i => $"<p>Paragraph number {i} with some words in it.</p>"));

        var result = Convert(html, maxChars: 300);

        Assert.True(result.Truncated);
        Assert.True(result.Markdown.Length <= 300);
        Assert.EndsWith("in it.\n", result.Markdown);
    }
}

public class MarkdownNormalizerTests
{
    [Fact]
    public void Line_endings_blank_runs_and_trailing_space_are_normalised()
    {
        var result = MarkdownNormalizer.Normalize("\r\n\r\nOne  \r\n\r\n\r\n\r\nTwo\t\n\n\n", nfc: false);

        Assert.Equal("One\n\nTwo\n", result);
    }

    [Fact]
    public void Nbsp_and_zero_width_characters_are_cleaned_outside_code_only()
    {
        var input = "a b​c `x y​z`\n```\nkeep this​  \n\n\n\nand blank lines\n```\n";

        var result = MarkdownNormalizer.Normalize(input, nfc: false);

        Assert.StartsWith("a bc `x y​z`\n", result);
        Assert.Contains("keep this​  \n\n\n\nand blank lines\n```", result);
    }

    [Fact]
    public void Nfkc_is_never_applied_and_nfc_only_when_asked()
    {
        const string decomposed = "é ① ﬁ";

        Assert.Equal(decomposed + "\n", MarkdownNormalizer.Normalize(decomposed, nfc: false));
        Assert.Equal("é ① ﬁ\n", MarkdownNormalizer.Normalize(decomposed, nfc: true));
    }
}

public class MarkdownTruncatorTests
{
    [Fact]
    public void Short_markdown_is_untouched()
    {
        Assert.Equal(new ConvertedMarkdown("short\n", false), MarkdownTruncator.Truncate("short\n", 100));
    }

    [Fact]
    public void Cuts_at_a_block_boundary()
    {
        var result = MarkdownTruncator.Truncate("First block.\n\nSecond block that is longer.\n\nThird.\n", 30);

        Assert.True(result.Truncated);
        Assert.Equal("First block.\n", result.Markdown);
    }

    [Fact]
    public void Never_cuts_inside_a_fenced_block()
    {
        var markdown = "Intro.\n\n```\nline one\n\nline two\n```\n\nAfter.\n";

        var result = MarkdownTruncator.Truncate(markdown, 30);

        Assert.Equal("Intro.\n", result.Markdown);
    }

    [Fact]
    public void An_oversized_first_fenced_block_is_cut_by_line_and_closed()
    {
        var markdown = "```\n" + string.Concat(Enumerable.Range(0, 20).Select(i => $"line {i}\n")) + "```\n";

        var result = MarkdownTruncator.Truncate(markdown, 40);

        Assert.True(result.Markdown.Length <= 40);
        Assert.StartsWith("```\nline 0\n", result.Markdown);
        Assert.EndsWith("\n```\n", result.Markdown);
    }

    [Fact]
    public void One_enormous_line_is_cut_at_a_word()
    {
        var result = MarkdownTruncator.Truncate(string.Join(' ', Enumerable.Repeat("word", 100)) + "\n", 23);

        Assert.Equal("word word word word\n", result.Markdown);
    }
}

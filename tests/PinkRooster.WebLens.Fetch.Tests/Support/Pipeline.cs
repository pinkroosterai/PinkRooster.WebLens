using AngleSharp.Html.Dom;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace PinkRooster.WebLens.Fetch.Tests;

/// <summary>The fetch module's browser-free half, wired as in production: classification, extraction and conversion of saved HTML.</summary>
internal sealed class Pipeline : IDisposable
{
    private readonly ServiceProvider _provider;

    private Pipeline(ServiceProvider provider) => _provider = provider;

    public FakeTimeProvider Time => (FakeTimeProvider)_provider.GetRequiredService<TimeProvider>();
    public FetchOptions Options => _provider.GetRequiredService<IOptions<FetchOptions>>().Value;
    public ResponseClassifier Classifier => _provider.GetRequiredService<ResponseClassifier>();
    public ContentExtractor Extractor => _provider.GetRequiredService<ContentExtractor>();
    public MarkdownConverter Converter => _provider.GetRequiredService<MarkdownConverter>();
    public OriginRegistry Origins => _provider.GetRequiredService<OriginRegistry>();
    public CapacityGate Capacity => _provider.GetRequiredService<CapacityGate>();
    public QualityScorer Scorer => _provider.GetRequiredService<QualityScorer>();

    public static Pipeline Create(Dictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?> { ["Browser:LaunchOnStart"] = "false", ["Security:RequireEgressProxy"] = "false" };
        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)));
        services.AddWebLensFetch(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return new Pipeline(services.BuildServiceProvider());
    }

    public NormalizedFetch Normalize(FetchRequest request) => FetchRequestNormalizer.Normalize(request, Options);

    public static RenderedPage Page(string html, string url = "https://corpus.test/page", int status = 200, Dictionary<string, string>? headers = null, string contentType = "text/html; charset=utf-8")
    {
        var all = new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase) { ["content-type"] = contentType };
        return new RenderedPage(new Uri(url), status, all, contentType, html, html.Length, false, 0);
    }

    /// <summary>Everything a fetch does after the browser: classify, extract, convert.</summary>
    public async Task<PipelineResult> RunAsync(RenderedPage page, FetchRequest? request = null)
    {
        var fetch = Normalize(request ?? new FetchRequest(page.FinalUrl.AbsoluteUri));
        IHtmlDocument? document = page.IsHtml ? ContentExtractor.Parse(page.Html) : null;
        var verdict = Classifier.Classify(page, document);
        if (verdict.Error is not null)
        {
            return new PipelineResult(verdict, null, null);
        }

        // Classification passes only HTML, and HTML was parsed above.
        var baseUrl = ContentExtractor.BaseUrl(document!, page.FinalUrl);
        var extracted = await Extractor.ExtractAsync(document!, fetch, CancellationToken.None);
        var markdown = extracted is null ? null : Converter.Convert(extracted.Content, baseUrl, fetch.IncludeLinks, fetch.IncludeImages, fetch.MaxChars, extracted.Strategy == ContentExtractor.Listing);
        return new PipelineResult(verdict, extracted, markdown);
    }

    public void Dispose() => _provider.Dispose();
}

internal sealed record PipelineResult(Classification Verdict, ExtractedContent? Extracted, ConvertedMarkdown? Markdown);

internal static class Corpus
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Corpus", name));
}

# PinkRooster.WebLens.Client

An idiomatic, strongly-typed .NET 10 client library for the [WebLens](https://github.com/pinkroosterai/PinkRooster.WebLens) search and fetch API.

Features:
- **Search and Fetch** — Execute meta-searches across search engines and extract clean Markdown from web pages.
- **NativeAOT & Trim Compatible** — Source-generated JSON serialization with zero reflection.
- **Built-in Resilience** — Automatic retries with exponential backoff on transient errors (502, 503, 504, 429) and `Retry-After` header adherence.
- **Granular Typed Exceptions** — RFC 9457 Problem Details mapped directly to specific C# exception types.
- **Flexible Lifecycles** — Use via `IServiceCollection` dependency injection or standalone instantiation.

## Installation

```bash
dotnet add package PinkRooster.WebLens.Client
```

## Quick Start

### 1. Dependency Injection Setup

Register WebLens in your `Program.cs` or service configuration:

```csharp
using PinkRooster.WebLens.Client;

var builder = WebApplication.CreateBuilder(args);

// Configure via options delegate:
builder.Services.AddWebLensClient(options =>
{
    options.BaseAddress = new Uri("https://weblens.example.com/");
    options.ApiKey = builder.Configuration["WebLens:ApiKey"]!;
    options.Timeout = TimeSpan.FromSeconds(30);
});

// Or bind directly from configuration (e.g. appsettings.json):
// builder.Services.AddWebLensClient(builder.Configuration.GetSection("WebLens:Client"));
```

Inject and use `IWebLensClient`:

```csharp
public class SearchAgent(IWebLensClient client)
{
    public async Task RunAsync()
    {
        var response = await client.SearchAsync(new SearchRequest("machine learning agents")
        {
            Limit = 5,
            SafeSearch = SafeSearch.Moderate
        });

        foreach (var item in response.Results)
        {
            Console.WriteLine($"{item.Title} - {item.Url}");
        }
    }
}
```

### 2. Standalone Instantiation

For console utilities, background workers, or scripts without a host container:

```csharp
using PinkRooster.WebLens.Client;
using PinkRooster.WebLens.Client.Models;

var options = new WebLensClientOptions
{
    BaseAddress = new Uri("https://weblens.example.com/"),
    ApiKey = "wl_secret_api_key_here",
    Timeout = TimeSpan.FromSeconds(30)
};

using var client = new WebLensClient(options);

// Fetch a web page as clean Markdown:
var fetchResponse = await client.FetchAsync(new FetchRequest("https://example.com/blog/article")
{
    Options = new FetchOptions
    {
        IncludeImages = false,
        MaxChars = 20000
    }
});

Console.WriteLine($"Title: {fetchResponse.Title}");
Console.WriteLine(fetchResponse.Markdown);
```

### 3. Error Handling

WebLens errors return RFC 9457 problem details, mapped to granular exception types deriving from `WebLensApiException`:

```csharp
using PinkRooster.WebLens.Client.Exceptions;

try
{
    var result = await client.FetchAsync(new FetchRequest("https://internal.lan/status"));
}
catch (WebLensRateLimitedException ex)
{
    Console.WriteLine($"Rate limited. Retry after {ex.RetryAfter?.TotalSeconds} seconds.");
}
catch (WebLensValidationException ex)
{
    Console.WriteLine($"Validation error: {ex.Message}");
    foreach (var (field, errors) in ex.Errors)
    {
        Console.WriteLine($"  {field}: {string.Join(", ", errors)}");
    }
}
catch (WebLensChallengeException)
{
    Console.WriteLine("The target website presented a bot challenge or CAPTCHA.");
}
catch (WebLensTargetNotAllowedException)
{
    Console.WriteLine("Target URL is blocked (e.g. private network IP).");
}
catch (WebLensUnavailableException ex)
{
    Console.WriteLine($"Service or upstream unavailable (status: {(int)ex.StatusCode}).");
}
catch (WebLensApiException ex)
{
    Console.WriteLine($"API error: {(int)ex.StatusCode} - {ex.Message}");
}
```

### 4. Health Checks

Check if the WebLens instance is reachable and answering live probes:

```csharp
bool isHealthy = await client.CheckHealthAsync();
```

## License

MIT

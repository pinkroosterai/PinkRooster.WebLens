using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>The OpenAPI 3.1 document is part of the contract, so it is snapshotted and any change is a reviewed diff.</summary>
public class OpenApiTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string SnapshotPath([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Snapshots", "openapi-v1.json");

    [Fact]
    public async Task The_document_matches_the_committed_snapshot()
    {
        await using var factory = new WebLensFactory();
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var actual = JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, Indented).ReplaceLineEndings("\n") + "\n";

        var path = SnapshotPath();
        var expected = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
        if (expected != actual)
        {
            // Review the difference; if it is intended, replace the snapshot with the .received file.
            File.WriteAllText(Path.ChangeExtension(path, ".received.json"), actual);
            Assert.Fail($"The OpenAPI document changed. See {Path.ChangeExtension(path, ".received.json")}.");
        }
    }

    [Fact]
    public async Task The_document_declares_the_api_key_and_requires_it_on_v1()
    {
        await using var factory = new WebLensFactory();
        using var client = factory.CreateClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).RootElement;

        Assert.StartsWith("3.1", document.GetProperty("openapi").GetString());
        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());
        foreach (var path in new[] { "/v1/search", "/v1/fetch" })
        {
            var operation = document.GetProperty("paths").GetProperty(path).GetProperty("post");
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("ApiKey", out _), $"{path} does not require the key");
        }
    }

    [Fact]
    public async Task Outside_development_the_document_needs_a_key()
    {
        await using var factory = new WebLensFactory { ApiKey = null };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

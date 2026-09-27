namespace PinkRooster.WebLens.Search.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public const string Search = "searxng-2026.9.22-search.json";
    public const string Unresponsive = "searxng-2026.9.22-unresponsive.json";
    public const string Config = "searxng-2026.9.22-config.json";
    public const string LegacyShape = "synthetic-legacy-shape.json";
}

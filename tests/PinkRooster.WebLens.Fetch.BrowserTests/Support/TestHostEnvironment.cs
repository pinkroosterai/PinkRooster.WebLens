using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace PinkRooster.WebLens.Fetch.BrowserTests;

/// <summary>The host environment the fetch module's start-up rules read. "Testing" allows the SSRF relaxations.</summary>
internal sealed class TestHostEnvironment(string name = "Testing") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "PinkRooster.WebLens.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

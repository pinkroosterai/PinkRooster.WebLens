using PinkRooster.WebLens.Api.Hosting;
using PinkRooster.WebLens.Fetch;
using PinkRooster.WebLens.Search;

var builder = WebApplication.CreateBuilder(args);

builder
    .AddWebLensOptions()
    .AddWebLensJson()
    .AddWebLensProblemDetails()
    .AddWebLensAuth()
    .AddWebLensRequestTimeouts()
    .AddWebLensCache()
    .AddWebLensServer();

builder.Services
    .AddWebLensRateLimiting()
    .AddWebLensHealth()
    .AddWebLensOpenApi()
    .AddWebLensMcp()
    .AddWebLensSearch(builder.Configuration.GetSection("WebLens:Search"))
    .AddWebLensFetch(builder.Configuration.GetSection("WebLens:Fetch"));

var app = builder.Build();

app.UseWebLensPipeline();
app.MapWebLensEndpoints();
app.Run();

// Lets WebApplicationFactory<Program> see the entry point from the API tests.
public partial class Program;

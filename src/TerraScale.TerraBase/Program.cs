using System.Net;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Features.Management;
using TerraScale.TerraBase.Features.Pressure;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Hosting;

// Match the slim host's environment precedence; WebApplicationFactory supplies an argument override.
var environmentName = args.LastOrDefault(argument => argument.StartsWith("--environment=", StringComparison.Ordinal))?[14..]
    ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? Environments.Production;
TerraBaseOptions options;
try
{
    options = TerraBaseOptions.FromProcess(args);
    if (options.Region.Length == 0)
    {
        options = options with
        {
            Region = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
                ? "development"
                : throw new TerraBaseConfigurationException("region is required outside Development (TERRABASE_REGION or --region=value)"),
        };
    }
}
catch (TerraBaseConfigurationException error)
{
    // Configuration messages name settings, never their values.
    await Console.Error.WriteLineAsync($"terrabase: {error.Message}");
    return 2;
}

// Settings come only from TerraBaseOptions. The slim builder still adds environment variables
// and appsettings files, and Kestrel would bind listeners from a "Kestrel" section in them
// (for example Kestrel__Endpoints__X__Url): drop every configuration source.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = environmentName });
builder.Configuration.Sources.Clear();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // The administrative listener is loopback-only by construction; it is never configurable.
    kestrel.Listen(IPAddress.Loopback, options.AdminPort, listen => listen.UseTerraBaseListener(TerraBaseListener.Admin));
    kestrel.AddTerraBaseClientListener(options);
});
// Per-request framework logs are metrics' job; keep the log stream for events.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, TerraBaseJsonContext.Default));
builder.Services.AddTerraScaleEndpoints(endpoints =>
{
    endpoints.Title = "TerraBase API";
    endpoints.Version = "v1";
});
builder.Services.AddTerraBaseRegionOpenApi();
builder.Services.AddTerraBaseRequestBodies("v1");
builder.Services.AddTerraBaseTelemetry();
builder.Services.AddTerraBaseEngine(options);
builder.Services.AddTerraBaseApiKeys();
builder.Services.AddTerraBaseDocuments();
builder.Services.AddTerraBaseManagement();
builder.Services.AddTerraBasePressure();
builder.AddTerraBaseReplication(options);

var app = builder.Build();
try
{
    // The engine takes the data directory's lock and checks its layout before anything is served.
    _ = app.Services.GetRequiredService<IStorageEngine>();
}
catch (TerraBaseConfigurationException error)
{
    await Console.Error.WriteLineAsync($"terrabase: {error.Message}");
    await app.DisposeAsync();
    return 2;
}

// Error bodies first, so they also cover the listener check's 404.
app.UseTerraBasePressure();
app.UseTerraBaseBadRequests();
app.UseTerraBaseListeners();
app.UseAuthentication();
app.UseAuthorization();
app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();
app.MapTerraBaseMetrics();
app.MapTerraBaseReplication();
await app.RunAsync();
return 0;

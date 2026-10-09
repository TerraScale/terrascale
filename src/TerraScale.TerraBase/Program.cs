using System.Net;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.ApiReference;
using TerraBase.Features.Backup;
using TerraBase.Features.CrossRegion;
using TerraBase.Features.Documents;
using TerraBase.Features.Health;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Pressure;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

// Match the slim host's environment precedence; WebApplicationFactory supplies an argument override.
var environmentName = args.LastOrDefault(argument => argument.StartsWith("--environment=", StringComparison.Ordinal))?[14..]
    ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? Environments.Production;
TerraBaseOptions? options = null;
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
    options?.Dispose();
    // Configuration messages name settings, never their values.
    await Console.Error.WriteLineAsync($"terrabase: {error.Message}");
    return 2;
}

// The externally registered options owns any certificates not handed to a service, even if building fails.
using var configuration = options;

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

var runCompletion = new ApplicationRunCompletion();
builder.Services.AddSingleton(runCompletion);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(json =>
{
    // Stored documents can reach SQLite's depth bound; response envelopes add a few levels.
    json.SerializerOptions.MaxDepth = TerraBaseJsonContext.Default.Options.MaxDepth;
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, TerraBaseJsonContext.Default);
});
builder.Services.AddTerraScaleEndpoints(endpoints =>
{
    endpoints.Title = "TerraBase API";
    endpoints.Version = "v1";
});
builder.Services.AddTerraBaseApiReference("v1");
builder.Services.AddTerraBaseRegionOpenApi();
builder.Services.AddTerraBaseRequestBodies("v1");
builder.Services.AddTerraBaseApiExamples("v1");
builder.Services.AddTerraBaseTelemetry();
builder.Services.AddTerraBaseMemory(options);
builder.Services.AddTerraBaseQos();
builder.Services.AddTerraBaseEngine(options);
builder.Services.AddTerraBaseApiKeys();
builder.Services.AddTerraBaseDocuments();
builder.Services.AddTerraBaseUserAuth();
builder.Services.AddTerraBaseManagement();
builder.Services.AddTerraBaseCluster(options);
builder.Services.AddTerraBaseBackup(options);
builder.Services.AddTerraBaseHealth();
builder.Services.AddTerraBasePressure();
builder.AddTerraBaseReplication(options);
builder.Services.AddTerraBaseCrossRegion(options);

try
{
    // Dispose startup certificates before publishing completion; the outer scope also covers setup failures.
    using var runConfiguration = configuration;
    await using var app = builder.Build();
    try
    {
        // The engine takes the data directory's lock and checks its layout before anything is served.
        _ = app.Services.GetRequiredService<IStorageEngine>();
    }
    catch (TerraBaseConfigurationException error)
    {
        await Console.Error.WriteLineAsync($"terrabase: {error.Message}");
        return 2;
    }

    // Forwarded requests run through everything after it. Error bodies first, so they also cover the listener check's 404.
    app.UseTerraBaseForwardedRequests();
    app.UseTerraBasePressure();
    app.UseTerraBaseBadRequests();
    app.UseTerraBaseLegacyRoutes();
    app.UseRouting();
    app.UseTerraBaseListeners();
    app.UseTerraBaseAntiEntropyResponses();
    app.UseTerraBaseQueryShapeAdmission();
    app.UseAuthentication();
    app.UseTerraBaseProjectCheck();
    app.UseAuthorization();
    app.UseTerraBaseQos();
    app.UseTerraBaseCatalogForwarding();
    app.UseTerraBaseProjectCatalog();
    app.UseTerraBaseDatabasePlacement();
    app.MapTerraScaleEndpoints();
    app.MapTerraBaseApiReference();
    app.MapTerraBaseMetrics();
    app.MapTerraBaseReplication();
    app.MapTerraBaseUsage();
    app.MapTerraBaseQueryShapes();
    await ApplicationRunCompletion.RunAsync(app);
    return 0;
}
catch (Exception error)
{
    runCompletion.Completion.TrySetException(error);
    // A failed Build has no host/owner to await this signal; the entry point still rethrows the original error.
    _ = runCompletion.Completion.Task.Exception;
    throw;
}
finally
{
    runCompletion.Completion.TrySetResult();
}

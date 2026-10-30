using System.Net;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.ApiReference;
using TerraBase.Features.Backup;
using TerraBase.Features.Documents;
using TerraBase.Features.Health;
using TerraBase.Features.Import;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Platform;
using TerraBase.Features.Pressure;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Rpc;
using TerraScale.TerraBase.Rpc;
using TerraBase.Features.Catalog;
using TerraBase.Features.Sql;
using TerraBase.Features.Stats;
using TerraBase.Features.UserAuth;
using TerraBase.Features.Functions;
using TerraBase.Hosting;
using TerraScale.Endpoints;
if (args is ["--storage-format"])
{
    Console.WriteLine(StorageFormat.Id);
    return 0;
}
var environmentName = TerraBaseOptions.EnvironmentName(args);
TerraBaseOptions options;
InternalCredential internalCredential;
IPAddress internalAddress;
try
{
    TerraBaseOptions.ReadSecretFiles();
    options = TerraBaseOptions.FromProcess(args);
    internalCredential = InternalCredential.FromProcess(options);
    internalAddress = Listeners.InternalAddress(options);
}
catch (TerraBaseConfigurationException error)
{
    await Console.Error.WriteLineAsync($"terrabase: {error.Message}");
    return 2;
}
if (options.Backup is null && environmentName != Environments.Development)
{
    await Console.Error.WriteLineAsync($"terrabase: backup requires {string.Join(", ", BackupSettings.TigrisVariables.Select(variable => variable.Variable))} (attach the Tigris bucket) and TERRABASE_MASTER_KEY outside Development");
    return 2;
}

if (options.ShapeStorage is null && environmentName != Environments.Development)
{
    await Console.Error.WriteLineAsync("terrabase: TERRABASE_SHAPES_ACCESS_KEY_ID, TERRABASE_SHAPES_SECRET_ACCESS_KEY and TERRABASE_SHAPES_CDN_API_TOKEN are required outside Development");
    return 2;
}
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = environmentName });
builder.Configuration.Sources.Clear();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddTerraBaseListeners(options, internalAddress, environmentName));
builder.Logging.AddTerraBaseLogging();

var runCompletion = new ApplicationRunCompletion();
builder.Services.AddSingleton(runCompletion);
builder.Services.AddSingleton<Shutdown>();
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = Shutdown.HostedBudget);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(internalCredential);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpClientDefaults(http => http
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }));
builder.Services.ConfigureHttpJsonOptions(json =>
{
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
builder.Services.AddTerraBaseClientCors();
builder.Services.AddTerraBaseDocuments();
TerraBase.Features.FileStorage.FileStorageExtensions.AddTerraBaseFileStorage(builder.Services);
builder.Services.AddTerraBaseFunctions();
builder.Services.AddTerraBaseSql();
builder.Services.AddTerraBaseUserAuth();
builder.Services.AddTerraBaseManagement();
builder.Services.AddTerraBaseRegions();
builder.Services.AddTerraBasePlatform();
builder.Services.AddTerraBasePlatformExplore();
builder.Services.AddSingleton<IShipperLeases, LocalShipperLeases>();
builder.Services.AddTerraBaseBackup(options);
builder.Services.AddTerraBaseHealth();
builder.Services.AddTerraBasePressure();
builder.Services.AddTerraBaseStats();
builder.Services.AddTerraBaseImport();
TerraBase.Features.Shapes.ShapesExtensions.AddTerraBaseShapes(builder.Services);
TerraBase.Features.Shapes.ShapeResumeExtensions.AddTerraBaseShapeResume(builder.Services);
builder.Services.AddTerraBaseCatalog();
builder.Services.AddTerraBaseRpc();

try
{
    await using var app = builder.Build();
    try
    {
        _ = app.Services.GetRequiredService<IStorageEngine>();
    }
    catch (TerraBaseConfigurationException error)
    {
        await Console.Error.WriteLineAsync($"terrabase: {error.Message}");
        return 2;
    }

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    app.UseTerraBaseRestPipeline();
    app.UseTerraScaleRequestIdentity(() => TerraBase.Primitives.EntityIds.New().ToString(),
        app.Services.GetRequiredService<InternalCredential>().CorrelationKey);
    app.UseTerraBasePrivateResponses();
    app.UseTerraBasePressure();
    app.UseTerraBaseUsage();
    app.UseTerraBaseBadRequests();
    app.UseTerraBaseLegacyRoutes();
    app.UseRouting();
    app.UseTerraBaseClientCors();
    TerraBase.Features.Shapes.ShapesExtensions.UseTerraBaseShapeNoStore(app);
    app.UseTerraBaseListeners();
    app.Use((context, next) =>
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested
            && !ReferenceEquals(context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>(), ListenerMetadata.Probe))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }

        return next(context);
    });
    app.UseTerraBaseQosMemory();
    app.UseTerraBaseCatalogGate();
    app.UseAuthentication();
    app.UseTerraBaseProjectCheck();
    app.UseAuthorization();
    app.UseTerraBaseQos();
    app.UseTerraBaseProjectCatalog();
    app.UseTerraBaseDatabasePlacement();
    app.UseTerraBaseManagementRouteValidation();
    app.MapTerraScaleEndpoints();
    app.MapTerraScaleOpenApi().WithMetadata(new ListenerMetadata(TerraBaseListener.Client));
    app.MapTerraBaseMetrics();
    app.MapTerraBaseRestRpc();
    await ApplicationRunCompletion.RunAsync(app);
    return 0;
}
catch (Exception error)
{
    runCompletion.Completion.TrySetException(error);
    _ = runCompletion.Completion.Task.Exception;
    throw;
}
finally
{
    runCompletion.Completion.TrySetResult();
}

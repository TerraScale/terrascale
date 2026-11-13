using System.Diagnostics.Metrics;
using System.Net;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.ApiReference;
using TerraBase.Features.Backup;
using TerraBase.Features.Health;
using TerraBase.Features.Clock;
using TerraBase.Features.HomeApply;
using TerraBase.Features.HomePush;
using TerraBase.Features.LocalAck;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Platform;
using TerraBase.Features.Postgres;
using TerraBase.Features.Pressure;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Features.Rpc;
using TerraScale.TerraBase.Rpc;
using TerraBase.Features.Catalog;
using TerraBase.Features.Shapes;
using TerraBase.Features.Sql;
using TerraBase.Features.Stats;
using TerraBase.Features.UserAuth;
using TerraBase.Features.FileStorage;
using TerraBase.Features.Functions;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;
if (args is ["--storage-format"])
{
    Console.WriteLine(StorageFormat.Id);
    return 0;
}
var environmentName = TerraBaseOptions.EnvironmentName(args);
TerraBaseOptions options;
InternalCredential internalCredential;
CallerRegistry callers;
IPAddress internalAddress;
try
{
    callers = CallerRegistry.FromProcess();
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
if (options.ShapeStorage is null && (environmentName != Environments.Development || options.Backup is not null))
{
    await Console.Error.WriteLineAsync("terrabase: TERRABASE_SHAPES_ACCESS_KEY_ID, TERRABASE_SHAPES_SECRET_ACCESS_KEY and TERRABASE_SHAPES_CDN_API_TOKEN are required outside Development and with backup, whose data keys live in shape storage");
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
builder.Services.AddSingleton(callers);
builder.Services.AddSingleton<CallerTokens>();
builder.Services.AddSingleton<CallerAdmission>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NodeHlcClock>();
builder.Services.AddSingleton<IHlcClock>(provider => provider.GetRequiredService<NodeHlcClock>());
builder.Services.AddSingleton<IHlcState>(provider => provider.GetRequiredService<NodeHlcClock>());
builder.Services.ConfigureHttpClientDefaults(http => http
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }));
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.MaxDepth = TerraBaseJsonContext.Default.Options.MaxDepth;
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, TerraBaseJson.Resolver);
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
builder.Services.AddTerraBaseClock();
builder.Services.AddTerraBaseMemory(options);
builder.Services.AddTerraBaseQos();
builder.Services.AddTerraBaseEngine(options);
builder.Services.AddTerraBaseApiKeys();
builder.Services.AddTerraBaseClientCors();
builder.Services.AddTerraBaseFileStorage();
builder.Services.AddTerraBaseFunctions();
builder.Services.AddTerraBaseSql();
builder.Services.AddTerraBaseUserAuth();
builder.Services.AddTerraBaseManagement();
builder.Services.AddTerraBaseRegions();
builder.Services.AddSingleton<PostgresConnectionHandler>();
builder.Services.AddSingleton<PostgresRelay>();
builder.Services.AddTerraBaseRegionReplicas();
builder.Services.AddTerraBaseHomeApply();
builder.Services.AddTerraBaseLocalAck();
builder.Services.AddTerraBaseHomePush();
builder.Services.AddTerraBasePlatform();
builder.Services.AddTerraBasePlatformExplore();
builder.Services.AddSingleton<IShipperLeases, LocalShipperLeases>();
builder.Services.AddTerraBaseBackup(options);
builder.Services.AddTerraBaseHealth();
builder.Services.AddTerraBasePressure();
builder.Services.AddTerraBaseStats();
builder.Services.AddTerraBaseShapes();
builder.Services.AddTerraBaseCatalog();
builder.Services.AddTerraBaseRpc();

try
{
    await using var app = builder.Build();
    callers.Observe(app.Services.GetRequiredService<IMeterFactory>(), app.Services.GetRequiredService<TimeProvider>());
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
    SqlRequestTiming.UseSqlRequestTiming(app);
    app.UseTerraBaseRestPipeline();
    app.UseTerraBaseForwardedRequests();
    app.UseTerraScaleRequestIdentity(() => EntityIds.New().ToString(),
        app.Services.GetRequiredService<InternalCredential>().CorrelationKey);
    app.UseTerraBasePrivateResponses();
    app.UseTerraBasePressure();
    app.UseTerraBaseUsage();
    app.UseTerraBaseBadRequests();
    app.UseRouting();
    app.UseWebSockets();
    app.UseTerraBaseClientCors();
    app.UseTerraBaseShapeNoStore();
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
    app.UseTerraBaseRegionalWriter();
    app.UseTerraBaseQosMemory();
    app.UseTerraBaseCatalogGate();
    SqlRequestTiming.Checkpoint(app, "admission");
    app.UseAuthentication();
    app.UseTerraBaseBearerChallenge();
    app.UseTerraBaseProjectCheck();
    app.UseAuthorization();
    SqlRequestTiming.Checkpoint(app, "auth");
    app.UseTerraBaseHomeForwarding();
    SqlRequestTiming.Checkpoint(app, "home_lookup");
    app.UseTerraBaseQos();
    SqlRequestTiming.Checkpoint(app, "qos");
    app.UseTerraBaseProjectCatalog();
    SqlRequestTiming.Checkpoint(app, "project");
    app.UseTerraBaseDatabasePlacement();
    SqlRequestTiming.Checkpoint(app, "placement");
    app.UseTerraBaseManagementRouteValidation();
    app.MapTerraScaleEndpoints();
    app.MapTerraScaleOpenApi().WithMetadata(new ListenerMetadata(TerraBaseListener.Client));
    app.MapTerraBaseMetrics();
    app.MapTerraBaseClock();
    app.MapTerraBaseKeyRevocation(options);
    app.MapTerraBaseRestRpc().WithMetadata(ListenerMetadata.Internal, new CallerTransport());
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

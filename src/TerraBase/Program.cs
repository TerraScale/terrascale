using System.Net;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.ApiReference;
using TerraBase.Features.Backup;
using TerraBase.Features.Documents;
using TerraBase.Features.Health;
using TerraBase.Features.Hrana;
using TerraBase.Features.Import;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Platform;
using TerraBase.Features.Pressure;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Replication;
using TerraBase.Features.Sql;
using TerraBase.Features.Stats;
using TerraBase.Features.UserAuth;
using TerraBase.Features.Functions;
using TerraBase.Features.Webhooks;
using TerraBase.Features.Wire;
using TerraBase.Hosting;
using TerraScale.Endpoints;

// The deploy gate reads the storage format a new image writes without starting a server.
if (args is ["--storage-format"])
{
    Console.WriteLine(StorageFormat.Id);
    return 0;
}

// Match the slim host's environment precedence; WebApplicationFactory supplies an argument override.
var environmentName = TerraBaseOptions.EnvironmentName(args);
TerraBaseOptions options;
PeerCredential peerCredential;
IPAddress internalAddress;
try
{
    TerraBaseOptions.ReadSecretFiles();
    options = TerraBaseOptions.FromProcess(args);
    peerCredential = PeerCredential.FromProcess(options);
    internalAddress = Listeners.InternalAddress(options);
}
catch (TerraBaseConfigurationException error)
{
    // Configuration messages name settings, never their values.
    await Console.Error.WriteLineAsync($"terrabase: {error.Message}");
    return 2;
}

// Zero config: outside Development a node never runs without continuous backup (docs/terrabase/backup.md).
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

// Settings come only from TerraBaseOptions. The slim builder still adds environment variables
// and appsettings files, and Kestrel would bind listeners from a "Kestrel" section in them
// (for example Kestrel__Endpoints__X__Url): drop every configuration source.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = environmentName });
builder.Configuration.Sources.Clear();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddTerraBaseListeners(options, internalAddress, environmentName));
// Per-request framework logs are metrics' job; keep the log stream for events. An unauthenticated request is answered 401
// on purpose, so its challenge is not an event either. Peer credential verification outcomes are counters,
// never per-request logs or secret values.
builder.Logging.AddTerraBaseLogging();

var runCompletion = new ApplicationRunCompletion();
builder.Services.AddSingleton(runCompletion);
builder.Services.AddSingleton<Shutdown>();
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = Shutdown.HostedBudget);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new BoundPeerAddress(internalAddress));
builder.Services.AddSingleton(peerCredential);
builder.Services.AddSingleton(TimeProvider.System);
// Outbound clients keep one handler for the host's lifetime and recycle its connections instead (DNS changes still
// apply). A rotated handler's expiry timer would keep a stopped host's whole service provider, metrics included,
// reachable for minutes after it stopped.
builder.Services.ConfigureHttpClientDefaults(http => http
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }));
builder.Services.ConfigureHttpJsonOptions(json =>
{
    // Stored documents can reach Turso's depth bound; response envelopes add a few levels.
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
builder.Services.AddTerraBaseWebhooks();
builder.Services.AddTerraBaseFunctions();
builder.Services.AddTerraBaseSql();
builder.Services.AddTerraBaseWire();
builder.Services.AddTerraBaseUserAuth();
builder.Services.AddTerraBaseManagement();
builder.Services.AddTerraBaseRegions();
builder.Services.AddTerraBasePlatform();
builder.Services.AddTerraBasePlatformExplore();
builder.Services.AddTerraBaseCluster(options);
builder.Services.AddTerraBaseBackup(options);
builder.Services.AddTerraBaseHealth();
builder.Services.AddTerraBasePressure();
builder.Services.AddTerraBaseStats();
builder.Services.AddTerraBaseImport();
TerraBase.Features.Shapes.ShapesExtensions.AddTerraBaseShapes(builder.Services);
builder.Services.AddTerraBaseHrana();
TerraBase.Features.Shapes.ShapeResumeExtensions.AddTerraBaseShapeResume(builder.Services);
builder.AddTerraBaseReplication();

try
{
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

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    app.UseTerraScaleRequestIdentity(() => TerraBase.Primitives.EntityIds.New().ToString(),
        app.Services.GetRequiredService<PeerCredential>().CorrelationKey);
    app.UseTerraBasePrivateResponses();
    // Error bodies first, so they also cover the listener check's 404.
    app.UseTerraBasePressure();
    app.UseTerraBaseUsage();
    app.UseTerraBaseBadRequests();
    app.UseTerraBaseLegacyRoutes();
    app.UseRouting();
    app.UseTerraBaseClientCors();
    TerraBase.Features.Shapes.ShapesExtensions.UseTerraBaseShapeNoStore(app);
    app.UseTerraBaseListeners();
    // Refuse new work on existing connections while preserving the probes' shutdown responses.
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
    app.Use(async (context, next) =>
    {
        if (!context.RequestServices.GetRequiredService<IPeerTopology>().IsCurrent
            && (Listeners.ArrivedOn(context) == TerraBaseListener.Client
                && !ReferenceEquals(context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>(), ListenerMetadata.Probe)
                || context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() is not null))
        {
            await BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "peer_topology_mismatch",
                "this machine's peer topology is not verified", op: null).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    });
    app.UseTerraBaseCatalogGate();
    app.UseAuthentication();
    app.UseTerraBaseProjectCheck();
    app.UseAuthorization();
    app.UseTerraBaseQos();
    app.UseTerraBaseProjectCatalog();
    app.UseTerraBaseDatabasePlacement();
    app.UseTerraBaseManagementRouteValidation();
    app.MapTerraScaleEndpoints();
    // OpenAPI, Scalar and the GET / redirect are client-listener only and need no credentials.
    app.MapTerraScaleOpenApi().WithMetadata(new ListenerMetadata(TerraBaseListener.Client));
    app.MapTerraBaseMetrics();
    app.MapTerraBaseReplication();
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

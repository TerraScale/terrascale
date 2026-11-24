using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Atlas;
using TerraBase.Features.Backup;
using TerraBase.Features.Callers;
using TerraBase.Features.Clock;
using TerraBase.Features.Health;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Platform;
using TerraBase.Features.Pressure;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Rows;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Postgres.Hosting;
using TerraScale.Contracts;
using TerraScale.Endpoints;
using TerraScale.Logging;
using TerraScale.QuicRpc;

if (args is ["--storage-format"])
{
    Console.WriteLine(StorageFormat.Id);
    return 0;
}

QuicPlatform.EnsureInitialized();

return await (Startup.Prepare(args) switch
{
    PreparedStartup prepared => ServeAsync(prepared),
    ConfigurationError error => FailAsync(error.Message),
});

static async Task<int> FailAsync(string message)
{
    await Console.Error.WriteLineAsync(string.Concat("terrabase: ", message));
    return 2;
}

static async Task<int> ServeAsync(PreparedStartup prepared)
{
    var options = prepared.Options;
    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = options.EnvironmentName });
    builder.Configuration.Sources.Clear();
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
        kestrel.AddTerraBaseListeners(options, Listeners.InternalAddress(prepared.Cluster, options));
    });
    builder.Logging.AddTerraScaleLogging("terrabase");

    builder.Services.Configure<HostOptions>(static host => host.ShutdownTimeout = TimeSpan.FromSeconds(25));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(prepared.Credential);
    builder.Services.AddSingleton(prepared.Callers);
    builder.Services.ConfigureHttpJsonOptions(json =>
    {
        json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        json.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonTypeInfoResolver.Combine(
            TerraScaleContractsJsonContext.Default, SqlJsonContext.Default, SqlSocketJsonContext.Default, ManagementJsonContext.Default, BackupJsonContext.Default,
            RulesJsonContext.Default, RowsJsonContext.Default, ApiKeysJsonContext.Default, HealthJsonContext.Default, UsageJsonContext.Default, LogsJsonContext.Default,
            RegionsJsonContext.Default, PressureJsonContext.Default, ClockJsonContext.Default, BackupDestinationJson.Default));
    });
    builder.Services.AddHttpClient(TokenVerifier.HttpClientName, static client => client.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddHttpClient(AtlasPublisher.HttpClientName, static client => client.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddHttpClient(CloseProjectEndpoint.HttpClientName, static client => client.Timeout = TimeSpan.FromSeconds(15));
    builder.Services.AddHttpClient(BackupDestinationProbe.ClientName, static client => client.Timeout = TimeSpan.FromSeconds(20))
        .ConfigurePrimaryHttpMessageHandler(BackupDestinationNetwork.Handler);
    builder.Services.AddSingleton<BackupDestinationProbe>();
    builder.Services.AddSingleton<BackupDestinationStore>();
    builder.Services.AddTerraScaleEndpoints(endpoints =>
    {
        endpoints.Title = "TerraBase API";
        endpoints.Version = "v2";
        endpoints.MapTerraBaseErrors();
    });
    builder.Services.AddTerraBaseTelemetry();
    builder.Services.AddTerraBaseClientCors();
    builder.Services.AddTerraBaseNode(prepared);
    builder.Services.AddTerraBaseFeatures();
    builder.Services.AddTerraBasePostgres(new PostgresServerOptions(Tls: null));

    var app = builder.Build();
    prepared.Callers.Observe(app.Services.GetRequiredService<IMeterFactory>(), app.Services.GetRequiredService<TimeProvider>());
    _ = app.Services.GetRequiredService<TenantSessions>();
    app.UseRouting();
    app.UseTerraBaseSocketInput();
    app.UseWebSockets();
    app.UseTerraBaseClientCors();
    app.UseTerraBaseListeners();
    app.UseTerraScaleRequestIdentity(() => Ulid.NewUlid().ToString(), prepared.Credential.CorrelationKey);
    app.UseTerraBaseCallers();
    app.UseTerraBasePressure();
    app.UseTerraBaseRequestLogs();
    app.UseTerraBaseTenantQos();
    app.MapTerraScaleEndpoints();
    app.MapTerraScaleOpenApi().WithMetadata(ListenerMetadata.Client);
    app.MapTerraBaseMetrics();
    var bootstrap = app.Services.GetRequiredService<SystemBootstrap>();
    var outcome = await ApplicationRunCompletion.RunAsync(app);
    if (bootstrap.Failure is { } failure)
    {
        return await FailAsync(failure.Message);
    }

    return outcome switch
    {
        ServicesStopped => 0,
        ServiceFaulted faulted => await FailAsync(string.Concat("background service ", faulted.Service, " faulted")),
    };
}

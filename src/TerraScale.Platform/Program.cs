using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenTelemetry.Metrics;
using TerraScale.Contracts;
using TerraScale.Logging;
using TerraScale.Platform;
using TerraScale.Platform.Features.FileStorage;
using TerraScale.Platform.Features.Functions;
using TerraScale.Platform.Features.Shapes;
using TerraScale.QuicRpc;

try
{
    var options = PlatformOptions.FromEnvironment(Environment.GetEnvironmentVariables());
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.Logging.ClearProviders().AddTerraScaleLogging("ts-platform");
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(options.Port, listen => listen.Protocols = HttpProtocols.Http1);
        kestrel.ListenAnyIP(options.MetricsPort, listen => listen.Protocols = HttpProtocols.Http1);
    });

    var services = builder.Services;
    services.AddSingleton(options);
    services.AddSingleton(TimeProvider.System);
    services.ConfigureHttpJsonOptions(json =>
    {
        json.SerializerOptions.TypeInfoResolverChain.Insert(0, PlatformJsonContext.Default);
        json.SerializerOptions.TypeInfoResolverChain.Insert(1, FunctionsJsonContext.Default);
        json.SerializerOptions.TypeInfoResolverChain.Insert(2, FileStorageJsonContext.Default);
        json.SerializerOptions.TypeInfoResolverChain.Insert(3, ShapeRequestsJsonContext.Default);
        json.SerializerOptions.TypeInfoResolverChain.Insert(4, TerraScaleContractsJsonContext.Default);
    });
    var (version, policy) = TerraBaseProtocol.For(options.TerraBaseUrl);
    services.AddHttpClient(TerraBaseClient.HttpClientName, client =>
    {
        client.BaseAddress = options.TerraBaseUrl;
        client.DefaultRequestVersion = version;
        client.DefaultVersionPolicy = policy;
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    if (options.Shapes is { } shapes)
    {
        services.AddHttpClient(ShapeCdn.HttpClientName, client =>
        {
            client.BaseAddress = shapes.CdnApi;
            client.Timeout = TimeSpan.FromSeconds(10);
        });
    }

    services
        .AddSingleton<PlatformReadiness>()
        .AddSingleton<PlatformMetrics>()
        .AddSingleton<QuicMeshMetrics>()
        .AddSingleton<TerraBaseQuic>()
        .AddSingleton<TerraBaseClient>()
        .AddSingleton<PlatformStore>()
        .AddSingleton<OidcTokens>()
        .AddSingleton<PlatformAccess>()
        .AddSingleton<FunctionSecretSeal>()
        .AddSingleton<FunctionService>()
        .AddSingleton<FunctionInvoker>()
        .AddSingleton<S3FileObjectStore>()
        .AddSingleton<FileStorageService>()
        .AddSingleton<ShapeCrypto>()
        .AddSingleton<ShapeObjectStore>()
        .AddSingleton<ShapeCdn>()
        .AddSingleton<ShapeNotifier>()
        .AddSingleton<ShapeRegistry>()
        .AddSingleton<ShapeSealer>()
        .AddSingleton<ShapeReader>()
        .AddHostedService<PlatformSchema>()
        .AddHostedService<FileSweeper>()
        .AddHostedService<FunctionScheduler>()
        .AddHostedService<ShapeMaterializerService>();
    services.AddOpenTelemetry().WithMetrics(metrics => metrics
        .AddMeter(PlatformMetrics.MeterName, QuicMeshMetrics.MeterName)
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());
    services.AddTerraScaleEndpoints(PlatformResponses.Configure);

    var app = builder.Build();
    app.UseMiddleware<CrashBarrier>();
    app.Use(static (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return next(context);
    });
    app.UseWebSockets();
    app.MapTerraScaleEndpoints();
    app.MapTerraScaleOpenApi();
    app.MapPrometheusScrapingEndpoint().RequireHost($"*:{options.MetricsPort}");
    await app.RunAsync();
    return 0;
}
catch (Exception error)
{
    await Console.Error.WriteLineAsync($"ts-platform: {error.Message}");
    return 1;
}

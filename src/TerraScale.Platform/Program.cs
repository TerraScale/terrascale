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
    QuicPlatform.EnsureInitialized();
    var options = PlatformOptions.FromEnvironment(Environment.GetEnvironmentVariables());
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.Logging.ClearProviders().AddTerraScaleLogging("ts-platform");
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Limits.MaxRequestBodySize = ClientAdmission.MaxRequestBodyBytes;
        kestrel.ListenAnyIP(options.Port, listen => listen.Protocols = HttpProtocols.Http1);
        kestrel.ListenAnyIP(options.MetricsPort, listen => listen.Protocols = HttpProtocols.Http1);
    });

    var services = builder.Services;
    services.Configure<HostOptions>(static host => host.ShutdownTimeout = TimeSpan.FromSeconds(25));
    services.AddSingleton(options);
    services.AddSingleton(TimeProvider.System);
    if (ConfigureQuic(services, options, TimeProvider.System) is { } invalid)
    {
        await Console.Error.WriteLineAsync($"ts-platform: {invalid.Message}");
        return 1;
    }

    services.ConfigureHttpJsonOptions(json =>
    {
        json.SerializerOptions.TypeInfoResolverChain.Insert(0, PlatformJsonContext.Default);
        json.SerializerOptions.TypeInfoResolverChain.Insert(1, FunctionsJsonContext.Default);
        json.SerializerOptions.TypeInfoResolverChain.Insert(2, FileStorageJsonContext.Default);
        json.SerializerOptions.TypeInfoResolverChain.Insert(3, ShapeRequestsJsonContext.Default);
        json.SerializerOptions.TypeInfoResolverChain.Insert(4, TerraScaleContractsJsonContext.Default);
    });
    var (version, policy) = TerraBaseProtocol.For(options.TerraBaseUrl);
    services.AddHttpClient(OidcTokens.HttpClientName, static client => client.Timeout = TimeSpan.FromSeconds(10));
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
        .AddSingleton<TerraBaseClient>()
        .AddSingleton<PlatformStore>()
        .AddSingleton<OidcTokens>()
        .AddSingleton<PlatformAccess>()
        .AddSingleton<FunctionSecretSeal>()
        .AddSingleton<FunctionService>()
        .AddSingleton<FunctionInvoker>()
        .AddSingleton<S3FileObjectStore>()
        .AddSingleton<FileStorageService>()
        .AddSingleton<ProjectFileErasure>()
        .AddSingleton<ShapeCrypto>()
        .AddSingleton<ShapeObjectStore>()
        .AddSingleton<ShapeCdn>()
        .AddSingleton<ShapeNotifier>()
        .AddSingleton<ShapeRegistry>()
        .AddSingleton<ShapeSealer>()
        .AddSingleton<ShapeReader>()
        .AddHostedService<PlatformSchema>()
        .AddHostedService<FileSweeper>()
        .AddHostedService<ProjectFileSweepWorker>()
        .AddHostedService<FunctionScheduler>()
        .AddHostedService<ShapeMaterializerService>()
        .AddHostedService(static provider => provider.GetRequiredService<ShapeCdn>());
    services.AddOpenTelemetry().WithMetrics(metrics => metrics
        .AddMeter(PlatformMetrics.MeterName, QuicMeshMetrics.MeterName)
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());
    services.AddCors(cors => cors.AddDefaultPolicy(policy => policy.AllowAnyOrigin()
        .WithMethods("GET", "HEAD", "POST", "PUT", "PATCH", "DELETE")
        .WithHeaders("Authorization", "Content-Type", "If-None-Match", "TB-Shape-Resume", "X-TerraScale-Region-Pin", "TerraScale-Consistency", "TerraScale-Bookmark")
        .WithExposedHeaders("ETag", "Location", "Retry-After", "TerraScale-Bookmark", "TerraScale-Consistency", "TB-Shape-Hints", "TB-Shape-Cursor", "TB-Shape-Handle",
            "TB-Shape-Next-Offset", "TB-Shape-Up-To-Date", "X-TerraBase-Region", "X-TerraBase-Machine", "X-Request-ID")));
    services.AddRateLimiter(ClientAdmission.Configure);
    services.AddTerraScaleEndpoints(PlatformResponses.Configure);

    var app = builder.Build();
    app.UseForwardedHeaders(ClientAdmission.Forwarding());
    app.UseTerraScaleRequestIdentity(() => Ulid.NewUlid().ToString(), options.PeerSecret);
    app.UseMiddleware<CrashBarrier>();
    app.UseCors();
    app.UseRateLimiter();
    app.Use(static (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return next(context);
    });
    app.UseMiddleware<RequestAdmission>();
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

static MeshPkiInvalid? ConfigureQuic(IServiceCollection services, PlatformOptions options, TimeProvider timeProvider)
{
    if (options.TerraBaseQuic is not { } endpoint)
    {
        services.AddSingleton(static _ => new TerraBaseQuic());
        return null;
    }

    MeshPkiOutcome outcome = endpoint.Pem is { } pem
        ? QuicMeshPki.LoadPem(pem.CertificatePath, pem.KeyPath, pem.AuthorityPath, options.Node, options.Region, timeProvider)
        : QuicMeshPki.DeriveNode(QuicMeshPki.DecodeSecret(options.PeerSecret), options.Node, options.Region, timeProvider);
    return outcome switch
    {
        QuicMeshCertificates certificates => Register(certificates),
        MeshPkiInvalid invalid => invalid,
    };

    MeshPkiInvalid? Register(QuicMeshCertificates certificates)
    {
        services.AddSingleton(provider => new TerraBaseQuic(
            endpoint,
            certificates,
            options,
            provider.GetRequiredService<QuicMeshMetrics>(),
            timeProvider,
            provider.GetRequiredService<ILogger<QuicRpcClient>>()));
        return null;
    }
}

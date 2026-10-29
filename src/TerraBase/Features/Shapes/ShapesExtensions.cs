using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;

namespace TerraBase.Features.Shapes;

/// <summary>Registers the Shapes slice.</summary>
public static class ShapesExtensions
{
    private const string Handle = "h1-f3171a2230109328";

    // getShape's responses: description, example body (null for none) and the TB-Shape-* headers each carries.
    private static readonly Dictionary<string, (string Description, string? Example, string[] Headers)> Responses = new(StringComparer.Ordinal)
    {
        ["200"] = ("A snapshot page, closed chunk or fresh body; the exact bytes the ETag hashes.", $$$"""
            {"shape_id":"a07ef7c2873b7b4c4fcbb88c9e682ca5","handle":"{{{Handle}}}","offset":"2","next_offset":"4","covered_offset":"4","values_at":"4","up_to_date":false,"cursor_resume":"opaque-cursor-from-server","changes":[{"offset":"4","ops":[{"op":"upsert","id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"opaque-version-from-server","document":{"title":"Updated"}},{"op":"delete","id":"01ARZ3NDEKTSV4RRFFQ69G5FB1"}]}]}
            """, ["ETag", "TB-Shape-Handle", "TB-Shape-Next-Offset", "TB-Shape-Up-To-Date", "TB-Shape-Cursor"]),
        ["304"] = ("If-None-Match matched; reuse the cached body.", null, ["ETag", "TB-Shape-Handle", "TB-Shape-Next-Offset", "TB-Shape-Up-To-Date", "TB-Shape-Cursor"]),
        ["307"] = ("offset=-1: Location names snap.<S>.0 of the newest published snapshot, or a resume's local commit boundary.", null, ["Location", "TB-Shape-Handle"]),
        ["400"] = ("Error codes: invalid_request, invalid_query.", Error("invalid_request", "resume cursor is malformed"), []),
        ["401"] = ("Error codes: invalid_key.", Error("invalid_key", "The request cannot be served."), []),
        ["403"] = ("Error codes: forbidden_scope, forbidden.", Error("forbidden_scope", "The request cannot be served."), []),
        ["404"] = ("Error codes: unknown_database, unknown_collection.", Error("unknown_collection", "the collection does not exist"), []),
        ["409"] = ("Error codes: must_refetch; the body and TB-Shape-Handle name the current handle. Restart at offset=-1.",
            $$$"""{"error":{"code":"must_refetch","message":"shape lineage changed","handle":"{{{Handle}}}"}}""", ["TB-Shape-Handle"]),
        ["414"] = ("Error codes: uri_too_long.", Error("uri_too_long", "the continuation URL would not fit the 8 KiB request line"), []),
        ["429"] = ("Error codes: shape_busy. Keep state and retry with backoff.", Error("shape_busy", "too many shape flights"), []),
        ["503"] = ("Error codes: unavailable, shape_too_large. Keep state and retry with backoff.", Error("unavailable", "shape storage changed or is busy; retry later"), []),
    };

    private static readonly Dictionary<string, string> Headers = new(StringComparer.Ordinal)
    {
        ["ETag"] = "\"<shape_id>:<handle>:<offset>:<next_offset>:<digest>\"",
        ["Location"] = "The continuation: where (canonical), then handle, then offset.",
        ["TB-Shape-Handle"] = "The current handle.",
        ["TB-Shape-Next-Offset"] = "next_offset of the body.",
        ["TB-Shape-Up-To-Date"] = "true or false.",
        ["TB-Shape-Cursor"] = "live=true only: the bucket to send as cursor on the next long-poll.",
    };

    /// <summary>
    /// Makes every answer on an <see cref="OriginNoStore"/> route <c>private, no-store</c>, including 401, 403, 429 and errors
    /// written by other middleware. Register it right after routing.
    /// </summary>
    public static WebApplication UseTerraBaseShapeNoStore(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use((context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<OriginNoStore>() is not null)
            {
                // OnStarting runs last, after error writers that set their own no-store, and survives Response.Clear.
                context.Response.OnStarting(static state =>
                {
                    ((HttpResponse)state).Headers.CacheControl = OriginNoStore.CacheControl;
                    return Task.CompletedTask;
                }, context.Response);
            }

            return next(context);
        });
        return app;
    }

    /// <summary>Registers the shape services behind <c>GET …/collections/{collection}/shape</c>, their JSON metadata and examples.</summary>
    public static IServiceCollection AddTerraBaseShapes(this IServiceCollection services)
    {
        services.AddHttpClient(ShapeObjectStore.HttpClientName);
        services.AddSingleton(provider => provider.GetRequiredService<TerraBaseOptions>().ShapeStorage!);
        services.AddHttpClient(ShapeCdnPurge.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        return services.ConfigureHttpJsonOptions(json =>
            {
                json.SerializerOptions.TypeInfoResolverChain.Insert(0, ShapeJsonContext.Default);
                json.SerializerOptions.TypeInfoResolverChain.Insert(0, ShapeKeyJsonContext.Default);
                json.SerializerOptions.TypeInfoResolverChain.Insert(0, ShapeMirrorJsonContext.Default);
            })
            // Runs after the generic examples, which do not know the shape protocol's bodies, headers and codes.
            .AddOpenApi("v1", options => options.AddOperationTransformer((operation, _, _) =>
            {
                if (operation.OperationId == "getShape")
                {
                    Document(operation);
                }
                else if (operation.OperationId is "getShapeKey" or "getShapeManifest" && operation.Responses is { } responses)
                {
                    foreach (var response in responses.Values.OfType<OpenApiResponse>())
                    {
                        DocumentNoStore(response);
                    }
                }

                return Task.CompletedTask;
            }))
            .AddSingleton<ShapeRegistry>()
            .AddSingleton<IShapeFlightSealer>(provider => provider.GetRequiredService<ShapeRegistry>())
            .AddSingleton<ShapeLiveGate>()
            .AddSingleton<IShapeMetrics, ShapeMetrics>()
            .AddSingleton<IShapeSealer>(provider => new ShapeSealer(provider.GetRequiredService<IMemoryBudget>(),
                provider.GetRequiredService<ILogger<ShapeSealer>>(), provider.GetRequiredService<IShapeStorage>(), provider.GetRequiredService<IRemoteDurability>(),
                provider.GetRequiredService<IShapeMetrics>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ShapeMirror>().ChunkSealed))
            .AddSingleton<IShapeSnapshotSealer>(provider => new ShapeSnapshots(provider.GetRequiredService<IMemoryBudget>(),
                provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IShapeSnapshotTransactions>(),
                provider.GetRequiredService<IRemoteDurability>(), provider.GetRequiredService<IShapeMetrics>(),
                provider.GetRequiredService<ShapeMirror>().SnapshotSealed, provider.GetRequiredService<ILogger<ShapeSnapshots>>()))
            .AddSingleton<IShapeRetention>(provider => new ShapeRetention(provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IShapeStorage>(),
                provider.GetRequiredService<ShapeMirror>().FloorTrimmedAsync))
            .AddSingleton<IShapeFilterRegistry>(provider => new ShapeFilterRegistry(provider.GetRequiredService<IMemoryBudget>()))
            .AddSingleton<IShapeFlights<ShapeRead>>(provider => new ShapeFlights<ShapeRead>(provider.GetRequiredService<IStorageEngine>(),
                provider.GetRequiredService<IShapeFlightSealer>(), provider.GetRequiredService<IMemoryBudget>(),
                provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IHostApplicationLifetime>(), provider.GetRequiredService<IShapeMetrics>()))
            .AddSingleton<ShapeService>()
            .AddSingleton<IShapeLineages>(provider => provider.GetRequiredService<ShapeService>())
            .AddSingleton<IShapeSnapshots>(provider => provider.GetRequiredService<ShapeService>())
            .AddSingleton<IShapeChunks>(provider => provider.GetRequiredService<ShapeService>())
            .AddSingleton<IShapeLiveTail>(provider => provider.GetRequiredService<ShapeService>())
            .AddSingleton<IShapeResume>(provider => provider.GetRequiredService<ShapeService>())
            .AddSingleton(provider => provider.GetService<ShapeStorageSettings>() is { } settings
                ? new ShapeObjectStore(settings, provider.GetRequiredService<IHttpClientFactory>(), provider.GetRequiredService<TimeProvider>())
                : null!)
            .AddSingleton<IShapePurgeBudget>(provider => new ShapePurgeBudget(provider.GetRequiredService<TerraBaseOptions>().Nats,
                async cancellationToken => provider.GetService<IMemberDirectory>() is { } members
                    ? (await members.ListAsync(cancellationToken).ConfigureAwait(false)).Select(member => member.Region).Distinct(StringComparer.Ordinal).Count()
                    : 1,
                provider.GetRequiredService<TimeProvider>()))
            .AddSingleton<ShapeMirror>(provider => new ShapeMirror(provider.GetRequiredService<IStorageEngine>(),
                provider.GetRequiredService<IShapeStorage>(), provider.GetRequiredService<IShapeMetrics>(), provider.GetRequiredService<IMemoryBudget>(), provider.GetRequiredService<TimeProvider>(),
                () => provider.GetRequiredService<IShapeCrypto>(), provider.GetService<ShapeObjectStore>(), provider.GetRequiredService<IShapeCleanupStore>(),
                () => provider.GetRequiredService<IShapeCleanup>(), provider.GetRequiredService<ILogger<ShapeMirror>>()))
            .AddSingleton<ShapeCdnPurge>(provider => new ShapeCdnPurge(provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetService<ShapeStorageSettings>()?.CloudflareApiToken, provider.GetService<ShapeObjectStore>(),
                provider.GetRequiredService<IShapeCleanupStore>(), provider.GetRequiredService<IShapePurgeBudget>(), provider.GetRequiredService<IMemoryBudget>(),
                provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IMeterFactory>(), provider.GetRequiredService<ILogger<ShapeCdnPurge>>()))
            .AddSingleton<IShapeCleanup>(provider => provider.GetRequiredService<ShapeCdnPurge>())
            .AddHostedService(provider => provider.GetRequiredService<ShapeCdnPurge>())
            .AddHostedService(provider => new ShapeCdnAnalytics(provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetService<ShapeStorageSettings>()?.Cdn?.Host,
                provider.GetService<ShapeStorageSettings>()?.CloudflareApiToken, provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IMeterFactory>(), provider.GetRequiredService<ILogger<ShapeCdnAnalytics>>()))
            .AddSingleton<IShapeMirror>(provider => provider.GetRequiredService<ShapeMirror>())
            .AddSingleton<IShapeKeyRetention>(provider => provider.GetRequiredService<ShapeMirror>())
            .AddHostedService(provider => provider.GetRequiredService<ShapeMirror>())
            .AddSingleton(provider => ActivatorUtilities.CreateInstance<ShapeSweeper>(provider,
                (Func<IDatabaseStore, string, long, CancellationToken, ValueTask>)provider.GetRequiredService<ShapeMirror>().FloorTrimmedAsync))
            .AddHostedService(provider => provider.GetRequiredService<ShapeSweeper>())
            .AddSingleton<IShapeCrypto>(provider => new ShapeCrypto(provider.GetService<DataKeys>(),
                provider.GetRequiredService<IShapeKeyRetention>(), provider.GetRequiredService<IMemoryBudget>(),
                provider.GetRequiredService<TimeProvider>()));
    }

    private static void Document(OpenApiOperation operation)
    {
        if (operation.Responses is not { } responses)
        {
            return;
        }

        foreach (var (status, response) in responses)
        {
            if (response is not OpenApiResponse concrete || !Responses.TryGetValue(status, out var documented))
            {
                continue;
            }

            DocumentNoStore(concrete);
            concrete.Description = documented.Description;
            foreach (var media in concrete.Content?.Values.OfType<OpenApiMediaType>() ?? [])
            {
                media.Example = documented.Example is null ? null : JsonNode.Parse(documented.Example);
            }

            foreach (var name in documented.Headers)
            {
                concrete.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
                concrete.Headers[name] = new OpenApiHeader { Description = Headers[name], Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
            }
        }
    }

    private static void DocumentNoStore(OpenApiResponse response)
    {
        response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
        response.Headers["Cache-Control"] = new OpenApiHeader
        {
            Description = OriginNoStore.CacheControl,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        };
    }

    private static string Error(string code, string message) => $$$"""{"error":{"code":"{{{code}}}","message":"{{{message}}}"}}""";
}

/// <summary>
/// Endpoint metadata: origin answers hold tenant plaintext or depend on the caller's credential, so no cache may store them.
/// </summary>
internal sealed class OriginNoStore
{
    internal const string CacheControl = "private, no-store";

    internal static readonly OriginNoStore Instance = new();

    private OriginNoStore()
    {
    }
}

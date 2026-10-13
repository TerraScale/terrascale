using System.Text.Json.Nodes;
using Microsoft.OpenApi;

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
        ["307"] = ("offset=-1: Location names snap.<S>.0 of the newest published snapshot, or a resume's local commit boundary (no-store).", null, ["Location", "TB-Shape-Handle"]),
        ["400"] = ("Error codes: invalid_request, invalid_query.", Error("invalid_request", "resume cursor is malformed"), []),
        ["401"] = ("Error codes: invalid_key.", Error("invalid_key", "The request cannot be served."), []),
        ["403"] = ("Error codes: forbidden_scope.", Error("forbidden_scope", "The request cannot be served."), []),
        ["404"] = ("Error codes: unknown_database, unknown_collection.", Error("unknown_collection", "the collection does not exist"), []),
        ["409"] = ("Error codes: must_refetch; the body and TB-Shape-Handle name the current handle. Restart at offset=-1.",
            $$$"""{"error":{"code":"must_refetch","message":"shape lineage changed","handle":"{{{Handle}}}"}}""", ["TB-Shape-Handle"]),
        ["414"] = ("Error codes: uri_too_long.", Error("uri_too_long", "the continuation URL would not fit the 8 KiB request line"), []),
        ["429"] = ("Error codes: shape_busy. Keep state and retry with backoff.", Error("shape_busy", "too many shape flights"), []),
        ["503"] = ("Error codes: unavailable, shape_too_large. Keep state and retry with backoff.", Error("unavailable", "shape reads are not served yet"), []),
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

    /// <summary>Registers the shape services behind <c>GET …/collections/{collection}/shape</c>, their JSON metadata and examples.</summary>
    public static IServiceCollection AddTerraBaseShapes(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, ShapeJsonContext.Default))
            // Runs after the generic examples, which do not know the shape protocol's bodies, headers and codes.
            .AddOpenApi("v1", options => options.AddOperationTransformer((operation, _, _) =>
            {
                if (operation.OperationId == "getShape")
                {
                    Document(operation);
                }

                return Task.CompletedTask;
            }))
            .AddSingleton<UnavailableShapes>()
            .AddSingleton<IShapeLineages>(provider => provider.GetRequiredService<UnavailableShapes>())
            .AddSingleton<IShapeSnapshots>(provider => provider.GetRequiredService<UnavailableShapes>())
            .AddSingleton<IShapeChunks>(provider => provider.GetRequiredService<UnavailableShapes>())
            .AddSingleton<IShapeLiveTail>(provider => provider.GetRequiredService<UnavailableShapes>())
            .AddSingleton<IShapeResume>(provider => provider.GetRequiredService<UnavailableShapes>());

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

    private static string Error(string code, string message) => $$$"""{"error":{"code":"{{{code}}}","message":"{{{message}}}"}}""";
}

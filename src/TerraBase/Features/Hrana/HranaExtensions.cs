using System.Text.Json.Nodes;

namespace TerraBase.Features.Hrana;

/// <summary>Registers the Hrana-over-HTTP slice.</summary>
public static class HranaExtensions
{
    /// <summary>Registers the slice's JSON metadata and its API reference examples.</summary>
    public static IServiceCollection AddTerraBaseHrana(this IServiceCollection services) => services
        .ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, HranaJsonContext.Default))
        // Runs after the generic examples, which do not know the Hrana bodies.
        .AddOpenApi("v1", options => options.AddOperationTransformer((operation, _, _) =>
        {
            if (operation.OperationId != "libsqlPipeline")
            {
                return Task.CompletedTask;
            }

            Example(operation.RequestBody?.Content, """{"baton":null,"requests":[{"type":"execute","stmt":{"sql":"SELECT name FROM people WHERE id = ?","args":[{"type":"integer","value":"1"}]}},{"type":"close"}]}""");
            if (operation.Responses?["400"] is Microsoft.OpenApi.OpenApiResponse invalid)
            {
                invalid.Description = "Bad request: PROTOCOL_ERROR (malformed body) or STREAM_EXPIRED (unknown, closed or idle baton).";
                Example(invalid.Content, """{"error":"stream not found: the baton is unknown or expired; open a new stream","message":"stream not found: the baton is unknown or expired; open a new stream","code":"STREAM_EXPIRED"}""");
            }

            Example(operation.Responses?["200"].Content, """{"baton":null,"base_url":null,"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[{"name":"name","decltype":"TEXT"}],"rows":[[{"type":"text","value":"Ada"}]],"affected_row_count":0,"last_insert_rowid":null}}},{"type":"ok","response":{"type":"close"}}]}""");
            return Task.CompletedTask;
        }));

    private static void Example(IDictionary<string, Microsoft.OpenApi.IOpenApiMediaType>? content, string json)
    {
        foreach (var media in content?.Values.OfType<Microsoft.OpenApi.OpenApiMediaType>() ?? [])
        {
            media.Example = JsonNode.Parse(json);
        }
    }
}

using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace TerraScale.Dashboard.Platform.Http;

public static class CursorContract
{
    public static RouteHandlerBuilder WithCursorContract(this RouteHandlerBuilder endpoint) => endpoint
        .AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "limit", In = ParameterLocation.Query, Description = "Rows per page, 1–100; default 100. Invalid values return 422.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Default = JsonValue.Create(100) }
            });
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "after", In = ParameterLocation.Query, Description = "Opaque nextCursor from this list with the same scope and filters. Omit for the first page.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
            return Task.CompletedTask;
        });
}

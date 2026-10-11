using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TerraBase.Hosting;

/// <summary>
/// Documents a request body in OpenAPI only. Unlike <c>Accepts</c>, it adds no routing constraint:
/// routing would otherwise answer a foreign media type with its own 415 endpoint, which carries no
/// listener metadata and so becomes a 404 on the client listener. Endpoints check media types themselves.
/// </summary>
/// <param name="Type">Body type.</param>
/// <param name="ContentType">Media type.</param>
public sealed record RequestBodyMetadata(Type Type, string ContentType);

/// <summary>Adds <see cref="RequestBodyMetadata"/> bodies to the OpenAPI document.</summary>
public static class RequestBodies
{
    /// <summary>Registers the operation transformer for the OpenAPI document <paramref name="documentName"/>.</summary>
    public static IServiceCollection AddTerraBaseRequestBodies(this IServiceCollection services, string documentName) =>
        services.Configure<OpenApiOptions>(documentName, AddTransformer);

    private static void AddTransformer(OpenApiOptions options) => options.AddOperationTransformer(DocumentAsync);

    private static async Task DocumentAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var bodies = context.Description.ActionDescriptor.EndpointMetadata.OfType<RequestBodyMetadata>().ToList();
        if (bodies.Count == 0)
        {
            return;
        }

        var content = new Dictionary<string, IOpenApiMediaType>();
        foreach (var body in bodies)
        {
            content[body.ContentType] = new OpenApiMediaType
            {
                Schema = await context.GetOrCreateSchemaAsync(body.Type, null, cancellationToken).ConfigureAwait(false),
            };
        }

        operation.RequestBody = new OpenApiRequestBody { Required = true, Content = content };
    }
}

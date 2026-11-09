using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TerraBase.Hosting;

public sealed record RequestBodyMetadata(Type Type, string ContentType);

public static class RequestBodies
{
    public static IServiceCollection AddTerraBaseRequestBodies(this IServiceCollection services, string name) =>
        services.Configure<OpenApiOptions>(name, AddTransformer);

    private static void AddTransformer(OpenApiOptions options) => options.AddOperationTransformer(DescribeAsync);

    private static async Task DescribeAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
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

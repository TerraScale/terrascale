using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

internal static class CollectionSchemaRoute
{
    public const string Route = "/v1/projects/{project}/databases/{database}/collections/{collection}/schema";

    public static async ValueTask<IResult> ExecuteAsync(
        HttpContext context, ITenantDirectory tenants, Func<TenantContext, string, ValueTask<IResult>> action, CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(context, tenants, cancellationToken).ConfigureAwait(false) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => (DocumentRoute.CheckCollectionName(context), DocumentRoute.CheckParameters(context)) switch
            {
                (Validation error, _) => ErrorResponse.From(error),
                (_, Validation error) => ErrorResponse.From(error),
                (string name, _) => await action(database, name).ConfigureAwait(false),
            },
        };

    public static IResult Answer(CollectionSchemaResult result) => result switch
    {
        CollectionSchemaBody body => TypedResults.Ok(body),
        NotFound error => ErrorResponse.From(error),
        Validation error => ErrorResponse.From(error),
        PayloadTooLarge error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };
}

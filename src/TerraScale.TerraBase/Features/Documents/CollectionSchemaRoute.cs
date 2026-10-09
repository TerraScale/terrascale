using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

internal static class CollectionSchemaRoute
{
    // New routes are unversioned by the owner's pre-release policy; see docs/collection-schemas.md.
    public const string Route = "/projects/{project}/databases/{database}/collections/{collection}/schema";

    public static async ValueTask<IResult> ExecuteAsync(
        HttpContext context, ITenantDirectory tenants, Func<TenantContext, string, ValueTask<IResult>> action, CancellationToken cancellationToken) =>
        await DocumentRoute.CheckTenantAsync(context, tenants, cancellationToken).ConfigureAwait(false) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
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

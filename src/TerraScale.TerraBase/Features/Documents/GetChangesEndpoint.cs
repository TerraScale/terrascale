using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Polls the ordered local change feed of a collection.</summary>
public sealed class GetChangesEndpoint(IChangeFeedService changes, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/projects/{project}/databases/{database}/collections/{collection}/changes");
        Name("getCollectionChanges");
        Summary("Polls committed collection changes in local order");
        Description("Requires a ts-auth project access token with documents:read. Query: limit (1 to 100, default 100), resume_token (opaque position from the previous page). Without a token, reads from the retained history floor. Inserts and updates are upserts. Pages stop at 100 events or 2 MiB of stored JSON and framing. Tokens are specific to this serving member and collection lineage. Stale tokens return 409 resume_expired; capacity returns 429 change_feed_busy. See docs/change-feeds.md.");
        Tags("Documents");
        Produces<ChangeFeedBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<ProjectAccess>() is null)
        {
            return ErrorResponse.From(new Unauthorized("a ts-auth project access token is required"));
        }

        return await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => (DocumentRoute.CheckCollectionName(HttpContext), DocumentRoute.CheckParameters(HttpContext, "limit", "resume_token")) switch
            {
                (Validation error, _) => ErrorResponse.From(error),
                (_, Validation error) => ErrorResponse.From(error),
                (string collection, IQueryCollection query) => DocumentRoute.CheckLimit(query, 100, 100) switch
                {
                    Validation error => ErrorResponse.From(error),
                    int limit => await changes.ReadAsync(database, collection, query["resume_token"].FirstOrDefault(), limit, cancellationToken),
                },
            },
        };
    }
}

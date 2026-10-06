using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Errors;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Indexes;

/// <summary><c>GET …/databases/{database}/indexes</c>: lists the indexes of a database.</summary>
public sealed class ListIndexesEndpoint(IManagementService management, IIndexService indexes) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Indexes);
        Name("listIndexes");
        Summary("Lists the indexes of a database");
        Description("Query: collection (optional) keeps the indexes of one collection. Items are in name order; state is always active." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<IndexListBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        string? collection = null;
        if (HttpContext.Request.Query.TryGetValue("collection", out var raw))
        {
            if (!DatabaseName.TryParse(raw, out var name))
            {
                return ErrorResponse.From(new Validation("collection must be a lowercase name of at most 63 characters"));
            }

            collection = name.Value;
        }

        return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
            ? TypedResults.Ok(await indexes.ListAsync(database, collection, cancellationToken))
            : ErrorResponse.From(new NotFound());
    }
}

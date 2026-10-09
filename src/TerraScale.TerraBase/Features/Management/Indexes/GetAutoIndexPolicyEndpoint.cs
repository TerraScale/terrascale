using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Indexes;

/// <summary>Reads the automatic index policy of a collection.</summary>
public sealed class GetAutoIndexPolicyEndpoint(IManagementService management, IIndexService indexes) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.AutoIndexPolicy);
        Name("getAutoIndexPolicy");
        Summary("Reads a collection's automatic index policy");
        Description("Enabled by default. An opt-out applies to automatic index creation and removal; manual index operations remain available. The collection need not exist. This policy does not start index maintenance." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Indexes");
        Produces<AutoIndexPolicy>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!DatabaseName.TryParse(ManagementRoute.Value(HttpContext, "collection"), out var collection))
        {
            return ErrorResponse.From(new Validation("collection must be a lowercase name of at most 63 characters"));
        }

        return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await indexes.GetAutoIndexPolicyAsync(ManagementRoute.UserId(HttpContext), database, collection.Value, cancellationToken) switch
            {
                AutoIndexPolicy policy => TypedResults.Ok(policy),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}

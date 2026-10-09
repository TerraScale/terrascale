using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Indexes;

/// <summary>Sets the automatic index policy of a collection.</summary>
public sealed class SetAutoIndexPolicyEndpoint(IManagementService management, IIndexService indexes) : Endpoint<AutoIndexPolicyRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ProjectApiRoute.AutoIndexPolicy);
        Name("setAutoIndexPolicy");
        Summary("Sets a collection's automatic index policy");
        Description($"enabled (required boolean): false opts out of automatic index creation and removal; true restores the default. At most {IndexService.MaxAutoIndexOptOuts} collections may opt out per database. Policies survive restarts and are removed with the database. Manual index operations remain available. The collection need not exist. This policy does not start index maintenance." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Indexes");
        Produces<AutoIndexPolicy>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(AutoIndexPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DatabaseName.TryParse(ManagementRoute.Value(HttpContext, "collection"), out var collection))
        {
            return ErrorResponse.From(new Validation("collection must be a lowercase name of at most 63 characters"));
        }

        if (request.Enabled is not { } enabled)
        {
            return ErrorResponse.From(new Validation("enabled must be a boolean"));
        }

        return await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await indexes.SetAutoIndexPolicyAsync(ManagementRoute.UserId(HttpContext), database, collection.Value, enabled, cancellationToken) switch
            {
                AutoIndexPolicy policy => TypedResults.Ok(policy),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}

using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>DELETE …/databases/{database}</c>: deletes a database and its data.</summary>
public sealed class DeleteManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ProjectApiRoute.Database);
        Metadata(AnchoredRevocation.Instance);
        Name("deleteDatabase");
        Summary("Deletes a database and its data");
        Description("Branches of the database are independent copies and are not deleted. A database is deleted in its home region (421 wrong_region elsewhere) once it has no replica regions (409 database_replicated): remove them with PATCH first." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware(), new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var placement = await DatabaseRegions.ElsewhereAsync(HttpContext, management, options.Region);
        if (placement is not Done)
        {
            return placement switch
            {
                Done => throw new System.Diagnostics.UnreachableException(),
                DatabaseRegions.Misdirected elsewhere => elsewhere,
                NotFound error => ManagementRoute.DatabaseNotFound(error),
                Unavailable error => ErrorResponse.From(error),
            };
        }

        return await management.DeleteDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}

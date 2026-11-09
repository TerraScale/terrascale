using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class DeleteManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(ProjectApiRoute.Database);
        Metadata(AnchoredRevocation.Instance);
        Name("deleteDatabase");
        Summary("Deletes a database and its data");
        Description("Branches of the database are independent copies and are not deleted. Delete in its home region (421 wrong_region elsewhere). Permanent purge authority removes every replica, including allRegions placement, without a preceding PATCH. Refused with 409 move_in_progress while the database's home move runs; retry after it settles. 204 records logical deletion and local cleanup, tells every replica region to forget its record and replica at once, and retries remote erasure durably." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware(), new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        DeleteAsync(HttpContext, management, options, cancellationToken);

    internal static async ValueTask<IResult> DeleteAsync(
        HttpContext context, IManagementService management, TerraBaseOptions options, CancellationToken cancellationToken)
    {
        var placement = await DatabaseRegions.ElsewhereAsync(context, management, options.Region);
        if (placement is not Done)
        {
            return placement switch
            {
                Done => throw new UnreachableException(),
                DatabaseRegions.Misdirected elsewhere => elsewhere,
                NotFound error => ManagementRoute.DatabaseNotFound(error),
                Unavailable error => ErrorResponse.From(error),
            };
        }

        return await management.DeleteDatabaseAsync(ManagementRoute.UserId(context), ManagementRoute.OrganizationId(context), ManagementRoute.Value(context, "project"), ManagementRoute.Value(context, "database"), cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}

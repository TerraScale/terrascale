using System.Diagnostics;
using NoContent = Microsoft.AspNetCore.Http.HttpResults.NoContent;
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
        Summary("Moves a database to the seven-day deleted state");
        Description("The database is unreachable and is listed under deleted-databases for at least seven days. Restore it there by immutable ID until permanent purge is authorized. Data, backups and keys are retained until then; storage remains counted against the organization quota until purge. Permanent cleanup begins after that deadline or an explicit DELETE of the deleted-database resource. A call outside placement returns 421 wrong_region; a move in progress or deletion outside the home region returns 409. Other regions drop their copies at deletion and are re-seeded after restore. Branches are independent. Platform databases are protected. " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), new ManagementUserRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware(), new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
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

        return await management.DeleteDatabaseAsync(ManagementRoute.UserId(context), ManagementRoute.OrganizationId(context), context.RouteValue("project"), context.RouteValue("database"), cancellationToken) switch
        {
            Done => Retained(context),
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
    private static NoContent Retained(HttpContext context)
    {
        context.Response.Headers.Append("TerraBase-Storage-Released", "at-purge");
        return TypedResults.NoContent();
    }
}

using TerraBase.Configuration;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>DELETE …/databases/{database}</c>: deletes a database and its data.</summary>
public sealed class DeleteManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ManagementRoute.Database);
        Name("deleteManagedDatabase");
        Summary("Deletes a database and its data");
        Description("Branches of the database are independent copies and are not deleted. A database is deleted in its home region (421 wrong_region elsewhere) once it has no replica regions (409 database_replicated): remove them with PATCH first." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new DatabaseRegions.PlacementAware(), new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DatabaseRegions.ElsewhereAsync(HttpContext, management, options.Region) ?? ManagementRoute.NoContent(await management.DeleteDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), cancellationToken));
}

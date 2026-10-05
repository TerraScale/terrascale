using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Backups;

/// <summary><c>GET …/databases/{database}/backups</c>: lists the backups of a database.</summary>
public sealed class ListBackupsEndpoint(IManagementService management, IManagedBackupService backups) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/backups");
        Name("listBackups");
        Summary("Lists the backups of a database");
        Description(ManagementRoute.PageDescription + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagementPage<DatabaseBackup>>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (after, limit) = ManagementRoute.Page(HttpContext);
        return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
            ? TypedResults.Ok(await backups.ListBackupsAsync(database.OrganizationId, database.DatabaseId!, after, limit, cancellationToken))
            : TypedResults.NotFound();
    }
}

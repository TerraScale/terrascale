using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

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
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
                ? TypedResults.Ok(await backups.ListBackupsAsync(database.OrganizationId, database.DatabaseId!, page.After, page.Limit, cancellationToken))
                : ErrorResponse.From(new NotFound()),
        };
}

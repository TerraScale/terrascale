using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>GET …/databases/{database}/backups</c>: lists the backups of a database.</summary>
internal sealed class ListBackupsEndpoint(IManagementService management, IManagedBackupService backups) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/backups");
        Name("listBackups");
        Summary("Lists the backups of a database");
        Description(ManagementRoute.PageDescription + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<ManagementPage<DatabaseBackup>>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
            {
                TenantContext database => await backups.ListBackupsAsync(database.OrganizationId, database.DatabaseId!, page.After, page.Limit, cancellationToken) switch
                {
                    ManagementPage<DatabaseBackup> backupPage => TypedResults.Ok(backupPage),
                    Unavailable error => ErrorResponse.From(error),
                },
                NotFound => ErrorResponse.From(new NotFound()),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}

using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>POST …/databases/{database}/backups</c>: backs a database up now.</summary>
public sealed class CreateBackupEndpoint(IManagementService management, IManagedBackupService backups) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Database + "/backups");
        Name("createBackup");
        Summary("Backs a database up now");
        Description("Takes a consistent snapshot of the database now; no request body. The backup is kept until the retention of the backup policy expires or the database is deleted." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<DatabaseBackup>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            TenantContext database => await backups.CreateBackupAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), database, cancellationToken) switch
            {
                DatabaseBackup backup => TypedResults.Created($"{ManagementRoute.DatabasePath(database)}/backups/{backup.Id}", backup),
                NotFound error => ErrorResponse.From(error),
            },
        };
}

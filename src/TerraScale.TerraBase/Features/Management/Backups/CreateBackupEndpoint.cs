using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Backups;

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
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var writable = await ManagementRoute.WritableDatabaseAsync(HttpContext, management, cancellationToken);
        if (writable.Value is not { } database)
        {
            return ManagementRoute.Error(writable.Error);
        }

        var organization = ManagementRoute.OrganizationId(HttpContext);
        var result = await backups.CreateBackupAsync(ManagementRoute.UserId(HttpContext), organization, database, cancellationToken);
        return result.Value is { } backup
            ? TypedResults.Created($"{ManagementRoute.DatabasePath(database)}/backups/{backup.Id}", backup)
            : ManagementRoute.Error(result.Error);
    }
}

using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Backups;

/// <summary><c>POST …/databases/{database}/backups/{backup}/restore</c>: restores a backup into a new database.</summary>
public sealed class RestoreBackupEndpoint(IManagementService management, IManagedBackupService backups) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Database + "/backups/{backup}/restore");
        Name("restoreBackup");
        Summary("Restores a backup into a new database");
        Description("Restore never overwrites: it creates a new database in the same project holding the backup's data, with branchedFrom set to the backed-up database. name follows the database name rules; without it the first free of <source>-restore, <source>-restore-2, … is used." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagedDatabase>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name is null ? null : ManagementRoute.DatabaseName(request);
        var writable = await ManagementRoute.WritableDatabaseAsync(HttpContext, management, cancellationToken);
        if (writable.Value is not { } database)
        {
            return ManagementRoute.Error(writable.Error);
        }

        var organization = ManagementRoute.OrganizationId(HttpContext);
        var result = await backups.RestoreAsync(ManagementRoute.UserId(HttpContext), organization, database, ManagementRoute.Value(HttpContext, "backup"), name, cancellationToken);
        return result.Value is { } value
            ? TypedResults.Created($"{ManagementRoute.Organizations}/{organization}/projects/{value.ProjectId}/databases/{value.Id}", value)
            : ManagementRoute.Error(result.Error);
    }
}

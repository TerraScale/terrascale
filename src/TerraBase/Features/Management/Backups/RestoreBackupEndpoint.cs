using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>POST …/databases/{database}/backups/{backup}/restore</c>: restores a backup into a new database.</summary>
internal sealed class RestoreBackupEndpoint(IManagementService management, IManagedBackupService backups) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/backups/{backup}/restore");
        Name("restoreBackup");
        Summary("Restores a backup into a new database");
        Description("Restore never overwrites: it creates a new database in the same project holding the backup's data, with branchedFrom set to the backed-up database and the same primary, replica regions and allRegions choice. A request sent elsewhere answers 421 with ts-replay naming the snapshot region. An unknown snapshot region answers 503 backup_region_unknown; a snapshot outside current placement answers 409 backup_region_not_placed. name follows the database name rules; without it the first free of <source>-restore, <source>-restore-2, … is used. Platform organization sources return 409 recovery_erasure_fenced; use the separately reviewed operator recovery procedure for platform recovery." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<ManagedDatabase>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? name = null;
        if (request.Name is not null)
        {
            switch (ManagementRoute.CheckDatabaseName(request))
            {
                case Validation error:
                    return ErrorResponse.From(error);
                case string valid:
                    name = valid;
                    break;
            }
        }

        var organization = ManagementRoute.OrganizationId(HttpContext);
        return await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await backups.RestoreAsync(
                ManagementRoute.UserId(HttpContext), organization, database, ManagementRoute.Value(HttpContext, "backup"), name, cancellationToken) switch
            {
                ManagedDatabase restored => TypedResults.Created(ManagementRoute.CreatedDatabasePath(HttpContext, restored), restored),
                BackupElsewhere elsewhere => DatabaseRegions.RecoveryRegion(elsewhere.Region, elsewhere.Database),
                NotFound error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
                QuotaExceeded error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}

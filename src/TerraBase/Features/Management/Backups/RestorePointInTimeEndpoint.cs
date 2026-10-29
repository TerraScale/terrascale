using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>POST …/databases/{database}/point-in-time/restore</c>: restores a database as of a time into a new database.</summary>
internal sealed class RestorePointInTimeEndpoint(IManagementService management, IManagedBackupService backups)
    : Endpoint<PointInTimeRestoreRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/point-in-time/restore");
        Name("restorePointInTime");
        Summary("Restores a database as of a time into a new database");
        Description("Restores, into a new database in the same project with branchedFrom set to the restored database, every transaction committed before pointInTime (to the millisecond) and none whose commit was recorded at or after it, whole transactions only; the database itself is unchanged. The copy preserves its primary, replica regions and allRegions choice; requests execute in the primary, with 421 routing from other regions. destination selects platform (default) or customer. 400 only when pointInTime is after now or before the plan's retention window; the error names the earliest and latest available restore timestamps; a time between them outside the restore window (GET …/point-in-time: before its oldest stored snapshot, or after the newest backed-up data) is 409 backup_history_incomplete, retryable once the backup holds it. The backup must prove the history complete up to pointInTime: contiguous segments from the snapshot, then the snapshot taken in pointInTime's millisecond, a read mark at or after it, a later transaction committed at or after it, or the generation's next snapshot at exactly the replayed log; otherwise 409 backup_history_incomplete (a missing segment, an unshipped or missing final segment, a generation that ended, a quiet period not yet closed). Transactions record their commit time in whole seconds: 409 restore_point_unproven when one read after pointInTime committed within its second (the message names that second's start, the nearest earlier time it can place), has no recorded time, or the times go back. 409 backup_unreadable when an object it needs fails authentication (corruption, a data key the master key did not wrap) or is truncated, or the replayed file fails its integrity check. Whole seconds with a later transaction, snapshot times and the window's latest restore exactly. Restores consume the same organization database allowance as create: 409 database_limit when at capacity. Nothing is created on 409. name follows the database name rules; without it the first free of <source>-restore, <source>-restore-2, … is used. Platform organization sources return 409 recovery_erasure_fenced; use the separately reviewed operator recovery procedure for platform recovery." + " " + ProjectApiRoute.IdentityDescription);
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
    public override async ValueTask<IResult> HandleAsync(PointInTimeRestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Access first: another tenant's database answers 404 whatever the body holds.
        return await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await RestoreAsync(database, request, cancellationToken),
        };
    }

    private async ValueTask<IResult> RestoreAsync(TenantContext database, PointInTimeRestoreRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await RestoreCoreAsync(database, request, cancellationToken);
        }
        catch (Exception error) when (BackupDestinationRoute.IsUnavailable(error, cancellationToken))
        {
            return ErrorResponse.From(BackupDestinationRoute.Unavailable());
        }
    }

    private async ValueTask<IResult> RestoreCoreAsync(TenantContext database, PointInTimeRestoreRequest request, CancellationToken cancellationToken)
    {
        string destination = BackupDestinationNames.Platform;
        switch (BackupDestinationRoute.Source(request.Destination))
        {
            case Validation selectionError:
                return ErrorResponse.From(selectionError);
            case string normalized:
                destination = normalized;
                break;
        }

        if (request.PointInTime is not { } at)
        {
            return ErrorResponse.From(new Validation("pointInTime is required"));
        }

        string? name = null;
        if (request.Name is not null)
        {
            switch (ManagementRoute.CheckDatabaseName(new NameRequest(request.Name)))
            {
                case Validation error:
                    return ErrorResponse.From(error);
                case string valid:
                    name = valid;
                    break;
            }
        }

        var organization = ManagementRoute.OrganizationId(HttpContext);
        return await backups.RestorePointInTimeAsync(ManagementRoute.UserId(HttpContext), organization, database, at, name, cancellationToken, destination) switch
        {
            BackupElsewhere elsewhere => DatabaseRegions.RecoveryRegion(elsewhere.Region, elsewhere.Database),
            ManagedDatabase restored => TypedResults.Created(ManagementRoute.CreatedDatabasePath(HttpContext, restored), restored),
            NotFound error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            QuotaExceeded error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}

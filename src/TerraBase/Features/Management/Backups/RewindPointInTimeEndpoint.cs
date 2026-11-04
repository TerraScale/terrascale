using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

internal sealed class RewindPointInTimeEndpoint(IManagementService management, IManagedBackupService backups)
    : Endpoint<PointInTimeRewindRequest, IResult>
{
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/point-in-time/rewind");
        Name("rewindPointInTime");
        Summary("Rewinds a database in place to a time");
        Description("Replaces every table, index, view and trigger of the database with its state as of pointInTime, in one transaction, keeping the database's id, name, credentials and allowance; changes committed after pointInTime are discarded. The time follows the same rules as point-in-time/restore: 400 outside the plan's window, 409 backup_history_incomplete, restore_point_unproven or backup_unreadable when the backup cannot prove it; nothing changes on an error. The rewind is an ordinary write: it is backed up, so a later rewind can undo it. destination selects platform (default) or customer. Platform organization sources return 409 recovery_erasure_fenced." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<ManagedDatabase>(StatusCodes.Status200OK);
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

    public override async ValueTask<IResult> HandleAsync(PointInTimeRewindRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await RewindAsync(database, request, cancellationToken),
        };
    }

    private async ValueTask<IResult> RewindAsync(TenantContext database, PointInTimeRewindRequest request, CancellationToken cancellationToken)
    {
        var destination = BackupDestinationNames.Platform;
        switch (BackupDestinationRoute.Source(request.Destination))
        {
            case Validation error:
                return ErrorResponse.From(error);
            case string valid:
                destination = valid;
                break;
        }

        if (request.PointInTime is not { } at)
        {
            return ErrorResponse.From(new Validation("pointInTime is required"));
        }

        try
        {
            return await backups.RewindPointInTimeAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), database, at, destination, cancellationToken) switch
            {
                BackupElsewhere elsewhere => DatabaseRegions.RecoveryRegion(elsewhere.Region, elsewhere.Database),
                ManagedDatabase rewound => TypedResults.Ok(rewound),
                NotFound error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
                QuotaExceeded error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            };
        }
        catch (Exception error) when (BackupDestinationRoute.IsUnavailable(error, cancellationToken))
        {
            return ErrorResponse.From(BackupDestinationRoute.Unavailable());
        }
    }
}

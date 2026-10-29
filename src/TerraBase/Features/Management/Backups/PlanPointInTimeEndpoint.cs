using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>The body of <c>POST …/point-in-time/plan</c>.</summary>
/// <param name="Destination">The backup bucket: platform (default) or customer.</param>
/// <param name="PointInTime">The required restore time, between the window's earliest and latest times, inclusive.</param>
public sealed record PointInTimePlanRequest(DateTimeOffset? PointInTime, string? Destination = null);

/// <summary><c>POST …/databases/{database}/point-in-time/plan</c>: previews continuous backup metadata without restoring.</summary>
internal sealed class PlanPointInTimeEndpoint(IManagementService management, IPointInTimeRestore restore) : Endpoint<PointInTimePlanRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/point-in-time/plan");
        Name("planPointInTime");
        Summary("Previews a point-in-time restore without changing the database");
        Description("destination selects platform (default) or customer. Read-only advisory metadata: the restore window, requested time, selected snapshot time and applicable segment count/latest time. pointInTime is required; as for restore, 400 only when it is after now or before the plan's retention window, and 409 backup_history_incomplete for any other time outside the restore window; 404 when no stored data remains within the plan's retention window. window.latest is the newest time the backup proves, as GET …/point-in-time reports it; read from the bucket, it downloads at most three of the newest segments for their read marks, and nothing else is downloaded. From object names alone it checks the history a restore needs: the first segment starts at the snapshot, segments follow in order, and the history can reach pointInTime (a later segment or snapshot of the generation follows, or the last segment was read at that millisecond); otherwise 409 backup_history_incomplete, as restore answers. historyCheck is metadata: segment lengths, read marks and transaction times are checked only by an actual restore, which can still answer 409 backup_history_incomplete or restore_point_unproven. Scans at most 10000 objects within 10 seconds; exceeding either bound returns 503." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<PointInTimePlan>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(PointInTimePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var found = await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (found is not TenantContext database)
        {
            return found switch
            {
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                TenantContext => throw new UnreachableException(),
            };
        }

        if (request.PointInTime is not { } at)
        {
            return ErrorResponse.From(new Validation("pointInTime is required"));
        }

        string destination = BackupDestinationNames.Platform;
        switch (BackupDestinationRoute.Source(request.Destination))
        {
            case Validation error:
                return ErrorResponse.From(error);
            case string source:
                destination = source;
                break;
        }

        return await restore.PlanAsync(database, at, cancellationToken, destination) switch
        {
            PointInTimePlan plan => TypedResults.Ok(plan),
            NotFound error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}

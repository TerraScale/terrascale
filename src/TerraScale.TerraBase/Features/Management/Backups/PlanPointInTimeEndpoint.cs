using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>The body of <c>POST …/point-in-time/plan</c>.</summary>
/// <param name="PointInTime">The required restore time, between the window's earliest time and now.</param>
public sealed record PointInTimePlanRequest(DateTimeOffset? PointInTime);

/// <summary><c>POST …/databases/{database}/point-in-time/plan</c>: previews continuous backup metadata without restoring.</summary>
public sealed class PlanPointInTimeEndpoint(IManagementService management, IPointInTimeRestore restore) : Endpoint<PointInTimePlanRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/point-in-time/plan");
        Name("planPointInTime");
        Summary("Previews a point-in-time restore without changing the database");
        Description("Read-only advisory metadata: the restore window, requested time, selected snapshot time and applicable segment count/latest time. pointInTime is required between the window's earliest time and now; after the window's latest time the latest data applies. No backup objects are downloaded or decrypted. Integrity and segment contiguity are checked during actual restore. Scans at most 10000 objects within 10 seconds; exceeding either bound returns 503." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<PointInTimePlan>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
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
        if (await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is not { } database)
        {
            return ErrorResponse.From(new NotFound());
        }

        if (request.PointInTime is not { } at)
        {
            return ErrorResponse.From(new Validation("pointInTime is required"));
        }

        return await restore.PlanAsync(database, at, cancellationToken) switch
        {
            PointInTimePlan plan => TypedResults.Ok(plan),
            NotFound error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}

using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary><c>GET …/databases/{database}/usage</c>: reads the storage and operation usage of a database.</summary>
public sealed class GetDatabaseUsageEndpoint(IManagementService management, IUsageService usage, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    /// <summary>What the read and write counts guarantee, for every usage route.</summary>
    internal const string CountsDescription =
        "Each request the engine answers is one read, however many documents it returns; each committed put, patch or delete is one write. "
        + "period sums the series; reads and writes are totals since each database was created. "
        + "Reads and writes are best-effort dashboard usage, not billing data: they are saved every 10 seconds and at a clean shutdown "
        + "(a catalog follower sends its counts to its primary as often), a failed save is retried, and a crash loses the operations "
        + "counted since the last successful save. " + UsageWindow.Description;

    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/usage");
        Name("getDatabaseUsage");
        Summary("Reads the usage of a database");
        Description("Bytes on disk, JSON bytes of the live documents (logicalBytes), collections, live documents, and document reads (gets, list, query and shape pages, SQL runs) and writes (committed puts, patches and deletes, each operation of a transaction), with one point per hour or day of the period. " + CountsDescription + " " + ProjectApiRoute.IdentityDescription);
        Tags("Usage");
        Produces<DatabaseUsage>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        UsageWindow.Parse(HttpContext.Request.Query, time.GetUtcNow()) switch
        {
            Validation error => ErrorResponse.From(error),
            UsageWindow window => await ReadAsync(window, cancellationToken),
        };

    private async ValueTask<IResult> ReadAsync(UsageWindow window, CancellationToken cancellationToken)
    {
        try
        {
            return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
                && await usage.GetDatabaseUsageAsync(database, window, cancellationToken) is { } result
                ? TypedResults.Ok(result)
                : TypedResults.NotFound();
        }
        catch (QueryCapacityException)
        {
            return HttpContext.Features.Get<LegacyManagementRoute>() is not null
                ? TypedResults.BadRequest("query too expensive")
                : ErrorResponse.From(new Validation("query too expensive", "invalid_query"));
        }
    }
}

using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.HomeApply;

internal sealed class ApplyEndpoint(IHomeApply apply, ITenantDirectory tenants, IReplicaCatalog catalog, IMemoryBudget memory, TerraBaseOptions options)
    : EndpointWithoutRequest<IResult>
{
    internal const long MaxBodyBytes = 8 * 1024 * 1024;

    public override void Configure()
    {
        Post("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/apply");
        Name("applyRegionTransaction");
        Summary("Applies a transaction committed in another region at the database's home");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Applies the transaction all or nothing with column "
            + "last-writer-wins by HLC, or records it as reverted in terrascale.conflicts; either way 200 with its state. Sending it again returns the "
            + "recorded state. 404 when the database is not homed here or its placement does not name the region; 503 when the home is unavailable.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(new RequestBodyMetadata(typeof(RegionTransaction), "application/json"), ListenerMetadata.Internal);
        Produces<HomeApplyResult>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.ContentLength is not (> 0 and <= MaxBodyBytes))
        {
            return ErrorResponse.From(new Validation("the body is one region transaction of at most 8 MiB"));
        }

        var length = HttpContext.Request.ContentLength.Value;
        using var reservation = memory.TryReserve(checked(length * 4), tenant: null);
        if (reservation is null)
        {
            return ErrorResponse.From(new Unavailable("the node's memory budget is exhausted; retry"));
        }

        RegionTransaction? transaction;
        try
        {
            transaction = await HttpContext.Request.ReadFromJsonAsync(TerraBaseJsonContext.Default.RegionTransaction, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            transaction = null;
        }

        if (Invalid(transaction) is { } invalid)
        {
            return ErrorResponse.From(new Validation(invalid));
        }

        var found = await tenants.FindDatabaseAsync(Value("organization"), Value("project"), Value("database"), cancellationToken).ConfigureAwait(false);
        if (found is not TenantContext database)
        {
            return found is Unavailable unavailable ? ErrorResponse.From(unavailable) : ErrorResponse.From(new NotFound("the database does not exist"));
        }

        switch (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false))
        {
            case Unavailable error:
                return ErrorResponse.From(error);
            case TenantDatabaseRecord { Placement: var placement } when placement.Home == options.Region && (placement.AllRegions || placement.Replicas.Contains(transaction!.Region!)):
                break;
            default:
                return ErrorResponse.From(new NotFound("the database is not homed here with a replica in that region"));
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            using (QosContext.Enter(database, memory))
            {
                return await apply.ApplyAsync(database, transaction!, cancellationToken).ConfigureAwait(false) is { } result
                    ? TypedResults.Json(result, TerraBaseJsonContext.Default.HomeApplyResult)
                    : ErrorResponse.From(new NotFound("the database is not stored on this node"));
            }
        }
        catch (Exception error) when (error is StorageBusyException or QosMemoryException or SqlStatementException)
        {
            return ErrorResponse.From(new Unavailable("the home cannot apply the transaction now; retry"));
        }
    }

    internal static string? Invalid(RegionTransaction? transaction)
    {
        if (transaction is null)
        {
            return "the body is a region transaction";
        }

        if (!EntityIds.IsValid(transaction.Id))
        {
            return "id must be a ULID";
        }

        if (!TerraBaseOptions.IsRegion(transaction.Region))
        {
            return "region must be a region id";
        }

        if (transaction.Hlc is not { PhysicalMilliseconds: > 0 and < 1L << 48, NodeId: { Length: >= 1 and <= 63 } })
        {
            return "hlc must be an HLC timestamp with its node id";
        }

        if (transaction.Changes is not { Count: > 0 and <= HomeApplier.MaxChanges } changes)
        {
            return "changes must hold 1 to 1024 changes";
        }

        foreach (var change in changes)
        {
            if (change is null || change.Op is not ("insert" or "update" or "delete") || string.IsNullOrEmpty(change.Table) || change.Table.Length > 1024)
            {
                return "every change has an op of insert, update or delete and a table";
            }

            if ((change.Op == "insert" ? change.Values : change.Key) is not { Count: > 0 and <= 2000 })
            {
                return "an insert carries its values and an update or delete its key";
            }
        }

        return null;
    }

    private string Value(string name) => HttpContext.Request.RouteValues[name] as string ?? string.Empty;
}

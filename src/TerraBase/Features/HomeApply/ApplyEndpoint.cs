using System.Globalization;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.HomeApply;

internal sealed class ApplyEndpoint(HomePush.HomePush pushes, ITenantDirectory tenants, IMemoryBudget memory, TerraBaseOptions options)
    : EndpointWithoutRequest<IResult>
{
    internal const long MaxBodyBytes = 8 * 1024 * 1024;

    public override void Configure()
    {
        Post("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/apply");
        Name("applyRegionTransaction");
        Summary("Applies a transaction committed in another region at the database's home");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. The transaction is one its origin already stored in its "
            + "push stream, sent by a partner finishing a lost node's held transaction; the optional position query names its stream sequence. The home "
            + "never applies it here: it wakes the sequencer of that origin and database and answers 200 with the recorded state once the transaction is "
            + "decided (by its id, or because the database's frontier reached the position), otherwise 503 so the partner keeps its hold and retries. "
            + "404 when the database is not homed here or its placement does not name the region.");
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
            transaction = await HttpContext.Request.ReadFromJsonAsync(HomeApplyJsonContext.Default.RegionTransaction, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            transaction = null;
        }

        if (Invalid(transaction) is { } invalid)
        {
            return ErrorResponse.From(new Validation(invalid));
        }

        ulong? position = null;
        if (HttpContext.Request.Query.TryGetValue("position", out var positionText))
        {
            if (!ulong.TryParse(positionText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
            {
                return ErrorResponse.From(new Validation("position is a stream sequence"));
            }

            position = parsed;
        }

        var found = await tenants.ResolveProjectDatabaseAsync(HttpContext.RouteValue("project"), HttpContext.RouteValue("database"), cancellationToken).ConfigureAwait(false);
        if (found is not ResolvedProjectDatabase { Database: var database, Placement: var placement } || database.OrganizationId != HttpContext.RouteValue("organization"))
        {
            return found is Unavailable unavailable ? ErrorResponse.From(unavailable) : ErrorResponse.From(new NotFound("the database does not exist"));
        }

        if (placement.Home != options.Region || !(placement.AllRegions || placement.Replicas.Contains(transaction!.Region!)))
        {
            return ErrorResponse.From(new NotFound("the database is not homed here with a replica in that region"));
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            using (QosContext.Enter(database, memory))
            {
                return await pushes.DecidedAsync(database, transaction!, position, cancellationToken).ConfigureAwait(false) is { } result
                    ? TypedResults.Json(result, HomeApplyJsonContext.Default.HomeApplyResult)
                    : ErrorResponse.From(new Unavailable("the transaction is not decided at the home yet; retry"));
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

}

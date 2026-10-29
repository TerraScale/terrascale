using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>An internal, peer-authenticated all-holder cancellation of one auth intent generation.</summary>
internal sealed class CancelClosureEndpoint(
    ITenantDirectory tenants, IProjectCatalog projects, IStorageEngine engine, SystemCatalog catalog, ClosureCancellation cancellations, TerraBaseOptions options)
    : Endpoint<ClosureCancellationRequest, IResult>
{
    public override void Configure()
    {
        Put("/admin/projects/{project}/databases/{database}/closure-cancellations/{generation}");
        Name("cancelClosure");
        Summary("Fences an auth account intent generation on every holder");
        Description("Internal peer listener only. Version 1. 202 pending until all recorded and current catalog holders acknowledge the generation fence; "
            + "200 cancelled only if none holds an irreversible commit mark; 200 committed otherwise. Retry the same targetId and generation.");
        Tags("Administration");
        Produces<ClosureCancellationStatus>();
        Produces<ClosureCancellationStatus>(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(ClosureCancellationRequest request, CancellationToken cancellationToken)
    {
        var project = HttpContext.Request.RouteValues["project"] as string;
        var database = HttpContext.Request.RouteValues["database"] as string;
        var generation = HttpContext.Request.RouteValues["generation"] as string;
        if (request.Version != 1 || !EntityIds.IsValid(project) || !EntityIds.IsValid(generation) || !EntityIds.IsValid(request.TargetId) || string.IsNullOrEmpty(database))
        {
            return ErrorResponse.From(new Validation("version must be 1; project, generation and targetId must be canonical ULIDs; database is required"));
        }

        // The peer listener uses the same project-to-organization mapping as internal document requests. In
        // particular, this supports the isolated auth databases used by integration tests without a production bypass.
        var owner = await projects.ProjectOrganizationAsync(project, cancellationToken).ConfigureAwait(false);
        if (owner is not string organization)
        {
            return owner switch
            {
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                string => throw new InvalidOperationException(),
            };
        }

        var resolved = await tenants.FindDatabaseAsync(organization, project, database, cancellationToken).ConfigureAwait(false);
        if (resolved is not TenantContext found)
        {
            return resolved switch
            {
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                TenantContext => throw new InvalidOperationException(),
            };
        }

        if (await tenants.EnsureLocalDatabaseAsync(found, cancellationToken).ConfigureAwait(false) is not Done
            || await engine.OpenDatabaseAsync(found, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return ErrorResponse.From(new Unavailable("the database is not available here"));
        }

        var holders = await catalog.ReadAsync<HashSet<string>?>(
            _ => ValueTask.FromResult<HashSet<string>?>(EngineManagementService.CatalogHolders(options.MachineId)),
            static _ => null, cancellationToken).ConfigureAwait(false);
        if (holders is null)
        {
            return ErrorResponse.From(new Unavailable("the catalog is unavailable"));
        }

        await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        var record = await SystemCatalog.GetAsync(transaction, ClosureCancellation.Fences, generation, TerraBaseJsonContext.Default.ClosureCancellationRecord, cancellationToken).ConfigureAwait(false);
        if (record is not null && (record.TargetId is not null && record.TargetId != request.TargetId || record.Generation != generation))
        {
            return ErrorResponse.From(new Conflict("this generation names another intent", "closure_conflict"));
        }

        if (record is null)
        {
            record = new(generation, request.TargetId, [.. holders.Order(StringComparer.Ordinal)]);
            await SystemCatalog.PutAsync(transaction, ClosureCancellation.Fences, generation, record, TerraBaseJsonContext.Default.ClosureCancellationRecord, cancellationToken).ConfigureAwait(false);
        }

        await cancellations.FenceAsync(transaction, generation, cancellationToken, request.TargetId).ConfigureAwait(false);
        holders.UnionWith(record.ExpectedHolders);
        var pending = 0;
        var committed = false;
        var unfinished = 0;
        var ownWasUnfinished = false;
        foreach (var holder in holders)
        {
            var receipt = await SystemCatalog.GetAsync(transaction, ClosureCancellation.Fences, generation + "/holders/" + holder,
                TerraBaseJsonContext.Default.ClosureCancellationReceipt, cancellationToken).ConfigureAwait(false);
            if (receipt?.Generation != generation || receipt.MachineId != holder)
            {
                pending++;
            }
            else
            {
                committed |= receipt.Committed;
                unfinished += receipt.Finalized ? 0 : 1;
                ownWasUnfinished |= holder == options.MachineId && !receipt.Finalized;
            }
        }

        if (pending == 0 && !committed && unfinished > 0)
        {
            // The decision is a separate immutable key: a late initial request cannot overwrite it. Only after every
            // holder fenced uncommitted writes may any holder remove the intent; committed work keeps its driver.
            var decision = generation + "/cancelled";
            if (await transaction.GetVersionAsync(ClosureCancellation.Fences, decision, cancellationToken).ConfigureAwait(false) is null)
            {
                _ = await transaction.PutAsync(ClosureCancellation.Fences, decision, "{}"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            await cancellations.FenceAsync(transaction, generation, cancellationToken, request.TargetId).ConfigureAwait(false);
            var own = await SystemCatalog.GetAsync(transaction, ClosureCancellation.Fences, generation + "/holders/" + options.MachineId,
                TerraBaseJsonContext.Default.ClosureCancellationReceipt, cancellationToken).ConfigureAwait(false);
            // Its first receipt in this snapshot was not finalized; the second one is, in this same durable transaction.
            unfinished -= ownWasUnfinished && own?.Finalized == true ? 1 : 0;
            pending = unfinished;
        }

        if (pending == 0 && record.TargetId is not null)
        {
            // Every holder installed the fence and the result is final. Keep only the random generation and infrastructure
            // receipts; its subject-derived address is no longer needed, including for a replay of this request.
            await SystemCatalog.PutAsync(transaction, ClosureCancellation.Fences, generation, record with { TargetId = null },
                TerraBaseJsonContext.Default.ClosureCancellationRecord, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var status = new ClosureCancellationStatus(generation, pending > 0 ? "pending" : committed ? "committed" : "cancelled", pending);
        return pending > 0 ? TypedResults.Json(status, TerraBaseJsonContext.Default.ClosureCancellationStatus, statusCode: StatusCodes.Status202Accepted)
            : TypedResults.Ok(status);
    }
}

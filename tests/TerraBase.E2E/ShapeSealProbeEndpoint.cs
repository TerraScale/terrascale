using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.E2E;

/// <summary>
/// Test only, on a region's internal listener: seals one shape of the harness's database from offset 0 with the server's
/// real <see cref="IShapeSealer"/> (bounded, as a caller gives up), then answers the remote durable frontier and every
/// chunk the sealer reads back, in order, until the first it does not serve.
/// </summary>
public sealed class ShapeSealProbeEndpoint : EndpointWithoutRequest<IResult>
{
    internal const string ProbeRoute = "/v1/e2e/shape-seal";
    private static readonly TimeSpan SealBound = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProbeRoute);
        Name("e2eShapeSeal");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var query = HttpContext.Request.Query;
        var services = HttpContext.RequestServices;
        var (collection, shapeId, handle) = (query["collection"].ToString(), query["shape"].ToString(), query["handle"].ToString());
        var tenant = new TenantContext(query["organization"].ToString(), PlanTier.Hobby, query["project"].ToString(), query["database"].ToString());
        var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, cancellationToken)
            ?? throw new InvalidOperationException("the database is missing");
        string generation;
        await using (var transaction = await store.BeginTransactionAsync(write: false, cancellationToken))
        {
            generation = (await transaction.GetCollectionAsync(collection, cancellationToken)
                ?? throw new InvalidOperationException("the collection is missing")).Id;
        }

        var sealer = services.GetRequiredService<IShapeSealer>();
        long? sealedTo = null;
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            bound.CancelAfter(SealBound);
            try
            {
                sealedTo = await sealer.SealUpToHeadAsync(store, new ShapeSealDefinition(shapeId, handle, collection, generation,
                    Convert.FromHexString(shapeId)), 0, bound.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Gave up: whatever was sealed before the bound is still read back below.
            }
        }

        var chunks = new JsonArray();
        for (var offset = 0L; ;)
        {
            using var chunk = await sealer.ReadChunkAsync(store, handle, offset, cancellationToken);
            if (chunk is null)
            {
                break;
            }

            chunks.Add(new JsonObject { ["end"] = chunk.EndOffset, ["values_at"] = chunk.ValuesAt, ["body"] = JsonNode.Parse(chunk.Body.Span) });
            offset = chunk.EndOffset;
        }

        var answer = new JsonObject
        {
            ["sealed"] = sealedTo,
            ["frontier"] = await sealer.FrontierAsync(store, cancellationToken),
            ["chunks"] = chunks,
        };
        // Inspect persisted bytes too: hiding an unsafe chunk at the read gate alone does not satisfy this regression.
        var persisted = new JsonArray();
        await using (var transaction = await services.GetRequiredService<IShapeStorage>().BeginTransactionAsync(store, write: false, cancellationToken))
        {
            var storage = (IShapeStorageTransaction)transaction;
            var memory = services.GetRequiredService<TerraBase.Features.Memory.IMemoryBudget>();
            for (var offset = 0L; ;)
            {
                using var chunk = await storage.ReadShapeChunkAsync(handle, offset, memory, cancellationToken);
                if (chunk is null)
                {
                    break;
                }

                persisted.Add(new JsonObject { ["end"] = chunk.EndOffset, ["values_at"] = chunk.ValuesAt, ["body"] = JsonNode.Parse(chunk.Body.Span) });
                offset = chunk.EndOffset;
            }
        }

        answer["persisted"] = persisted;
        return Results.Text(answer.ToJsonString(), "application/json");
    }
}

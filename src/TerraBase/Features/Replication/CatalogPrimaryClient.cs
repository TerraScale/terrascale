using Grpc.Core;
using TerraBase.Engine.Cluster;
using TerraBase.Errors;
using TerraBase.Features.Replication.V1;

namespace TerraBase.Features.Replication;

/// <summary>A catalog follower's calls to its catalog primary's peer services, over its internal listener.</summary>
public interface ICatalogPrimaryClient
{
    /// <summary>
    /// Sends <paramref name="batch"/> to <c>CatalogUsage.Add</c>: <see cref="Done"/> once the primary acknowledged it,
    /// <see cref="Validation"/> when it refused it as invalid and <see cref="QuotaExceeded"/> when it lacked the capacity to
    /// take it; it added none of a refused batch. Throws when the outcome is unknown.
    /// </summary>
    Task<AddUsageResult> AddUsageAsync(UsageBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="batch"/> to <c>CatalogQueryShapes.Add</c>: true after durable acknowledgement,
    /// false when the whole batch was rejected as invalid before mutation. Throws when the outcome is unknown.
    /// </summary>
    Task<bool> AddQueryShapesAsync(QueryShapesBatch batch, CancellationToken cancellationToken);
}

/// <summary>What a catalog primary did with a usage batch: added it, refused it for good, or lacked the capacity to take it.</summary>
public readonly union AddUsageResult(Done, Validation, QuotaExceeded);

internal sealed class CatalogPrimaryClient(IMembership membership) : ICatalogPrimaryClient
{
    // Batches are bounded; query shapes are durably committed before acknowledgement.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<AddUsageResult> AddUsageAsync(UsageBatch batch, CancellationToken cancellationToken)
    {
        var primary = await membership.CatalogPrimaryAsync(cancellationToken).ConfigureAwait(false) ?? throw new IOException("no catalog primary is known");
        // ponytail: a channel per batch, one every 10 seconds; keep one if that matters.
        using var channel = PeerChannel.Create(primary);
        try
        {
            // gRPC measures deadlines on the system clock, whatever clock the counts are kept on.
            await new CatalogUsage.CatalogUsageClient(channel)
                .AddAsync(batch, deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new Done();
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            // The host recognizes caller cancellation; the pending batch remains available for the shutdown drain.
            throw new OperationCanceledException("catalog usage send canceled", error, cancellationToken);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            // The primary validates a whole batch before it adds any of it, so a retry would be refused the same way.
            return new Validation("the catalog primary refused the usage batch");
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.ResourceExhausted)
        {
            // Memory admission and the sender bound refuse a batch before any of it is added; a smaller one may fit.
            return new QuotaExceeded("the catalog primary lacked the capacity for the usage batch");
        }
    }

    public async Task<bool> AddQueryShapesAsync(QueryShapesBatch batch, CancellationToken cancellationToken)
    {
        var primary = await membership.CatalogPrimaryAsync(cancellationToken).ConfigureAwait(false) ?? throw new IOException("no catalog primary is known");
        using var channel = PeerChannel.Create(primary);
        try
        {
            await new CatalogQueryShapes.CatalogQueryShapesClient(channel)
                .AddAsync(batch, deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            return false;
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Catalog query shapes call canceled", error, cancellationToken);
        }
    }
}

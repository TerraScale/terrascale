using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Features.Replication.V1;

namespace TerraBase.Features.Replication;

/// <summary>A catalog follower's calls to its catalog primary's peer services, over the peer listener's mutual TLS.</summary>
public interface ICatalogPrimaryClient
{
    /// <summary>
    /// Sends <paramref name="batch"/> to <c>CatalogUsage.Add</c>: <see langword="true"/> once the primary acknowledged it,
    /// <see langword="false"/> when it refused it as invalid (it added none of it). Throws when the outcome is unknown.
    /// </summary>
    Task<bool> AddUsageAsync(UsageBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="batch"/> to <c>CatalogQueryShapes.Add</c>: true after durable acknowledgement,
    /// false when the whole batch was rejected as invalid before mutation. Throws when the outcome is unknown.
    /// </summary>
    Task<bool> AddQueryShapesAsync(QueryShapesBatch batch, CancellationToken cancellationToken);
}

internal sealed class CatalogPrimaryClient(TerraBaseOptions options, PeerCredentials credentials) : ICatalogPrimaryClient
{
    // Batches are bounded; query shapes are durably committed before acknowledgement.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<bool> AddUsageAsync(UsageBatch batch, CancellationToken cancellationToken)
    {
        using var lease = credentials.Capture();
        // ponytail: a channel (TLS handshake) per batch, one every 10 seconds; keep one per credential generation if that matters.
        using var channel = PeerChannel.Create(options.CatalogPrimary!, lease);
        try
        {
            // gRPC measures deadlines on the system clock, whatever clock the counts are kept on.
            await new CatalogUsage.CatalogUsageClient(channel)
                .AddAsync(batch, deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            // The host recognizes caller cancellation; the pending batch remains available for the shutdown drain.
            throw new OperationCanceledException("catalog usage send canceled", error, cancellationToken);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            // The primary validates a whole batch before it adds any of it, so a retry would be refused the same way.
            return false;
        }
    }

    public async Task<bool> AddQueryShapesAsync(QueryShapesBatch batch, CancellationToken cancellationToken)
    {
        using var lease = credentials.Capture();
        using var channel = PeerChannel.Create(options.CatalogPrimary!, lease);
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
    }
}

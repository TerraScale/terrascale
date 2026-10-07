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
}

internal sealed class CatalogPrimaryClient(TerraBaseOptions options, PeerCredentials credentials) : ICatalogPrimaryClient
{
    // A batch is small and the primary only adds it to memory.
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
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            // The primary validates a whole batch before it adds any of it, so a retry would be refused the same way.
            return false;
        }
    }
}

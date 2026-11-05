using Grpc.Net.Client;
using TerraBase.Configuration;
using TerraBase.Features.Clock;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

// Deletion is off the write hot path. All nodes, including hold partners, must evict before204.
// An unavailable peer keeps deletion pending; its restart has empty caches and cold reads fail closed.
internal sealed class KeyRevocationPeers(TerraBaseOptions options, InternalCredential credential) : IDisposable
{
    private readonly HttpClient _http = PeerHttp.Create(credential);

    internal async Task RevokeAsync(TenantContext database, CancellationToken token)
    {
        if (options.PeersFile is null)
        {
            return;
        }
        var nodes = await new PeersFile(options).ListAsync(token).ConfigureAwait(false);
        if (nodes.Length > 1024 || !nodes.Any(node => node.Name == options.MachineId))
        {
            throw new IOException("key revocation requires a bounded complete peer inventory");
        }
        await Parallel.ForEachAsync(nodes.Where(node => node.Name != options.MachineId),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (node, cancellationToken) =>
            {
                var address = new UriBuilder(Uri.UriSchemeHttp, node.Address.ToString(), options.InternalPort).Uri;
                using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    HttpClient = _http,
                    CompressionProviders = [new ClockCompression()],
                    MaxReceiveMessageSize = 4096,
                    MaxSendMessageSize = 4096,
                });
                var reply = await new KeyRevocation.KeyRevocationClient(channel).RevokeAsync(new KeyRevocationRequest
                {
                    Organization = database.OrganizationId,
                    Project = database.ProjectId!,
                    Database = database.DatabaseId!,
                }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
                if (!reply.Revoked)
                {
                    throw new IOException("a peer did not acknowledge the database key tombstone");
                }
            }).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}

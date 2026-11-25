using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public static class ReplicaWatermarks
{
    public static async ValueTask<CausalBookmark> ConfirmAsync(
        QuicRpcClient client,
        ClusterOptions cluster,
        IReadOnlyList<PeerNodeInfo> peers,
        Ulid databaseId,
        CausalBookmark local,
        CancellationToken cancellationToken)
    {
        if (peers.Count == 0)
        {
            return local;
        }

        var request = new CatchUpRequestMessage(databaseId, cluster.NodeId, local, MaxEntries: 1);
        var observed = new CausalBookmark[peers.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, peers.Count),
            new ParallelOptions { MaxDegreeOfParallelism = cluster.MaxReplicationParallelism, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var response = await client.CallAsync<CatchUpRequestMessage, CatchUpResponseMessage>(
                    peers[index].NodeId, QuicMessageType.CatchUpRequest, request, cluster.PingTimeout, token);
                observed[index] = response switch
                {
                    CatchUpResponseMessage { Found: true } page when page.DatabaseId == databaseId => page.Bookmark,
                    CatchUpResponseMessage or QuicTimeout or QuicUnavailable or QuicRemoteError => CausalBookmark.Empty,
                };
            });

        return CausalBookmark.Create(local.Entries.Select(entry => new KeyValuePair<string, long>(
            entry.Key, Math.Min(entry.Value, observed.Min(peer => peer.Get(entry.Key))))));
    }
}

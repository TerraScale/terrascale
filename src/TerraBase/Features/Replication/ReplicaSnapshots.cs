using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>Imports a whole pinned snapshot, then its obsolete-origin prefix proofs. Partial transfers certify nothing.</summary>
internal sealed class ReplicaSnapshots(IReplicaSnapshotImporter importer, IReplicaStates states, PeerCredential credential)
{
    internal async Task<bool> PullAsync(TenantContext database, ClusterMember peer, IReadOnlySet<ulong> origins, CancellationToken cancellationToken)
    {
        if (await states.DigestAsync(database, expected: null, cancellationToken).ConfigureAwait(false) is not { } local)
        {
            return false;
        }

        using var channel = PeerChannel.Create(new Uri(peer.PeerAddress), credential);
        var client = new V1.Replication.ReplicationClient(channel);
        var digest = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database, null) },
            deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!digest.Held || ChangeGroups.Incarnation(new Database { Incarnation = digest.Incarnation }) is not { } source)
        {
            return false;
        }

        // Deliberately read every state, even if digests match. Coverage comes from one native read snapshot and its
        // terminal vector, never from equality of independently sampled XOR roots.
        using var call = client.ReadSnapshot(new DigestRequest { Database = ChangeGroups.Wire(database, source) },
            deadline: DateTime.UtcNow.AddMinutes(10), cancellationToken: cancellationToken);
        return await importer.ImportAsync(database, local.Incarnation, Pages(cancellationToken), origins, cancellationToken).ConfigureAwait(false);

        async IAsyncEnumerable<ReplicaSnapshotPage> Pages([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            var complete = false;
            while (await call.ResponseStream.MoveNext(token).ConfigureAwait(false))
            {
                var page = call.ResponseStream.Current;
                if (complete || page.Page is null || page.Applied.Count > 1024 || !page.Complete && page.Applied.Count != 0
                    || page.CollectedBefore > long.MaxValue)
                {
                    throw new InvalidDataException("invalid snapshot terminal frame");
                }

                if (page.Complete && (!page.Page.States.IsEmpty
                    || ChangeGroups.Incarnation(new Database { Incarnation = page.Page.Incarnation }) != source))
                {
                    throw new InvalidDataException("invalid snapshot completion page");
                }

                var values = page.Complete ? [] : AntiEntropy.Decode(database, page.Page, source);
                Dictionary<ReplicaId, Hlc>? applied = null;
                if (page.Complete)
                {
                    applied = [];
                    foreach (var cut in page.Applied)
                    {
                        if (cut.Replica == 0 || cut.Stamp == 0 || cut.Stamp > long.MaxValue
                            || !applied.TryAdd(new ReplicaId(cut.Replica), new Hlc(cut.Stamp)))
                        {
                            throw new InvalidDataException("invalid snapshot origin cut");
                        }
                    }

                    complete = true;
                }

                yield return new ReplicaSnapshotPage(values, applied, new Hlc(page.CollectedBefore));
            }
        }
    }
}

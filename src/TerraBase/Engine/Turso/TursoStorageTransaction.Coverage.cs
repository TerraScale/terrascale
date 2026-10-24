using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    public async ValueTask RequireAppliedAsync(ReplicaId origin, Hlc expected, CancellationToken cancellationToken)
    {
        _ = await RunAsync(() =>
        {
            ThrowIfReadOnly();
            var command = Command("SELECT applied FROM _tb_peers WHERE node = $node;");
            command.Parameters.AddWithValue("$node", unchecked((long)origin.Value));
            var applied = command.ExecuteScalar() is long stored ? unchecked((ulong)stored) : 0;
            if (applied < expected.Value)
            {
                throw new ReceiverCoverageException();
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyDictionary<ReplicaId, Hlc>> GetAppliedAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyDictionary<ReplicaId, Hlc>>(() =>
        {
            var applied = new Dictionary<ReplicaId, Hlc>();
            using var reader = Command("SELECT node, applied FROM _tb_peers WHERE applied > 0;").ExecuteReader();
            while (reader.Read())
            {
                applied[new ReplicaId(unchecked((ulong)reader.GetInt64(0)))] = new Hlc(unchecked((ulong)reader.GetInt64(1)));
            }

            return applied;
        }, cancellationToken);

    public ValueTask<bool> HoldsHistoryAtOrBelowAsync(Hlc cut, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var dead = Command("SELECT EXISTS (SELECT 1 FROM _tb_state WHERE live = 0 AND stamp <= $cut);");
            dead.Parameters.AddWithValue("$cut", Version(cut));
            if ((long)dead.ExecuteScalar()! != 0)
            {
                return true;
            }

            if (store.IsSystem)
            {
                return false;
            }

            var feed = Command("SELECT EXISTS (SELECT 1 FROM _tb_feed WHERE stamp <= $cut);");
            feed.Parameters.AddWithValue("$cut", Version(cut));
            return (long)feed.ExecuteScalar()! != 0;
        }, cancellationToken);
}

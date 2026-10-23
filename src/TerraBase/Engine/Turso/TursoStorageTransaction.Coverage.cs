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
}

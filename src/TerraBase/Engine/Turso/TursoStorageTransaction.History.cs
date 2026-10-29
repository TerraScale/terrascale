using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    internal ValueTask<int> CollectHistoryAsync(Hlc before, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command("""
                DELETE FROM _tb_state WHERE rowid IN (
                    SELECT rowid FROM _tb_state WHERE live = 0 AND stamp < $before ORDER BY stamp LIMIT 1024
                );
                """);
            command.Parameters.AddWithValue("$before", Version(before));
            return command.ExecuteNonQuery();
        }, cancellationToken, mutation: true);

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

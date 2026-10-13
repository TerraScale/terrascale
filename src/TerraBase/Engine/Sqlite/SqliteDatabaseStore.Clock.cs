using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteDatabaseStore
{
    // The caller holds admission and has bootstrapped this unpublished store.
    internal async Task SeedClockAsync(CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stamp = await session.ExecuteAsync(io, () =>
            {
                // shortcut: cold opens scan state and feed stamps; add a stamp index if measured open latency requires it.
                const string states = "SELECT MAX(stamp) AS stamp FROM _tb_state";
                var sources = IsSystem ? states : states
                    + " UNION ALL SELECT MAX(stamp) FROM _tb_feed UNION ALL SELECT MAX(stamp) FROM _tb_feed_floor";
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(MAX(stamp), 0) FROM (" + sources + ");";
                return new Hlc(checked((ulong)(long)command.ExecuteScalar()!));
            }, cancellationToken).ConfigureAwait(false);
            Member!.Clock.Seed(stamp);
        }
        catch
        {
            await io.RunAsync(() => pool.Discard(session), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await ReturnAsync(session).ConfigureAwait(false);
    }
}

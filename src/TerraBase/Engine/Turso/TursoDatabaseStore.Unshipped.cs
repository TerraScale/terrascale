using System.Diagnostics.Metrics;
using Turso.Data.Sqlite;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    private const long UnshippedCap = 256L * 1024 * 1024;

    private long _unshippedFloor = -1;
    private long _unshippedThrough;
    private long _unshippedBytes;

    internal Counter<long>? UnshippedDiscarded { get; init; }

    internal void TrimUntapped(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (transaction is null)
        {
            using var owned = connection.BeginTransaction();
            TrimUntapped(connection, owned);
            owned.Commit();
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var below = long.MaxValue;
        if (TursoStorageTransaction.BackupFloor(command) is { } floor)
        {
            below = floor;
            if (floor != _unshippedFloor)
            {
                (_unshippedFloor, _unshippedThrough, _unshippedBytes) = (floor, floor, 0);
            }

            command.CommandText = """
                SELECT coalesce(sum(coalesce(octet_length(before), 0) + coalesce(octet_length(after), 0) + 32), 0), coalesce(max(change_id), $through)
                FROM turso_cdc WHERE change_id > $through;
                """;
            command.Parameters.AddWithValue("$through", _unshippedThrough);
            using (var reader = command.ExecuteReader())
            {
                _ = reader.Read();
                _unshippedBytes += reader.GetInt64(0);
                _unshippedThrough = reader.GetInt64(1);
            }

            command.Parameters.Clear();
            if (_unshippedBytes > UnshippedCap)
            {
                TursoStorageTransaction.ClearBackupFloor(command);
                below = long.MaxValue;
                logger?.ZLogWarning($"The change log of database {name:@Database} grew to {_unshippedBytes:@Bytes} bytes after position {floor:@Floor}, the last one its backup stored, while no backup tap was open; it is discarded and the backup records the hole when its tap opens");
                UnshippedDiscarded?.Add(1);
                _unshippedFloor = -1;
            }
        }

        _ = TursoStorageTransaction.Trim(connection, transaction, below, IsSystem);
    }
}

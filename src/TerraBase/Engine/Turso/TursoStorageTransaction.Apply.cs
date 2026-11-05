using TerraScale.TerraBase.Contracts;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private bool _foreignKeysSuspended;

    ValueTask IStorageTransaction.SuspendForeignKeysAsync(CancellationToken cancellationToken) =>
        new(RunAsync(() =>
        {
            if (!write || store.IsSystem)
            {
                throw new InvalidOperationException("foreign keys are suspended only in a client database's write transaction");
            }

            if (!_foreignKeysSuspended)
            {
                _foreignKeysSuspended = true;
                ForeignKeys(on: false);
            }

            return true;
        }, cancellationToken).AsTask());

    ValueTask<IReadOnlyList<CapturedRowChange>> IStorageTransaction.ReadCapturedChangesAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<CapturedRowChange>>(() =>
        {
            if (!write || store.IsSystem || !HasChangeTable())
            {
                return [];
            }

            using var last = session.Connection.CreateCommand();
            last.Transaction = transaction;
            last.CommandText = "SELECT change_type, change_txn_id FROM turso_cdc ORDER BY change_id DESC LIMIT 1;";
            long first;
            using (var reader = last.ExecuteReader())
            {
                if (!reader.Read() || reader.GetInt64(0) == 2 || reader.IsDBNull(1))
                {
                    return [];
                }

                first = reader.GetInt64(1);
            }

            var changes = new List<CapturedRowChange>();
            var columns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            using var read = session.Connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT change_type, table_name, before, after, updates, id, change_id FROM turso_cdc WHERE change_id >= $first AND change_type IN (-1, 0, 1) ORDER BY change_id;";
            read.Parameters.AddWithValue("$first", first);
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read())
                {
                    var table = reader.GetString(1);
                    if (TerraBaseSqlNames.IsInternalTable(table))
                    {
                        continue;
                    }

                    if (!columns.TryGetValue(table, out var names))
                    {
                        columns[table] = names = TableColumns(table);
                    }

                    var beforeImage = reader.IsDBNull(2) ? null : TursoSession.Bytes(reader, 2);
                    var afterImage = reader.IsDBNull(3) ? null : TursoSession.Bytes(reader, 3);
                    var before = beforeImage is null ? null : SqliteRecord.Decode(beforeImage, names.Length);
                    var after = afterImage is null ? null : SqliteRecord.Decode(afterImage, names.Length);
                    bool[]? updated = null;
                    if (!reader.IsDBNull(4))
                    {
                        var flags = SqliteRecord.Decode(TursoSession.Bytes(reader, 4), names.Length);
                        updated = [.. flags.Select(flag => flag is long set && set != 0)];
                    }

                    changes.Add(new CapturedRowChange(table, (int)reader.GetInt64(0), names, before, after, updated,
                        reader.IsDBNull(5) ? null : new CapturedImages(reader.GetInt64(5), beforeImage, afterImage, reader.GetInt64(6))));
                }
            }

            return changes;
        }, cancellationToken);

    private string[] TableColumns(string table)
    {
        using var command = session.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_xinfo(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(6) is not (1 or 2))
            {
                names.Add(reader.GetString(1));
            }
        }

        return [.. names];
    }

    private void ForeignKeys(bool on)
    {
        using var command = session.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = on ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;";
        command.ExecuteNonQuery();
        session.ForgetSchema();
    }

    private void RestoreForeignKeys()
    {
        if (_foreignKeysSuspended)
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
            session.ForgetSchema();
            _foreignKeysSuspended = false;
        }
    }
}

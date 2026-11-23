using System.Diagnostics;

namespace TerraBase.Storage;

internal readonly record struct ObservedRow(RowKey Key, ulong CausalLength, HlcTimestamp RowHlc);

internal static class MetaStore
{
    public const string SchemaSql =
        "CREATE TABLE IF NOT EXISTS _tb_meta (tbl TEXT NOT NULL, pk BLOB NOT NULL, meta BLOB NOT NULL, PRIMARY KEY (tbl, pk)) WITHOUT ROWID;"
        + "CREATE TABLE IF NOT EXISTS _tb_applied (tx_id BLOB PRIMARY KEY, applied_at INTEGER NOT NULL) WITHOUT ROWID;"
        + "CREATE TABLE IF NOT EXISTS _tb_vv (origin TEXT PRIMARY KEY, seq INTEGER NOT NULL, schema_epoch INTEGER NOT NULL) WITHOUT ROWID;";

    public const string SelectMetaSql = "SELECT meta FROM _tb_meta WHERE tbl = ?1 AND pk = ?2";

    public const string SelectTableHlcSql = "SELECT meta FROM _tb_meta WHERE tbl = ?1 AND pk = X''";

    public const string SelectVersionsSql = "SELECT origin, seq, schema_epoch FROM _tb_vv";

    public const string SelectAppliedSql = "SELECT 1 FROM _tb_applied WHERE tx_id = ?1";

    private const string UpsertMetaSql =
        "INSERT INTO _tb_meta (tbl, pk, meta) VALUES (?1, ?2, ?3) ON CONFLICT (tbl, pk) DO UPDATE SET meta = excluded.meta";

    private const string TouchTableSql =
        "INSERT INTO _tb_meta (tbl, pk, meta) VALUES (?1, X'', ?2) ON CONFLICT (tbl, pk) DO UPDATE SET meta = excluded.meta WHERE excluded.meta > _tb_meta.meta";

    private const string UpsertVersionSql =
        "INSERT INTO _tb_vv (origin, seq, schema_epoch) VALUES (?1, ?2, ?3) ON CONFLICT (origin) DO UPDATE SET seq = excluded.seq, schema_epoch = excluded.schema_epoch";

    private const string InsertAppliedSql = "INSERT OR IGNORE INTO _tb_applied (tx_id, applied_at) VALUES (?1, ?2)";

    public static RowMeta Load(WriteScope scope, string table, byte[] key)
    {
        var row = scope.First(SelectMetaSql, SqlValue.FromText(table), SqlValue.FromBlob(key));
        if (row is null || !row[0].TryGetBlob(out var blob))
        {
            return new RowMeta();
        }

        if (RowMeta.Decode(blob.Span) is { } meta)
        {
            return meta;
        }

        scope.Fail(new SqliteStatus(SqliteNative.Corrupt, string.Concat("corrupt _tb_meta row for table ", table)));
        return new RowMeta();
    }

    public static void Save(WriteScope scope, string table, byte[] key, RowMeta meta) =>
        scope.Run(UpsertMetaSql, SqlValue.FromText(table), SqlValue.FromBlob(key), SqlValue.FromBlob(meta.Encode()));

    public static void TouchTables(WriteScope scope, IReadOnlyDictionary<string, HlcTimestamp> tables)
    {
        Span<byte> encoded = stackalloc byte[HlcTimestamp.EncodedSize];
        foreach (var (table, hlc) in tables)
        {
            HlcCodec.Write(encoded, hlc);
            scope.Run(TouchTableSql, SqlValue.FromText(table), SqlValue.FromBlob(encoded.ToArray()));
        }
    }

    public static void SaveVersion(WriteScope scope, string origin, long sequence, ulong schemaEpoch) =>
        scope.Run(UpsertVersionSql, SqlValue.FromText(origin), SqlValue.FromIntegral(sequence), SqlValue.FromIntegral((long)schemaEpoch));

    public static void MarkApplied(WriteScope scope, Ulid txId, long appliedAtMs) =>
        scope.Run(InsertAppliedSql, SqlValue.FromBlob(txId.ToByteArray()), SqlValue.FromIntegral(appliedAtMs));

    public static bool IsApplied(WriteScope scope, Ulid txId) =>
        scope.First(SelectAppliedSql, SqlValue.FromBlob(txId.ToByteArray())) is not null;

    public static void Raise(Dictionary<string, HlcTimestamp> tables, string table, HlcTimestamp hlc)
    {
        tables[table] = tables.TryGetValue(table, out var existing) ? HlcCodec.Max(existing, hlc) : hlc;
    }
}

internal static class LocalStamper
{
    public static List<RowMutation> Stamp(
        WriteScope scope,
        IReadOnlyList<NetChange> changes,
        HlcTimestamp hlc,
        bool persist,
        List<ObservedRow> observed,
        Dictionary<string, HlcTimestamp> tables)
    {
        List<RowMutation> mutations = new(changes.Count);
        foreach (var change in changes)
        {
            var schema = change.Schema;
            var meta = MetaStore.Load(scope, schema.Table, change.EncodedKey);
            observed.Add(new ObservedRow(new RowKey(schema.Table, PrimaryKeyCodec.ToKeyString(change.EncodedKey)), meta.CausalLength, meta.RowHlc));
            var next = new RowMeta { CausalLength = meta.CausalLength, RowHlc = hlc };
            List<ColumnChange> columnChanges = [];
            switch (change.Op)
            {
                case MutationOp.Insert:
                    next.CausalLength = meta.CausalLength == 0 ? 1 : meta.Alive ? meta.CausalLength + 2 : meta.CausalLength + 1;
                    for (var index = 0; index < schema.DataColumns.Count; index++)
                    {
                        var column = schema.DataColumns[index];
                        columnChanges.Add(new ColumnChange(column.Name, ChangeKind.Assign, 0, Value(change.Final, index), 0));
                        next.Columns[column.Index] = new ColumnMeta(hlc, 0, 0);
                    }

                    break;
                case MutationOp.Update:
                    next.CausalLength = meta.Alive ? meta.CausalLength : meta.CausalLength + 1;
                    foreach (var (ordinal, column) in meta.Columns)
                    {
                        next.Columns[ordinal] = column;
                    }

                    foreach (var index in change.ChangedColumns)
                    {
                        var column = schema.DataColumns[index];
                        var current = next.Column(column.Index);
                        var final = Value(change.Final, index);
                        if (!column.IsDeltaCounter)
                        {
                            columnChanges.Add(new ColumnChange(column.Name, ChangeKind.Assign, current.Epoch, final, 0));
                            next.Columns[column.Index] = current with { Hlc = hlc };
                            continue;
                        }

                        var hasBefore = Value(change.Original, index).TryGetIntegral(out var before);
                        var hasAfter = final.TryGetIntegral(out var after);
                        if (hasBefore && hasAfter && !change.Replaced && !change.AbsoluteColumns.Contains(column.Name))
                        {
                            var delta = after - before;
                            columnChanges.Add(new ColumnChange(column.Name, ChangeKind.DeltaOp, current.Epoch, SqlValue.Null, delta));
                            next.Columns[column.Index] = current with { DeltaSum = current.DeltaSum + delta };
                        }
                        else
                        {
                            columnChanges.Add(new ColumnChange(column.Name, ChangeKind.AbsoluteReset, current.Epoch + 1, final, 0));
                            next.Columns[column.Index] = new ColumnMeta(hlc, current.Epoch + 1, 0);
                        }
                    }

                    break;
                case MutationOp.Delete:
                    next.CausalLength = meta.Alive ? meta.CausalLength + 1 : meta.CausalLength + 2;
                    break;
                default:
                    throw new UnreachableException();
            }

            if (persist)
            {
                MetaStore.Save(scope, schema.Table, change.EncodedKey, next);
            }

            MetaStore.Raise(tables, schema.Table, hlc);
            mutations.Add(new RowMutation(schema.Table, change.Op, next.CausalLength, change.PrimaryKey, columnChanges));
        }

        return mutations;
    }

    private static SqlValue Value(SqlValue[]? values, int index) =>
        values is not null && index < values.Length ? values[index] : SqlValue.Null;
}

internal static class CrdtApplier
{
    public static void Apply(WriteScope scope, RowMutation mutation, HlcTimestamp hlc, Dictionary<string, HlcTimestamp> tables)
    {
        if (scope.Connection.Schema(mutation.Table) is not { } schema || mutation.PrimaryKey.Count != schema.PrimaryKeyColumnNames.Count)
        {
            return;
        }

        var keyValues = mutation.PrimaryKey.ToArray();
        var key = PrimaryKeyCodec.Encode(keyValues);
        var meta = MetaStore.Load(scope, schema.Table, key);
        if (scope.Failed || mutation.CausalLength < meta.CausalLength)
        {
            return;
        }

        var sql = scope.Sql(schema);
        var current = scope.First(sql.SelectRow, keyValues);
        if (scope.Failed)
        {
            return;
        }

        var deleteOp = mutation.Op == MutationOp.Delete;
        var odd = (mutation.CausalLength & 1) == 1;
        if (deleteOp == odd)
        {
            return;
        }

        RowMeta next;
        if (deleteOp)
        {
            if (mutation.CausalLength > meta.CausalLength)
            {
                if (current is not null)
                {
                    scope.Run(sql.DeleteRow, keyValues);
                }

                next = new RowMeta { CausalLength = mutation.CausalLength, RowHlc = hlc };
            }
            else
            {
                next = meta;
                next.RowHlc = HlcCodec.Max(meta.RowHlc, hlc);
            }
        }
        else if (mutation.CausalLength > meta.CausalLength)
        {
            next = new RowMeta { CausalLength = mutation.CausalLength, RowHlc = hlc };
            List<TableColumn> columns = [];
            List<SqlValue> values = [];
            foreach (var change in mutation.Changes)
            {
                if (schema.FindColumn(change.Column) is not { IsPrimaryKey: false, Generated: false } column)
                {
                    continue;
                }

                columns.Add(column);
                switch (change.Kind)
                {
                    case ChangeKind.Assign or ChangeKind.AbsoluteReset:
                        values.Add(change.Value);
                        next.Columns[column.Index] = new ColumnMeta(hlc, change.Epoch, 0);
                        break;
                    case ChangeKind.DeltaOp:
                        values.Add(SqlValue.FromIntegral(change.Delta));
                        next.Columns[column.Index] = new ColumnMeta(HlcTimestamp.Zero, change.Epoch, change.Delta);
                        break;
                    default:
                        throw new UnreachableException();
                }
            }

            Write(scope, sql, keyValues, current is not null, columns, values, orIgnore: mutation.Op == MutationOp.Update);
        }
        else
        {
            next = meta;
            next.RowHlc = HlcCodec.Max(meta.RowHlc, hlc);
            List<TableColumn> columns = [];
            List<SqlValue> values = [];
            foreach (var change in mutation.Changes)
            {
                if (schema.FindColumn(change.Column) is not { IsPrimaryKey: false, Generated: false } column)
                {
                    continue;
                }

                var state = next.Column(column.Index);
                if (change.Epoch < state.Epoch)
                {
                    continue;
                }

                var existing = Existing(schema, current, column);
                switch (change.Kind)
                {
                    case ChangeKind.Assign or ChangeKind.AbsoluteReset:
                        if (change.Epoch > state.Epoch)
                        {
                            next.Columns[column.Index] = new ColumnMeta(hlc, change.Epoch, 0);
                            columns.Add(column);
                            values.Add(change.Value);
                        }
                        else if (hlc > state.Hlc)
                        {
                            next.Columns[column.Index] = state with { Hlc = hlc };
                            columns.Add(column);
                            values.Add(Add(change.Value, state.DeltaSum));
                        }

                        break;
                    case ChangeKind.DeltaOp:
                        next.Columns[column.Index] = change.Epoch > state.Epoch
                            ? new ColumnMeta(HlcTimestamp.Zero, change.Epoch, change.Delta)
                            : state with { DeltaSum = state.DeltaSum + change.Delta };
                        columns.Add(column);
                        values.Add(Add(existing, change.Delta));
                        break;
                    default:
                        throw new UnreachableException();
                }
            }

            Write(scope, sql, keyValues, current is not null, columns, values, orIgnore: true);
        }

        MetaStore.Save(scope, schema.Table, key, next);
        MetaStore.Raise(tables, schema.Table, hlc);
    }

    private static void Write(WriteScope scope, TableSql sql, SqlValue[] key, bool exists, List<TableColumn> columns, List<SqlValue> values, bool orIgnore)
    {
        if (exists && columns.Count == 0)
        {
            return;
        }

        SqlValue[] parameters = [.. key, .. values];
        scope.Run(exists ? sql.Update(columns) : sql.Insert(columns, orIgnore), parameters);
    }

    private static SqlValue Existing(TableSchema schema, SqlValue[]? current, TableColumn column)
    {
        if (current is null)
        {
            return SqlValue.Null;
        }

        var ordinal = schema.DataOrdinal(column);
        return ordinal >= 0 && ordinal < current.Length ? current[ordinal] : SqlValue.Null;
    }

    private static SqlValue Add(SqlValue value, long delta)
    {
        if (delta == 0)
        {
            return value;
        }

        if (value.TryGetIntegral(out var integral))
        {
            return SqlValue.FromIntegral(integral + delta);
        }

        if (value.TryGetReal(out var real))
        {
            return SqlValue.FromReal(real + delta);
        }

        return SqlValue.FromIntegral(delta);
    }
}

using System.Diagnostics;

namespace TerraBase.Storage;

internal readonly record struct ObservedRow(RowKey Key, ulong CausalLength, HlcTimestamp RowHlc);

internal readonly record struct MetaCacheKey(string Table, byte[] PrimaryKey);

internal sealed class MetaCacheKeyComparer : IEqualityComparer<MetaCacheKey>
{
    public static MetaCacheKeyComparer Instance { get; } = new();

    private MetaCacheKeyComparer()
    {
    }

    public bool Equals(MetaCacheKey x, MetaCacheKey y) =>
        string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase)
        && x.PrimaryKey.AsSpan().SequenceEqual(y.PrimaryKey);

    public int GetHashCode(MetaCacheKey obj)
    {
        var hash = new HashCode();
        hash.Add(obj.Table, StringComparer.OrdinalIgnoreCase);
        hash.AddBytes(obj.PrimaryKey);
        return hash.ToHashCode();
    }
}

internal sealed class MetaCache
{
    public const int MaxEntries = 8192;

    private readonly Dictionary<MetaCacheKey, int> _index = new(MetaCacheKeyComparer.Instance);
    private readonly HashSet<string> _checkedTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completeTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HlcTimestamp> _knownTableHlcs = new(StringComparer.OrdinalIgnoreCase);
    private Entry[] _entries = [];
    private int _count;
    private int _head = -1;
    private int _tail = -1;

    public bool TryGet(string table, byte[] primaryKey, out RowMeta? meta)
    {
        if (_index.TryGetValue(new MetaCacheKey(table, primaryKey), out var slot))
        {
            MoveToFront(slot);
            meta = _entries[slot].Meta;
            return true;
        }

        meta = null;
        return false;
    }

    public void Set(string table, byte[] primaryKey, RowMeta meta)
    {
        var key = new MetaCacheKey(table, primaryKey);
        if (_index.TryGetValue(key, out var existing))
        {
            _entries[existing].Meta = meta;
            MoveToFront(existing);
            return;
        }

        if (_count < MaxEntries)
        {
            if (_count == _entries.Length)
            {
                var nextLength = _entries.Length == 0 ? 16 : Math.Min(_entries.Length * 4, MaxEntries);
                Array.Resize(ref _entries, nextLength);
            }

            var slot = _count++;
            _entries[slot] = new Entry(table, primaryKey, meta, -1, _head);
            if (_head >= 0)
            {
                _entries[_head].Prev = slot;
            }

            _head = slot;
            if (_tail < 0)
            {
                _tail = slot;
            }

            _index[key] = slot;
            return;
        }

        var evictedSlot = _tail;
        ref var evicted = ref _entries[evictedSlot];
        _ = _index.Remove(new MetaCacheKey(evicted.Table, evicted.PrimaryKey));
        _ = _completeTables.Remove(evicted.Table);
        evicted.Table = table;
        evicted.PrimaryKey = primaryKey;
        evicted.Meta = meta;
        _index[key] = evictedSlot;
        MoveToFront(evictedSlot);
    }

    public bool IsCompleteTable(string table) => _completeTables.Contains(table);

    public bool HasCheckedTable(string table) => _checkedTables.Contains(table);

    public void MarkTableChecked(string table, bool isEmpty)
    {
        _ = _checkedTables.Add(table);
        if (isEmpty)
        {
            _ = _completeTables.Add(table);
        }
    }

    public bool TryGetTableHlc(string table, out HlcTimestamp hlc) => _knownTableHlcs.TryGetValue(table, out hlc);

    public void SetTableHlc(string table, HlcTimestamp hlc) => _knownTableHlcs[table] = hlc;

    public void Clear()
    {
        _index.Clear();
        if (_count > 0)
        {
            Array.Clear(_entries, 0, _count);
            _count = 0;
        }

        _head = -1;
        _tail = -1;
        _checkedTables.Clear();
        _completeTables.Clear();
        _knownTableHlcs.Clear();
    }

    private void MoveToFront(int slot)
    {
        if (slot == _head)
        {
            return;
        }

        var prev = _entries[slot].Prev;
        var next = _entries[slot].Next;
        if (prev >= 0)
        {
            _entries[prev].Next = next;
        }

        if (next >= 0)
        {
            _entries[next].Prev = prev;
        }
        else
        {
            _tail = prev;
        }

        _entries[slot].Prev = -1;
        _entries[slot].Next = _head;
        if (_head >= 0)
        {
            _entries[_head].Prev = slot;
        }

        _head = slot;
        if (_tail < 0)
        {
            _tail = slot;
        }
    }

    private struct Entry(string table, byte[] primaryKey, RowMeta meta, int prev, int next)
    {
        public string Table = table;
        public byte[] PrimaryKey = primaryKey;
        public RowMeta Meta = meta;
        public int Prev = prev;
        public int Next = next;
    }
}

internal static class MetaStore
{
    public const string SchemaSql =
        "CREATE TABLE IF NOT EXISTS _tb_meta (tbl TEXT NOT NULL, pk BLOB NOT NULL, meta BLOB NOT NULL, PRIMARY KEY (tbl, pk)) WITHOUT ROWID;"
        + "CREATE TABLE IF NOT EXISTS _tb_applied (tx_id BLOB PRIMARY KEY, applied_at INTEGER NOT NULL) WITHOUT ROWID;"
        + "CREATE TABLE IF NOT EXISTS _tb_vv (origin TEXT PRIMARY KEY, seq INTEGER NOT NULL, schema_epoch INTEGER NOT NULL) WITHOUT ROWID;";

    public const string SelectMetaSql = "SELECT meta FROM _tb_meta WHERE tbl = ?1 AND pk = ?2";

    public const string SelectClockWatermarkSql =
        "WITH RECURSIVE tables(name) AS (SELECT min(tbl) FROM _tb_meta UNION ALL SELECT (SELECT min(tbl) FROM _tb_meta WHERE tbl > tables.name) FROM tables WHERE tables.name IS NOT NULL) "
        + "SELECT max(meta.meta) FROM tables JOIN _tb_meta AS meta ON meta.tbl = tables.name AND meta.pk = X'' WHERE length(meta.meta) = 10 AND meta.meta <= ?1";

    public const string SelectTableHlcSql = "SELECT meta FROM _tb_meta WHERE tbl = ?1 AND pk = X''";

    public const string SelectVersionsSql = "SELECT origin, seq, schema_epoch FROM _tb_vv";

    public const string SelectAppliedSql = "SELECT 1 FROM _tb_applied WHERE tx_id = ?1";

    private const string SelectAnyRowMetaSql = "SELECT 1 FROM _tb_meta WHERE tbl = ?1 AND pk > X'' LIMIT 1";

    private const string UpsertMetaSql =
        "INSERT INTO _tb_meta (tbl, pk, meta) VALUES (?1, ?2, ?3) ON CONFLICT (tbl, pk) DO UPDATE SET meta = excluded.meta";

    private const string TouchTableSql =
        "INSERT INTO _tb_meta (tbl, pk, meta) VALUES (?1, X'', ?2) ON CONFLICT (tbl, pk) DO UPDATE SET meta = excluded.meta WHERE excluded.meta > _tb_meta.meta";

    private const string UpdateTableHlcSql =
        "UPDATE _tb_meta SET meta = ?2 WHERE tbl = ?1 AND pk = X'' AND meta < ?2";

    private const string UpsertVersionSql =
        "INSERT INTO _tb_vv (origin, seq, schema_epoch) VALUES (?1, ?2, ?3) ON CONFLICT (origin) DO UPDATE SET seq = excluded.seq, schema_epoch = excluded.schema_epoch";

    private const string InsertAppliedSql = "INSERT OR IGNORE INTO _tb_applied (tx_id, applied_at) VALUES (?1, ?2)";

    private const string PruneAppliedSql = "DELETE FROM _tb_applied WHERE tx_id IN (SELECT tx_id FROM _tb_applied WHERE tx_id < ?1 LIMIT 64)";

    private static readonly TimeSpan AppliedRetention = TimeSpan.FromDays(7);

    private static readonly byte[] FloorRandomness = new byte[10];

    public static RowMeta Load(WriteScope scope, string table, byte[] key)
    {
        var cache = scope.MetaCache;
        if (cache is not null)
        {
            if (cache.TryGet(table, key, out var cached) && cached is not null)
            {
                return cached;
            }

            if (cache.IsCompleteTable(table))
            {
                return new RowMeta();
            }

            if (!cache.HasCheckedTable(table))
            {
                var probe = scope.First(SelectAnyRowMetaSql, SqlValue.FromText(table));
                if (scope.Failed)
                {
                    return new RowMeta();
                }

                cache.MarkTableChecked(table, isEmpty: probe is null);
                if (probe is null)
                {
                    return new RowMeta();
                }
            }
        }

        var row = scope.First(SelectMetaSql, SqlValue.FromText(table), SqlValue.FromBlob(key));
        if (row is null || !row[0].TryGetBlob(out var blob))
        {
            return new RowMeta();
        }

        if (RowMeta.Decode(blob.Span) is { } meta)
        {
            cache?.Set(table, key, meta);
            return meta;
        }

        scope.Fail(new SqliteStatus(SqliteNative.Corrupt, string.Concat("corrupt _tb_meta row for table ", table)));
        return new RowMeta();
    }

    public static void Save(WriteScope scope, string table, byte[] key, RowMeta meta)
    {
        var buffer = scope.GetMetaScratch(meta.MaxEncodedSize);
        var written = meta.EncodeTo(buffer);
        scope.Run(UpsertMetaSql, SqlValue.FromText(table), SqlValue.FromBlob(key), SqlValue.FromBlob(buffer.AsMemory(0, written)));
        if (!scope.Failed)
        {
            scope.MetaCache?.Set(table, key, meta);
        }
    }

    public static void TouchTables(WriteScope scope, IReadOnlyDictionary<string, HlcTimestamp> tables)
    {
        var encoded = scope.HlcScratch;
        var cache = scope.MetaCache;
        foreach (var (table, hlc) in tables)
        {
            if (cache is not null && cache.TryGetTableHlc(table, out var known))
            {
                if (hlc <= known)
                {
                    continue;
                }

                HlcCodec.Write(encoded, hlc);
                scope.Run(UpdateTableHlcSql, SqlValue.FromText(table), SqlValue.FromBlob(encoded));
                if (!scope.Failed)
                {
                    cache.SetTableHlc(table, hlc);
                }

                continue;
            }

            HlcCodec.Write(encoded, hlc);
            scope.Run(TouchTableSql, SqlValue.FromText(table), SqlValue.FromBlob(encoded));
            if (!scope.Failed)
            {
                cache?.SetTableHlc(table, hlc);
            }
        }
    }

    public static void SaveVersion(WriteScope scope, string origin, long sequence, ulong schemaEpoch) =>
        scope.Run(UpsertVersionSql, SqlValue.FromText(origin), SqlValue.FromIntegral(sequence), SqlValue.FromIntegral((long)schemaEpoch));

    public static void MarkApplied(WriteScope scope, Ulid txId, long appliedAtMs)
    {
        var bytes = scope.UlidScratch;
        _ = txId.TryWriteBytes(bytes);
        scope.Run(InsertAppliedSql, SqlValue.FromBlob(bytes), SqlValue.FromIntegral(appliedAtMs));
    }

    public static void PruneApplied(WriteScope scope, DateTimeOffset now)
    {
        if (now - DateTimeOffset.UnixEpoch > AppliedRetention)
        {
            var bytes = scope.UlidScratch;
            _ = Ulid.NewUlid(now - AppliedRetention, FloorRandomness).TryWriteBytes(bytes);
            scope.Run(PruneAppliedSql, SqlValue.FromBlob(bytes));
        }
    }

    public static bool IsApplied(WriteScope scope, Ulid txId)
    {
        var bytes = scope.UlidScratch;
        _ = txId.TryWriteBytes(bytes);
        return scope.First(SelectAppliedSql, SqlValue.FromBlob(bytes)) is not null;
    }

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
        ref HlcTimestamp hlc,
        bool persist,
        List<ObservedRow>? observed,
        Dictionary<string, HlcTimestamp> tables)
    {
        var metas = new RowMeta[changes.Count];
        var maxExisting = HlcTimestamp.Zero;
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            var meta = MetaStore.Load(scope, change.Schema.Table, change.EncodedKey);
            metas[index] = meta;
            maxExisting = HlcCodec.Max(maxExisting, meta.RowHlc);
            foreach (var column in meta.Columns.Values)
            {
                maxExisting = HlcCodec.Max(maxExisting, column.Hlc);
            }
        }

        if (maxExisting >= hlc)
        {
            hlc = AdvancePast(hlc, maxExisting);
        }

        var mutations = new List<RowMutation>(changes.Count);
        for (var changeIndex = 0; changeIndex < changes.Count; changeIndex++)
        {
            var change = changes[changeIndex];
            var schema = change.Schema;
            var meta = metas[changeIndex];
            observed?.Add(new ObservedRow(new RowKey(schema.Table, PrimaryKeyCodec.ToKeyString(change.EncodedKey)), meta.CausalLength, meta.RowHlc));
            RowMeta next;
            IReadOnlyList<ColumnChange> columnChanges;
            switch (change.Op)
            {
                case MutationOp.Insert:
                    next = new RowMeta(schema.DataColumns.Count)
                    {
                        CausalLength = meta.CausalLength == 0 ? 1 : meta.Alive ? meta.CausalLength + 2 : meta.CausalLength + 1,
                        RowHlc = hlc,
                    };
                    var insertedColumns = schema.DataColumns.Count == 0 ? [] : new ColumnChange[schema.DataColumns.Count];
                    for (var index = 0; index < schema.DataColumns.Count; index++)
                    {
                        var column = schema.DataColumns[index];
                        insertedColumns[index] = new ColumnChange(column.Name, ChangeKind.Assign, 0, Value(change.Final, index), 0);
                        next.Columns[column.Index] = new ColumnMeta(hlc, 0, 0);
                    }

                    columnChanges = insertedColumns;
                    break;
                case MutationOp.Update:
                    next = new RowMeta(meta.Columns.Count)
                    {
                        CausalLength = meta.Alive ? meta.CausalLength : meta.CausalLength + 1,
                        RowHlc = hlc,
                    };
                    foreach (var (ordinal, column) in meta.Columns)
                    {
                        next.Columns[ordinal] = column;
                    }

                    var updatedColumns = new ColumnChange[change.ChangedColumns.Count];
                    for (var changedIndex = 0; changedIndex < change.ChangedColumns.Count; changedIndex++)
                    {
                        var index = change.ChangedColumns[changedIndex];
                        var column = schema.DataColumns[index];
                        var current = next.Column(column.Index);
                        var final = Value(change.Final, index);
                        if (!column.IsDeltaCounter)
                        {
                            updatedColumns[changedIndex] = new ColumnChange(column.Name, ChangeKind.Assign, current.Epoch, final, 0);
                            next.Columns[column.Index] = current with { Hlc = hlc };
                            continue;
                        }

                        var hasBefore = Value(change.Original, index).TryGetIntegral(out var before);
                        var hasAfter = final.TryGetIntegral(out var after);
                        if (hasBefore && hasAfter && !change.Replaced && !change.AbsoluteColumns.Contains(column.Name)
                            && DeltaMath.TryDifference(after, before, out var delta))
                        {
                            updatedColumns[changedIndex] = new ColumnChange(column.Name, ChangeKind.DeltaOp, current.Epoch, SqlValue.Null, delta);
                            next.Columns[column.Index] = current with { DeltaSum = DeltaMath.Sum(current.DeltaSum, delta) };
                        }
                        else
                        {
                            updatedColumns[changedIndex] = new ColumnChange(column.Name, ChangeKind.AbsoluteReset, current.Epoch + 1, final, 0);
                            next.Columns[column.Index] = new ColumnMeta(hlc, current.Epoch + 1, 0);
                        }
                    }

                    columnChanges = updatedColumns;
                    break;
                case MutationOp.Delete:
                    next = new RowMeta
                    {
                        CausalLength = meta.Alive ? meta.CausalLength + 1 : meta.CausalLength + 2,
                        RowHlc = hlc,
                    };
                    columnChanges = [];
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

    private static HlcTimestamp AdvancePast(HlcTimestamp current, HlcTimestamp existing)
    {
        if (current > existing)
        {
            return current;
        }

        if (current.NodeId > existing.NodeId)
        {
            return new HlcTimestamp(existing.WallMs, existing.Logical, current.NodeId);
        }

        return existing.Logical < ushort.MaxValue
            ? new HlcTimestamp(existing.WallMs, (ushort)(existing.Logical + 1), current.NodeId)
            : existing.WallMs < HlcTimestamp.MaxWallMs
                ? new HlcTimestamp(existing.WallMs + 1, 0, current.NodeId)
                : new HlcTimestamp(HlcTimestamp.MaxWallMs, ushort.MaxValue, ushort.MaxValue);
    }
}

internal static class CrdtApplier
{
    private static TableSchema? Unresolved(WriteScope scope, SqliteStatus failure)
    {
        scope.Fail(failure);
        return null;
    }

    public static void Apply(WriteScope scope, RowMutation mutation, HlcTimestamp hlc, Dictionary<string, HlcTimestamp> tables) =>
        Apply(scope, mutation, hlc, hlc, tables);

    public static void Apply(WriteScope scope, RowMutation mutation, HlcTimestamp hlc, HlcTimestamp tableHlc, Dictionary<string, HlcTimestamp> tables)
    {
        var lookup = scope.Connection.Schema(mutation.Table);
        var resolved = lookup switch
        {
            TableSchema found => found,
            TableNotFound => null,
            SqliteStatus failure => Unresolved(scope, failure),
        };
        if (resolved is not { } schema || mutation.PrimaryKey.Count != schema.PrimaryKeyColumnNames.Count)
        {
            return;
        }

        var keyValues = mutation.PrimaryKey as SqlValue[] ?? [.. mutation.PrimaryKey];
        var key = PrimaryKeyCodec.Encode(keyValues);
        var meta = MetaStore.Load(scope, schema.Table, key);
        if (scope.Failed || mutation.CausalLength < meta.CausalLength)
        {
            return;
        }

        var sql = schema.Sql;
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
        var columns = scope.ApplyColumns;
        var values = scope.ApplyValues;
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
            next = new RowMeta(mutation.Changes.Count) { CausalLength = mutation.CausalLength, RowHlc = hlc };
            columns.Clear();
            values.Clear();
            for (var index = 0; index < mutation.Changes.Count; index++)
            {
                var change = mutation.Changes[index];
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

            Write(scope, sql, keyValues, current is not null, columns, values);
            columns.Clear();
            values.Clear();
        }
        else
        {
            next = meta;
            next.RowHlc = HlcCodec.Max(meta.RowHlc, hlc);
            columns.Clear();
            values.Clear();
            for (var index = 0; index < mutation.Changes.Count; index++)
            {
                var change = mutation.Changes[index];
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
                            : state with { DeltaSum = DeltaMath.Sum(state.DeltaSum, change.Delta) };
                        columns.Add(column);
                        values.Add(Add(existing, change.Delta));
                        break;
                    default:
                        throw new UnreachableException();
                }
            }

            Write(scope, sql, keyValues, current is not null, columns, values);
            columns.Clear();
            values.Clear();
        }

        MetaStore.Save(scope, schema.Table, key, next);
        MetaStore.Raise(tables, schema.Table, tableHlc);
    }

    private static void Write(WriteScope scope, TableSql sql, SqlValue[] key, bool exists, List<TableColumn> columns, List<SqlValue> values)
    {
        if (exists && columns.Count == 0)
        {
            return;
        }

        var total = key.Length + values.Count;
        var parameters = scope.GetParameterScratch(total);
        key.AsSpan().CopyTo(parameters);
        for (var index = 0; index < values.Count; index++)
        {
            parameters[key.Length + index] = values[index];
        }

        scope.Run(exists ? sql.Update(columns) : sql.Insert(columns), parameters.AsSpan(0, total));
        parameters.AsSpan(0, total).Clear();
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
            return DeltaMath.TryDifference(integral, -(Int128)delta, out var sum)
                ? SqlValue.FromIntegral(sum)
                : SqlValue.FromReal((double)((Int128)integral + delta));
        }

        if (value.TryGetReal(out var real))
        {
            return SqlValue.FromReal(real + delta);
        }

        return SqlValue.FromIntegral(delta);
    }
}

internal static class DeltaMath
{
    public static bool TryDifference(Int128 after, Int128 before, out long delta)
    {
        var exact = after - before;
        var fits = exact >= long.MinValue && exact <= long.MaxValue;
        delta = fits ? (long)exact : 0;
        return fits;
    }

    public static long Sum(long left, long right) => long.CreateSaturating((Int128)left + right);
}

using System.Diagnostics;

namespace TerraBase.Storage;

internal readonly record struct ObservedRow(RowKey Key, ulong CausalLength, HlcTimestamp RowHlc);

internal readonly record struct MetaCacheKey(string Table, byte[] PrimaryKey);

internal readonly ref struct MetaCacheKeyRef(string table, ReadOnlySpan<byte> primaryKey)
{
    public string Table { get; } = table;

    public ReadOnlySpan<byte> PrimaryKey { get; } = primaryKey;
}

internal sealed class MetaCacheKeyComparer : IEqualityComparer<MetaCacheKey>, IAlternateEqualityComparer<MetaCacheKeyRef, MetaCacheKey>
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

    public bool Equals(MetaCacheKeyRef alternate, MetaCacheKey other) =>
        string.Equals(alternate.Table, other.Table, StringComparison.OrdinalIgnoreCase)
        && alternate.PrimaryKey.SequenceEqual(other.PrimaryKey);

    public int GetHashCode(MetaCacheKeyRef alternate)
    {
        var hash = new HashCode();
        hash.Add(alternate.Table, StringComparer.OrdinalIgnoreCase);
        hash.AddBytes(alternate.PrimaryKey);
        return hash.ToHashCode();
    }

    public MetaCacheKey Create(MetaCacheKeyRef alternate) => new(alternate.Table, alternate.PrimaryKey.ToArray());
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

    public bool TryGet(string table, ReadOnlySpan<byte> primaryKey, out RowMeta? meta)
    {
        var lookup = _index.GetAlternateLookup<MetaCacheKeyRef>();
        if (lookup.TryGetValue(new MetaCacheKeyRef(table, primaryKey), out var slot))
        {
            MoveToFront(slot);
            meta = _entries[slot].Meta;
            return true;
        }

        meta = null;
        return false;
    }

    public void Set(string table, ReadOnlySpan<byte> primaryKey, RowMeta meta, byte[]? ownedKey = null)
    {
        var lookup = _index.GetAlternateLookup<MetaCacheKeyRef>();
        if (lookup.TryGetValue(new MetaCacheKeyRef(table, primaryKey), out var existing))
        {
            _entries[existing].Meta = meta;
            MoveToFront(existing);
            return;
        }

        var keyBytes = ownedKey ?? primaryKey.ToArray();
        var key = new MetaCacheKey(table, keyBytes);
        if (_count < MaxEntries)
        {
            if (_count == _entries.Length)
            {
                var nextLength = _entries.Length == 0 ? 16 : Math.Min(_entries.Length * 4, MaxEntries);
                Array.Resize(ref _entries, nextLength);
            }

            var slot = _count++;
            _entries[slot] = new Entry(table, keyBytes, meta, -1, _head);
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
        evicted.PrimaryKey = keyBytes;
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

    public void SetTableHlc(string table, HlcTimestamp hlc)
    {
        if (hlc > HlcTimestamp.Zero)
        {
            _knownTableHlcs[table] = hlc;
        }
    }

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

    private const string PruneAppliedSql = "DELETE FROM _tb_applied WHERE tx_id IN (SELECT tx_id FROM _tb_applied WHERE tx_id < ?1 LIMIT 64)";

    private static readonly TimeSpan AppliedRetention = TimeSpan.FromDays(7);

    private static readonly byte[] FloorRandomness = new byte[10];

    public static RowMeta Load(WriteScope scope, TableSchema schema, ReadOnlySpan<byte> key, byte[]? ownedKey = null)
    {
        if (scope.Failed)
        {
            return RowMeta.Empty;
        }

        var table = schema.Table;
        var cache = scope.MetaCache;
        if (cache is not null)
        {
            if (cache.TryGet(table, key, out var cached) && cached is not null)
            {
                return cached;
            }

            if (cache.IsCompleteTable(table))
            {
                return RowMeta.Empty;
            }

            if (!cache.HasCheckedTable(table))
            {
                var probeStatus = scope.Connection.MetaHasAnyRow(schema.TableUtf8, out var hasAny);
                if (!probeStatus.IsOk)
                {
                    scope.Fail(probeStatus);
                    return RowMeta.Empty;
                }

                cache.MarkTableChecked(table, isEmpty: !hasAny);
                if (!hasAny)
                {
                    return RowMeta.Empty;
                }
            }
        }

        var selectStatus = scope.Connection.MetaSelectRow(schema.TableUtf8, key, out var meta, out var found);
        if (!selectStatus.IsOk)
        {
            scope.Fail(selectStatus);
            return RowMeta.Empty;
        }

        if (!found)
        {
            return RowMeta.Empty;
        }

        if (meta is not null)
        {
            cache?.Set(table, key, meta, ownedKey);
            return meta;
        }

        scope.Fail(new SqliteStatus(SqliteNative.Corrupt, string.Concat("corrupt _tb_meta row for table ", table)));
        return RowMeta.Empty;
    }

    public static RowMeta Load(WriteScope scope, string table, ReadOnlySpan<byte> key, byte[]? ownedKey = null)
    {
        if (scope.Failed)
        {
            return RowMeta.Empty;
        }

        var cache = scope.MetaCache;
        if (cache is not null)
        {
            if (cache.TryGet(table, key, out var cached) && cached is not null)
            {
                return cached;
            }

            if (cache.IsCompleteTable(table))
            {
                return RowMeta.Empty;
            }

            if (!cache.HasCheckedTable(table))
            {
                var probeStatus = scope.Connection.MetaHasAnyRow(table, out var hasAny);
                if (!probeStatus.IsOk)
                {
                    scope.Fail(probeStatus);
                    return RowMeta.Empty;
                }

                cache.MarkTableChecked(table, isEmpty: !hasAny);
                if (!hasAny)
                {
                    return RowMeta.Empty;
                }
            }
        }

        var selectStatus = scope.Connection.MetaSelectRow(table, key, out var meta, out var found);
        if (!selectStatus.IsOk)
        {
            scope.Fail(selectStatus);
            return RowMeta.Empty;
        }

        if (!found)
        {
            return RowMeta.Empty;
        }

        if (meta is not null)
        {
            cache?.Set(table, key, meta, ownedKey);
            return meta;
        }

        scope.Fail(new SqliteStatus(SqliteNative.Corrupt, string.Concat("corrupt _tb_meta row for table ", table)));
        return RowMeta.Empty;
    }

    public static void Save(WriteScope scope, TableSchema schema, ReadOnlySpan<byte> key, RowMeta meta, byte[]? ownedKey = null)
    {
        if (scope.Failed)
        {
            return;
        }

        var buffer = scope.GetMetaScratch(meta.MaxEncodedSize);
        var written = meta.EncodeTo(buffer);
        var status = scope.Connection.MetaUpsertRow(schema.TableUtf8, key, buffer.AsSpan(0, written));
        if (!status.IsOk)
        {
            scope.Fail(status);
            return;
        }

        scope.MetaCache?.Set(schema.Table, key, meta, ownedKey);
    }

    public static HlcTimestamp LoadTableHlc(WriteScope scope, string table)
    {
        if (scope.Failed)
        {
            return HlcTimestamp.Zero;
        }

        if (scope.MetaCache is { } cache && cache.TryGetTableHlc(table, out var cached))
        {
            return cached;
        }

        var status = scope.Connection.MetaSelectTableHlc(table, out var hlc);
        if (!status.IsOk)
        {
            scope.Fail(status);
            return HlcTimestamp.Zero;
        }

        if (hlc > HlcTimestamp.Zero)
        {
            scope.MetaCache?.SetTableHlc(table, hlc);
        }

        return hlc;
    }

    public static void TouchTables(WriteScope scope, IReadOnlyDictionary<string, HlcTimestamp> tables)
    {
        if (scope.Failed)
        {
            return;
        }

        var encoded = scope.HlcScratch;
        var cache = scope.MetaCache;
        foreach (var (table, hlc) in tables)
        {
            if (cache is not null && cache.TryGetTableHlc(table, out var known) && known > HlcTimestamp.Zero)
            {
                if (hlc <= known)
                {
                    continue;
                }

                HlcCodec.Write(encoded, hlc);
                var updated = scope.Connection.MetaUpdateTableHlc(table, encoded);
                if (!updated.IsOk)
                {
                    scope.Fail(updated);
                    return;
                }

                cache.SetTableHlc(table, hlc);
                continue;
            }

            HlcCodec.Write(encoded, hlc);
            var touched = scope.Connection.MetaTouchTable(table, encoded);
            if (!touched.IsOk)
            {
                scope.Fail(touched);
                return;
            }

            cache?.SetTableHlc(table, hlc);
        }
    }

    public static void SaveVersion(WriteScope scope, string origin, long sequence, ulong schemaEpoch)
    {
        if (!scope.Failed)
        {
            scope.Fail(scope.Connection.MetaUpsertVersion(origin, sequence, schemaEpoch));
        }
    }

    public static void MarkApplied(WriteScope scope, Ulid txId, long appliedAtMs)
    {
        if (scope.Failed)
        {
            return;
        }

        var bytes = scope.UlidScratch;
        _ = txId.TryWriteBytes(bytes);
        scope.Fail(scope.Connection.MetaInsertApplied(bytes, appliedAtMs, out _));
    }

    public static bool TryMarkApplied(WriteScope scope, Ulid txId, long appliedAtMs)
    {
        if (scope.Failed)
        {
            return false;
        }

        var bytes = scope.UlidScratch;
        _ = txId.TryWriteBytes(bytes);
        var status = scope.Connection.MetaInsertApplied(bytes, appliedAtMs, out var changes);
        scope.Fail(status);
        return status.IsOk && changes > 0;
    }

    public static long PruneApplied(WriteScope scope, DateTimeOffset now)
    {
        if (now - DateTimeOffset.UnixEpoch > AppliedRetention)
        {
            var bytes = scope.UlidScratch;
            _ = Ulid.NewUlid(now - AppliedRetention, FloorRandomness).TryWriteBytes(bytes);
            return scope.RunChanges(PruneAppliedSql, SqlValue.FromBlob(bytes));
        }

        return 0;
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
    public static byte[] StampAndEncode(
        WriteScope scope,
        IReadOnlyList<NetChange> changes,
        IReadOnlyList<string> ddl,
        ref HlcTimestamp hlc,
        Dictionary<string, HlcTimestamp> tables)
    {
        var count = changes.Count;
        if (count == 0)
        {
            if (ddl.Count == 0)
            {
                return [];
            }

            using var ddlEncoder = MutationBatchCodec.CreateDirectEncoder(0);
            return ddlEncoder.Finish(ddl);
        }

        RowMeta? singleMeta = null;
        RowMeta[]? metas = null;
        var maxExisting = HlcTimestamp.Zero;
        if (count == 1)
        {
            var change = changes[0];
            singleMeta = MetaStore.Load(scope, change.Schema, change.EncodedKey, change.EncodedKey);
            if (!ReferenceEquals(singleMeta, RowMeta.Empty))
            {
                maxExisting = singleMeta.MaxHlc();
            }
        }
        else
        {
            metas = new RowMeta[count];
            for (var index = 0; index < count; index++)
            {
                var change = changes[index];
                var meta = MetaStore.Load(scope, change.Schema, change.EncodedKey, change.EncodedKey);
                metas[index] = meta;
                if (!ReferenceEquals(meta, RowMeta.Empty))
                {
                    maxExisting = HlcCodec.Max(maxExisting, meta.MaxHlc());
                }
            }
        }

        if (maxExisting >= hlc)
        {
            hlc = AdvancePast(hlc, maxExisting);
        }

        using var encoder = MutationBatchCodec.CreateDirectEncoder(count);
        string? lastRaisedTable = null;
        for (var changeIndex = 0; changeIndex < count; changeIndex++)
        {
            var change = changes[changeIndex];
            var schema = change.Schema;
            var meta = metas is not null ? metas[changeIndex] : singleMeta ?? RowMeta.Empty;
            RowMeta next;
            switch (change.Op)
            {
                case MutationOp.Insert:
                    var dataCount = schema.DataColumns.Count;
                    next = new RowMeta(dataCount)
                    {
                        CausalLength = meta.CausalLength == 0 ? 1 : meta.Alive ? meta.CausalLength + 2 : meta.CausalLength + 1,
                        RowHlc = hlc,
                    };
                    encoder.BeginMutation(schema.Table, MutationOp.Insert, next.CausalLength, change.PrimaryKey, dataCount);
                    for (var index = 0; index < dataCount; index++)
                    {
                        var column = schema.DataColumns[index];
                        encoder.WriteAssign(column.Name, 0, Value(change.Final, index));
                        next.SetColumn(column.Index, new ColumnMeta(hlc, 0, 0));
                    }

                    break;
                case MutationOp.Update:
                    var changedCount = change.ChangedColumns.Count;
                    next = new RowMeta(Math.Max(meta.ColumnCount, changedCount))
                    {
                        CausalLength = meta.Alive ? meta.CausalLength : meta.CausalLength + 1,
                        RowHlc = hlc,
                    };
                    next.CloneColumnsFrom(meta);
                    encoder.BeginMutation(schema.Table, MutationOp.Update, next.CausalLength, change.PrimaryKey, changedCount);
                    for (var changedIndex = 0; changedIndex < changedCount; changedIndex++)
                    {
                        var index = change.ChangedColumns[changedIndex];
                        var column = schema.DataColumns[index];
                        var current = next.Column(column.Index);
                        var final = Value(change.Final, index);
                        if (!column.IsDeltaCounter)
                        {
                            encoder.WriteAssign(column.Name, current.Epoch, final);
                            next.SetColumn(column.Index, current with { Hlc = hlc });
                            continue;
                        }

                        var hasBefore = Value(change.Original, index).TryGetIntegral(out var before);
                        var hasAfter = final.TryGetIntegral(out var after);
                        if (hasBefore && hasAfter && !change.Replaced && !change.AbsoluteColumns.Contains(column.Name)
                            && DeltaMath.TryDifference(after, before, out var delta))
                        {
                            encoder.WriteDelta(column.Name, current.Epoch, delta);
                            next.SetColumn(column.Index, current with { DeltaSum = DeltaMath.Sum(current.DeltaSum, delta) });
                        }
                        else
                        {
                            encoder.WriteAbsoluteReset(column.Name, current.Epoch + 1, final);
                            next.SetColumn(column.Index, new ColumnMeta(hlc, current.Epoch + 1, 0));
                        }
                    }

                    break;
                case MutationOp.Delete:
                    next = new RowMeta
                    {
                        CausalLength = meta.Alive ? meta.CausalLength + 1 : meta.CausalLength + 2,
                        RowHlc = hlc,
                    };
                    encoder.BeginMutation(schema.Table, MutationOp.Delete, next.CausalLength, change.PrimaryKey, 0);
                    break;
                default:
                    throw new UnreachableException();
            }

            MetaStore.Save(scope, schema, change.EncodedKey, next, change.EncodedKey);
            if (!ReferenceEquals(schema.Table, lastRaisedTable))
            {
                MetaStore.Raise(tables, schema.Table, hlc);
                lastRaisedTable = schema.Table;
            }
        }

        return encoder.Finish(ddl);
    }

    public static List<RowMutation> Stamp(
        WriteScope scope,
        IReadOnlyList<NetChange> changes,
        ref HlcTimestamp hlc,
        bool persist,
        List<ObservedRow>? observed,
        Dictionary<string, HlcTimestamp>? tables)
    {
        var count = changes.Count;
        if (count == 0)
        {
            return [];
        }

        RowMeta? singleMeta = null;
        RowMeta[]? metas = null;
        var maxExisting = HlcTimestamp.Zero;
        if (count == 1)
        {
            var change = changes[0];
            singleMeta = MetaStore.Load(scope, change.Schema, change.EncodedKey, change.EncodedKey);
            if (!ReferenceEquals(singleMeta, RowMeta.Empty))
            {
                maxExisting = singleMeta.MaxHlc();
            }
        }
        else
        {
            metas = new RowMeta[count];
            for (var index = 0; index < count; index++)
            {
                var change = changes[index];
                var meta = MetaStore.Load(scope, change.Schema, change.EncodedKey, change.EncodedKey);
                metas[index] = meta;
                if (!ReferenceEquals(meta, RowMeta.Empty))
                {
                    maxExisting = HlcCodec.Max(maxExisting, meta.MaxHlc());
                }
            }
        }

        if (maxExisting >= hlc)
        {
            hlc = AdvancePast(hlc, maxExisting);
        }

        var mutations = new List<RowMutation>(count);
        string? lastRaisedTable = null;
        for (var changeIndex = 0; changeIndex < count; changeIndex++)
        {
            var change = changes[changeIndex];
            var schema = change.Schema;
            var meta = metas is not null ? metas[changeIndex] : singleMeta ?? RowMeta.Empty;
            observed?.Add(new ObservedRow(new RowKey(schema.Table, PrimaryKeyCodec.ToKeyString(change.EncodedKey)), meta.CausalLength, meta.RowHlc));
            RowMeta? next = null;
            ulong causalLength;
            IReadOnlyList<ColumnChange> columnChanges;
            switch (change.Op)
            {
                case MutationOp.Insert:
                    causalLength = meta.CausalLength == 0 ? 1 : meta.Alive ? meta.CausalLength + 2 : meta.CausalLength + 1;
                    if (persist)
                    {
                        next = new RowMeta(schema.DataColumns.Count)
                        {
                            CausalLength = causalLength,
                            RowHlc = hlc,
                        };
                    }

                    var insertedColumns = schema.DataColumns.Count == 0 ? [] : new ColumnChange[schema.DataColumns.Count];
                    for (var index = 0; index < schema.DataColumns.Count; index++)
                    {
                        var column = schema.DataColumns[index];
                        insertedColumns[index] = new ColumnChange(column.Name, ChangeKind.Assign, 0, Value(change.Final, index), 0);
                        next?.SetColumn(column.Index, new ColumnMeta(hlc, 0, 0));
                    }

                    columnChanges = insertedColumns;
                    break;
                case MutationOp.Update:
                    causalLength = meta.Alive ? meta.CausalLength : meta.CausalLength + 1;
                    if (persist)
                    {
                        next = new RowMeta(Math.Max(meta.ColumnCount, change.ChangedColumns.Count))
                        {
                            CausalLength = causalLength,
                            RowHlc = hlc,
                        };
                        next.CloneColumnsFrom(meta);
                    }

                    var updatedColumns = new ColumnChange[change.ChangedColumns.Count];
                    for (var changedIndex = 0; changedIndex < change.ChangedColumns.Count; changedIndex++)
                    {
                        var index = change.ChangedColumns[changedIndex];
                        var column = schema.DataColumns[index];
                        var current = (next ?? meta).Column(column.Index);
                        var final = Value(change.Final, index);
                        if (!column.IsDeltaCounter)
                        {
                            updatedColumns[changedIndex] = new ColumnChange(column.Name, ChangeKind.Assign, current.Epoch, final, 0);
                            next?.SetColumn(column.Index, current with { Hlc = hlc });
                            continue;
                        }

                        var hasBefore = Value(change.Original, index).TryGetIntegral(out var before);
                        var hasAfter = final.TryGetIntegral(out var after);
                        if (hasBefore && hasAfter && !change.Replaced && !change.AbsoluteColumns.Contains(column.Name)
                            && DeltaMath.TryDifference(after, before, out var delta))
                        {
                            updatedColumns[changedIndex] = new ColumnChange(column.Name, ChangeKind.DeltaOp, current.Epoch, SqlValue.Null, delta);
                            next?.SetColumn(column.Index, current with { DeltaSum = DeltaMath.Sum(current.DeltaSum, delta) });
                        }
                        else
                        {
                            updatedColumns[changedIndex] = new ColumnChange(column.Name, ChangeKind.AbsoluteReset, current.Epoch + 1, final, 0);
                            next?.SetColumn(column.Index, new ColumnMeta(hlc, current.Epoch + 1, 0));
                        }
                    }

                    columnChanges = updatedColumns;
                    break;
                case MutationOp.Delete:
                    causalLength = meta.Alive ? meta.CausalLength + 1 : meta.CausalLength + 2;
                    if (persist)
                    {
                        next = new RowMeta
                        {
                            CausalLength = causalLength,
                            RowHlc = hlc,
                        };
                    }

                    columnChanges = [];
                    break;
                default:
                    throw new UnreachableException();
            }

            if (next is not null)
            {
                MetaStore.Save(scope, schema, change.EncodedKey, next, change.EncodedKey);
            }

            if (tables is not null && !ReferenceEquals(schema.Table, lastRaisedTable))
            {
                MetaStore.Raise(tables, schema.Table, hlc);
                lastRaisedTable = schema.Table;
            }

            mutations.Add(new RowMutation(schema.Table, change.Op, causalLength, change.PrimaryKey, columnChanges));
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

    public static void Apply(WriteScope scope, RowMutation mutation, HlcTimestamp hlc, Dictionary<string, HlcTimestamp> tables)
    {
        TableSchema? cached = null;
        Apply(scope, mutation, hlc, hlc, tables, ref cached);
    }

    public static void Apply(WriteScope scope, RowMutation mutation, HlcTimestamp hlc, HlcTimestamp tableHlc, Dictionary<string, HlcTimestamp> tables)
    {
        TableSchema? cached = null;
        Apply(scope, mutation, hlc, tableHlc, tables, ref cached);
    }

    public static void Apply(WriteScope scope, RowMutation mutation, HlcTimestamp hlc, HlcTimestamp tableHlc, Dictionary<string, HlcTimestamp> tables, ref TableSchema? lastSchema)
    {
        TableSchema? resolved;
        if (lastSchema is not null && string.Equals(lastSchema.Table, mutation.Table, StringComparison.OrdinalIgnoreCase))
        {
            resolved = lastSchema;
        }
        else
        {
            var lookup = scope.Connection.Schema(mutation.Table);
            resolved = lookup switch
            {
                TableSchema found => found,
                TableNotFound => null,
                SqliteStatus failure => Unresolved(scope, failure),
            };
            if (resolved is not null)
            {
                lastSchema = resolved;
            }
        }

        if (resolved is not { } schema || mutation.PrimaryKey.Count != schema.PrimaryKeyColumnNames.Count)
        {
            return;
        }

        var keyValues = mutation.PrimaryKey as SqlValue[] ?? [.. mutation.PrimaryKey];
        Span<byte> stackKey = stackalloc byte[128];
        byte[]? heapKey = null;
        var key = PrimaryKeyCodec.TryEncode(keyValues, stackKey, out var keyLength)
            ? (ReadOnlySpan<byte>)stackKey[..keyLength]
            : (heapKey = PrimaryKeyCodec.Encode(keyValues));
        var meta = MetaStore.Load(scope, schema, key, heapKey);
        if (scope.Failed || mutation.CausalLength < meta.CausalLength)
        {
            return;
        }

        var sql = schema.Sql;
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
                scope.Run(sql.DeleteRow, keyValues);
                next = new RowMeta { CausalLength = mutation.CausalLength, RowHlc = hlc };
            }
            else
            {
                next = ReferenceEquals(meta, RowMeta.Empty) ? new RowMeta() : meta;
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
                        next.SetColumn(column.Index, new ColumnMeta(hlc, change.Epoch, 0));
                        break;
                    case ChangeKind.DeltaOp:
                        values.Add(SqlValue.FromIntegral(change.Delta));
                        next.SetColumn(column.Index, new ColumnMeta(HlcTimestamp.Zero, change.Epoch, change.Delta));
                        break;
                    default:
                        throw new UnreachableException();
                }
            }

            if (sql.SupportsUpsert && columns.Count == schema.DataColumns.Count)
            {
                WriteUpsert(scope, sql, keyValues, columns, values);
            }
            else
            {
                var current = scope.First(sql.SelectRow, keyValues);
                if (!scope.Failed)
                {
                    Write(scope, sql, keyValues, current is not null, columns, values);
                }
            }

            columns.Clear();
            values.Clear();
        }
        else
        {
            var hasDeltaOp = false;
            for (var index = 0; index < mutation.Changes.Count; index++)
            {
                if (mutation.Changes[index].Kind == ChangeKind.DeltaOp)
                {
                    hasDeltaOp = true;
                    break;
                }
            }

            SqlValue[]? current = null;
            var needSelect = hasDeltaOp || !meta.Alive || !sql.SupportsUpsert;
            if (needSelect)
            {
                current = scope.First(sql.SelectRow, keyValues);
                if (scope.Failed)
                {
                    return;
                }
            }

            next = ReferenceEquals(meta, RowMeta.Empty) ? new RowMeta() : meta;
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

                switch (change.Kind)
                {
                    case ChangeKind.Assign or ChangeKind.AbsoluteReset:
                        if (change.Epoch > state.Epoch)
                        {
                            next.SetColumn(column.Index, new ColumnMeta(hlc, change.Epoch, 0));
                            columns.Add(column);
                            values.Add(change.Value);
                        }
                        else if (hlc > state.Hlc)
                        {
                            next.SetColumn(column.Index, state with { Hlc = hlc });
                            columns.Add(column);
                            values.Add(Add(change.Value, state.DeltaSum));
                        }

                        break;
                    case ChangeKind.DeltaOp:
                        var existing = Existing(schema, current, column);
                        next.SetColumn(
                            column.Index,
                            change.Epoch > state.Epoch
                                ? new ColumnMeta(HlcTimestamp.Zero, change.Epoch, change.Delta)
                                : state with { DeltaSum = DeltaMath.Sum(state.DeltaSum, change.Delta) });
                        columns.Add(column);
                        values.Add(Add(existing, change.Delta));
                        break;
                    default:
                        throw new UnreachableException();
                }
            }

            if (needSelect)
            {
                Write(scope, sql, keyValues, current is not null, columns, values);
            }
            else if (columns.Count > 0)
            {
                WriteUpdateOrInsert(scope, sql, keyValues, columns, values);
            }

            columns.Clear();
            values.Clear();
        }

        MetaStore.Save(scope, schema, key, next, heapKey);
        MetaStore.Raise(tables, schema.Table, tableHlc);
    }

    private static void WriteUpsert(WriteScope scope, TableSql sql, SqlValue[] key, List<TableColumn> columns, List<SqlValue> values)
    {
        var total = key.Length + values.Count;
        var parameters = scope.GetParameterScratch(total);
        key.AsSpan().CopyTo(parameters);
        for (var index = 0; index < values.Count; index++)
        {
            parameters[key.Length + index] = values[index];
        }

        scope.Run(sql.Upsert(columns), parameters.AsSpan(0, total));
        parameters.AsSpan(0, total).Clear();
    }

    private static void WriteUpdateOrInsert(WriteScope scope, TableSql sql, SqlValue[] key, List<TableColumn> columns, List<SqlValue> values)
    {
        var total = key.Length + values.Count;
        var parameters = scope.GetParameterScratch(total);
        key.AsSpan().CopyTo(parameters);
        for (var index = 0; index < values.Count; index++)
        {
            parameters[key.Length + index] = values[index];
        }

        var span = parameters.AsSpan(0, total);
        if (scope.RunChanges(sql.Update(columns), span) == 0 && !scope.Failed)
        {
            scope.Run(sql.Insert(columns), span);
        }

        span.Clear();
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

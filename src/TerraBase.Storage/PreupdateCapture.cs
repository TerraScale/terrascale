using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TerraBase.Storage;

internal sealed record StatementAnnotation(string Table, IReadOnlySet<string> AbsoluteColumns);

internal sealed record NetChange(
    TableSchema Schema,
    SqlValue[] PrimaryKey,
    byte[] EncodedKey,
    MutationOp Op,
    SqlValue[]? Original,
    SqlValue[]? Final,
    IReadOnlyList<int> ChangedColumns,
    IReadOnlySet<string> AbsoluteColumns,
    bool Replaced);

internal sealed unsafe class PreupdateCapture : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<(string Table, string Key), TouchedRow> _byKey = [];
    private readonly List<TouchedRow> _touched = [];
    private GCHandle<PreupdateCapture> _self;
    private int _writtenRows;
    private int _remainingBytes;
    private bool _active;
    private bool _registered;

    internal PreupdateCapture(SqliteConnection connection)
    {
        _connection = connection;
        _self = new GCHandle<PreupdateCapture>(this);
        _ = SqliteNative.PreupdateHook(connection.Handle, &OnPreupdate, GCHandle<PreupdateCapture>.ToIntPtr(_self));
        _registered = true;
    }

    public StatementAnnotation? Annotation { get; set; }

    public bool Suppressed { get; set; }

    public SqliteStatus Fault { get; private set; } = SqliteStatus.Success;

    public void Begin()
    {
        _touched.Clear();
        _byKey.Clear();
        Fault = SqliteStatus.Success;
        _writtenRows = 0;
        _remainingBytes = SqlLimits.MaxWriteBytes;
        Annotation = null;
        _active = true;
    }

    public void End()
    {
        _touched.Clear();
        _byKey.Clear();
        Annotation = null;
        _active = false;
    }

    public SqliteStatus TakeNetChanges(List<NetChange> changes)
    {
        _active = false;
        Annotation = null;
        if (!Fault.IsOk)
        {
            var fault = Fault;
            End();
            return fault;
        }

        var status = SqliteStatus.Success;
        Dictionary<TableSchema, TableSql> statements = [];
        foreach (var row in _touched)
        {
            if (_connection.Schema(row.Table) is not { } schema)
            {
                continue;
            }

            if (!statements.TryGetValue(schema, out var sql))
            {
                sql = new TableSql(schema);
                statements[schema] = sql;
            }

            status = _connection.QueryFirst(sql.SelectRow, out var current, row.PrimaryKey);
            if (!status.IsOk)
            {
                break;
            }

            var final = current is null ? null : schema.DataColumns.Count == 0 ? [] : current;
            if (Build(schema, row, Align(schema, row), final) is { } change)
            {
                changes.Add(change);
            }
        }

        End();
        return status;
    }

    public SqliteStatus ChargeCreatedRow(nint statement, int columnCount)
    {
        if (!CountWrite())
        {
            return Fault;
        }

        for (var column = 0; column < columnCount; column++)
        {
            var length = SqliteNative.ColumnType(statement, column) switch
            {
                SqliteNative.TypeText or SqliteNative.TypeBlob => SqliteNative.ColumnBytes(statement, column),
                SqliteNative.TypeNull => 0,
                _ => sizeof(long),
            };
            if (!FitsWrite(length, _remainingBytes))
            {
                return Fault;
            }

            _remainingBytes -= length;
        }

        return SqliteStatus.Success;
    }

    public void Dispose()
    {
        if (!_self.IsAllocated)
        {
            return;
        }

        if (_registered)
        {
            _ = SqliteNative.PreupdateHook(_connection.Handle, null, 0);
            _registered = false;
        }

        End();
        _self.Dispose();
    }

    private static SqlValue[]? Align(TableSchema schema, TouchedRow row)
    {
        if (row.Original is not { } original || ReferenceEquals(schema, row.Schema))
        {
            return row.Original;
        }

        var aligned = new SqlValue[schema.DataColumns.Count];
        for (var index = 0; index < aligned.Length; index++)
        {
            var name = schema.DataColumns[index].Name;
            for (var previous = 0; previous < row.Schema.DataColumns.Count && previous < original.Length; previous++)
            {
                if (string.Equals(row.Schema.DataColumns[previous].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    aligned[index] = original[previous];
                    break;
                }
            }
        }

        return aligned;
    }

    private static NetChange? Build(TableSchema schema, TouchedRow row, SqlValue[]? originalValues, SqlValue[]? final)
    {
        var absolute = row.Absolute ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (originalValues, final)
        {
            case (null, null):
                return null;
            case (null, { } inserted):
                return new NetChange(schema, row.PrimaryKey, row.EncodedKey, MutationOp.Insert, null, inserted, [], absolute, row.Replaced);
            case ({ } original, null):
                return new NetChange(schema, row.PrimaryKey, row.EncodedKey, MutationOp.Delete, original, null, [], absolute, row.Replaced);
            case ({ } original, { } updated):
                List<int> changed = [];
                for (var index = 0; index < schema.DataColumns.Count && index < original.Length && index < updated.Length; index++)
                {
                    if (!original[index].Equals(updated[index]))
                    {
                        changed.Add(index);
                    }
                }

                return changed.Count == 0
                    ? null
                    : new NetChange(schema, row.PrimaryKey, row.EncodedKey, MutationOp.Update, original, updated, changed, absolute, row.Replaced);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPreupdate(nint context, nint db, int op, byte* database, byte* table, long oldKey, long newKey)
    {
        var capture = GCHandle<PreupdateCapture>.FromIntPtr(context).Target;
        capture.Handle(db, op, database, table, oldKey, newKey);
    }

    private void Handle(nint db, int op, byte* database, byte* table, long oldKey, long newKey)
    {
        if (!_active || Suppressed || !Fault.IsOk || !IsMain(database) || IsInternal(table))
        {
            return;
        }

        var name = SqliteNative.ReadUtf8(table);
        var schema = _connection.Schema(name);
        if (schema is not null && SqliteNative.PreupdateCount(db) != schema.StorageColumnCount)
        {
            _connection.InvalidateSchemaCache();
            schema = _connection.Schema(name);
        }

        if (schema is null)
        {
            Fault = SqliteStatus.Fail(string.Concat("capture could not resolve schema for table ", name));
            return;
        }

        if (_connection.HasUserLimits && !ChargeWrite(db, op, schema.Columns))
        {
            SqliteNative.InterruptQuery(db);
            return;
        }

        IReadOnlyList<int> sources = [.. schema.DataColumns.Select(static column => column.Index)];
        if (schema.IsShadow)
        {
            if (schema.ContentOwner is not { } owner || _connection.Schema(owner) is not { IsVirtual: true } virtualTable)
            {
                return;
            }

            sources = [.. schema.DataColumns.Take(virtualTable.DataColumns.Count).Select(static column => column.Index)];
            schema = virtualTable;
            if (sources.Count != schema.DataColumns.Count)
            {
                Fault = SqliteStatus.Fail(string.Concat("virtual table content layout mismatch for ", owner));
                return;
            }
        }

        var depth = SqliteNative.PreupdateDepth(db);
        switch (op)
        {
            case SqliteNative.OpInsert:
                if (ReadKey(db, schema, newKey, useNew: true) is { } inserted)
                {
                    Touch(db, schema, sources, inserted, existed: false).Replaced = true;
                }

                break;
            case SqliteNative.OpDelete:
                if (ReadKey(db, schema, oldKey, useNew: false) is { } deleted)
                {
                    _ = Touch(db, schema, sources, deleted, existed: true);
                }

                break;
            case SqliteNative.OpUpdate:
                if (ReadKey(db, schema, oldKey, useNew: false) is not { } before || ReadKey(db, schema, newKey, useNew: true) is not { } after)
                {
                    return;
                }

                var row = Touch(db, schema, sources, before, existed: true);
                if (depth == 0 && Annotation is { } annotation && string.Equals(annotation.Table, schema.Table, StringComparison.OrdinalIgnoreCase))
                {
                    row.Absolute ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    row.Absolute.UnionWith(annotation.AbsoluteColumns);
                }

                if (!before.AsSpan().SequenceEqual(after))
                {
                    _ = Touch(db, schema, sources, after, existed: false);
                }

                break;
            default:
                Fault = SqliteStatus.Fail("unknown preupdate operation");
                break;
        }
    }

    private bool CountWrite()
    {
        if (++_writtenRows > SqlLimits.MaxWriteRows)
        {
            Fault = new SqliteStatus(SqliteNative.TooBig, "a transaction may write at most 1024 rows");
            return false;
        }

        return true;
    }

    private bool ChargeWrite(nint db, int op, IReadOnlyList<TableColumn> columns)
    {
        if (!CountWrite())
        {
            return false;
        }

        var bytes = 0;
        if (op != SqliteNative.OpInsert && !MeasureRow(db, columns, useNew: false, out bytes))
        {
            return false;
        }

        if (op != SqliteNative.OpDelete)
        {
            if (!MeasureRow(db, columns, useNew: true, out var after))
            {
                return false;
            }

            bytes = Math.Max(bytes, after);
        }

        _remainingBytes -= bytes;
        return true;
    }

    private bool MeasureRow(nint db, IReadOnlyList<TableColumn> columns, bool useNew, out int bytes)
    {
        bytes = 0;
        foreach (var column in columns)
        {
            nint value;
            var rc = useNew ? SqliteNative.PreupdateNew(db, column.Index, &value) : SqliteNative.PreupdateOld(db, column.Index, &value);
            if (rc == SqliteNative.Range && column.Generated)
            {
                continue;
            }

            if (rc != SqliteNative.Ok)
            {
                Fault = new SqliteStatus(rc, "preupdate value read failed");
                return false;
            }

            var length = SqliteNative.ValueType(value) switch
            {
                SqliteNative.TypeText or SqliteNative.TypeBlob => SqliteNative.ValueBytes(value),
                SqliteNative.TypeNull => 0,
                _ => sizeof(long),
            };
            if (!FitsWrite(length, _remainingBytes - bytes))
            {
                return false;
            }

            bytes += length;
        }

        return true;
    }

    private bool FitsWrite(int length, int remainingBytes)
    {
        if (length > remainingBytes)
        {
            Fault = new SqliteStatus(SqliteNative.TooBig, "a transaction may write at most 8 MiB");
            return false;
        }

        return true;
    }

    private TouchedRow Touch(nint db, TableSchema schema, IReadOnlyList<int> sources, SqlValue[] key, bool existed)
    {
        var encoded = PrimaryKeyCodec.Encode(key);
        var lookup = (schema.Table, PrimaryKeyCodec.ToKeyString(encoded));
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_byKey, lookup, out var exists);
        if (exists && slot is { } found)
        {
            return found;
        }

        SqlValue[]? original = null;
        if (existed)
        {
            original = new SqlValue[schema.DataColumns.Count];
            for (var index = 0; index < original.Length; index++)
            {
                original[index] = Read(db, sources[index], useNew: false);
            }
        }

        var row = new TouchedRow(schema, key, encoded, original);
        slot = row;
        _touched.Add(row);
        return row;
    }

    private SqlValue[]? ReadKey(nint db, TableSchema schema, long rowId, bool useNew)
    {
        if (schema.UsesImplicitRowId)
        {
            return [SqlValue.FromIntegral(rowId)];
        }

        var key = new SqlValue[schema.PrimaryKeyIndices.Count];
        for (var index = 0; index < key.Length; index++)
        {
            key[index] = Read(db, schema.PrimaryKeyIndices[index], useNew);
        }

        return Fault.IsOk ? key : null;
    }

    private SqlValue Read(nint db, int column, bool useNew)
    {
        nint value;
        var rc = useNew ? SqliteNative.PreupdateNew(db, column, &value) : SqliteNative.PreupdateOld(db, column, &value);
        if (rc != SqliteNative.Ok)
        {
            Fault = new SqliteStatus(rc, "preupdate value read failed");
            return SqlValue.Null;
        }

        return SqliteNative.ReadValue(value);
    }

    private static bool IsMain(byte* database) =>
        database is not null && MemoryMarshal.CreateReadOnlySpanFromNullTerminated(database).SequenceEqual("main"u8);

    private static bool IsInternal(byte* table)
    {
        if (table is null)
        {
            return true;
        }

        var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(table);
        return StartsWithIgnoreCase(name, "_tb_"u8) || StartsWithIgnoreCase(name, "sqlite_"u8);
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Length; index++)
        {
            if ((value[index] | 0x20) != (prefix[index] | 0x20))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class TouchedRow(TableSchema schema, SqlValue[] primaryKey, byte[] encodedKey, SqlValue[]? original)
    {
        public TableSchema Schema { get; } = schema;

        public string Table => Schema.Table;

        public SqlValue[] PrimaryKey { get; } = primaryKey;

        public byte[] EncodedKey { get; } = encodedKey;

        public SqlValue[]? Original { get; } = original;

        public HashSet<string>? Absolute { get; set; }

        public bool Replaced { get; set; }
    }
}

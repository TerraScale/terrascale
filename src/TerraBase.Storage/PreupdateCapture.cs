using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

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

internal readonly record struct TouchedState(bool Replaced, IReadOnlySet<string>? Absolute);

internal sealed record CaptureSavepoint(IReadOnlyList<TouchedState> Rows);

internal sealed unsafe class PreupdateCapture : IDisposable
{
    private const int MaxCachedTableNames = 512;

    private static readonly CaptureSavepoint EmptySavepoint = new([]);
    private static readonly IReadOnlySet<string> EmptyAbsoluteColumns = FrozenSet<string>.Empty;

    private readonly SqliteConnection _connection;
    private readonly Dictionary<RowLookupKey, TouchedRow> _byKey = new(RowLookupKeyComparer.Instance);
    private readonly List<TouchedRow> _touched = [];
    private readonly Dictionary<byte[], TableNameEntry> _tableNames = new(Utf8BytesComparer.Instance);
    private byte[]? _lastTableUtf8;
    private string? _lastTableName;
    private GCHandle<PreupdateCapture> _self;
    private int _writtenRows;
    private int _remainingBytes;
    private bool _active;
    private bool _registered;
    private bool _sawSavepointRollback;

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
        _sawSavepointRollback = false;
        _active = true;
    }

    public void Fail(SqliteStatus status)
    {
        if (Fault.IsOk && !status.IsOk)
        {
            Fault = status;
        }
    }

    public void MarkNeedsReselect() => _sawSavepointRollback = true;

    public TableSchema? Resolve(string table)
    {
        var lookup = _connection.Schema(table);
        return lookup switch
        {
            TableSchema schema => schema,
            TableNotFound => null,
            SqliteStatus failure => Unresolved(failure),
        };
    }

    private TableSchema? Unresolved(SqliteStatus failure)
    {
        Fail(failure);
        return null;
    }

    public void End()
    {
        _touched.Clear();
        _byKey.Clear();
        Annotation = null;
        _sawSavepointRollback = false;
        _active = false;
    }

    public CaptureSavepoint MarkSavepoint()
    {
        if (_touched.Count == 0)
        {
            return EmptySavepoint;
        }

        var rows = new TouchedState[_touched.Count];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = _touched[index];
            rows[index] = new TouchedState(row.Replaced, Copy(row.Absolute));
        }

        return new CaptureSavepoint(rows);
    }

    public void RestoreSavepoint(CaptureSavepoint mark)
    {
        _sawSavepointRollback = true;
        var kept = mark.Rows.Count;
        for (var index = kept; index < _touched.Count; index++)
        {
            var row = _touched[index];
            _ = _byKey.Remove(new RowLookupKey(row.Table, row.EncodedKey));
        }

        _touched.RemoveRange(kept, _touched.Count - kept);
        for (var index = 0; index < kept; index++)
        {
            _touched[index].Replaced = mark.Rows[index].Replaced;
            _touched[index].Absolute = Copy(mark.Rows[index].Absolute);
        }
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
        for (var index = 0; index < _touched.Count; index++)
        {
            var row = _touched[index];
            if (Resolve(row.Table) is not { } schema)
            {
                if (!Fault.IsOk)
                {
                    status = Fault;
                    break;
                }

                continue;
            }

            SqlValue[]? final;
            var requiresReselect = _sawSavepointRollback
                || !row.HasCapturedFinal
                || !ReferenceEquals(schema, row.Schema)
                || schema.IsVirtual
                || schema.HasVirtualGeneratedColumns;
            if (!requiresReselect)
            {
                final = row.Final;
            }
            else
            {
                status = _connection.QueryFirst(schema.Sql.SelectRow, out var current, row.PrimaryKey);
                if (!status.IsOk)
                {
                    break;
                }

                final = current is null ? null : schema.DataColumns.Count == 0 ? [] : current;
            }

            if (Build(schema, row, Align(schema, row), final) is { } change)
            {
                changes.Add(change);
            }
        }

        End();
        return status;
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

    private static HashSet<string>? Copy(IReadOnlySet<string>? columns) =>
        columns is null ? null : new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);

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
        var absolute = row.Absolute ?? EmptyAbsoluteColumns;
        switch (originalValues, final)
        {
            case (null, null):
                return null;
            case (null, { } inserted):
                return new NetChange(schema, row.PrimaryKey, row.EncodedKey, MutationOp.Insert, null, inserted, [], absolute, row.Replaced);
            case ({ } original, null):
                return new NetChange(schema, row.PrimaryKey, row.EncodedKey, MutationOp.Delete, original, null, [], absolute, row.Replaced);
            case ({ } original, { } updated):
                List<int>? changed = null;
                var limit = Math.Min(schema.DataColumns.Count, Math.Min(original.Length, updated.Length));
                for (var index = 0; index < limit; index++)
                {
                    if (!original[index].Equals(updated[index]))
                    {
                        changed ??= new List<int>(limit - index);
                        changed.Add(index);
                    }
                }

                return changed is null
                    ? null
                    : new NetChange(schema, row.PrimaryKey, row.EncodedKey, MutationOp.Update, original, updated, changed, absolute, row.Replaced);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPreupdate(nint context, nint db, int op, byte* database, byte* table, long oldKey, long newKey)
    {
        try
        {
            var capture = GCHandle<PreupdateCapture>.FromIntPtr(context).Target;
            capture.Handle(db, op, database, table, oldKey, newKey);
        }
        catch (Exception error)
        {
            try
            {
                GCHandle<PreupdateCapture>.FromIntPtr(context).Target.RecordFault(error);
            }
            catch
            {
            }

            SqliteNative.InterruptQuery(db);
        }
    }

    private void RecordFault(Exception error) => Fault = SqliteStatus.Fail(error.Message);

    private string ResolveTableName(byte* table)
    {
        var span = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(table);
        if (_lastTableUtf8 is { } lastUtf8 && span.SequenceEqual(lastUtf8) && _lastTableName is { } lastName)
        {
            return lastName;
        }

        var lookup = _tableNames.GetAlternateLookup<ReadOnlySpan<byte>>();
        if (lookup.TryGetValue(span, out var cached))
        {
            _lastTableUtf8 = cached.Bytes;
            _lastTableName = cached.Name;
            return cached.Name;
        }

        var name = Encoding.UTF8.GetString(span);
        if (_tableNames.Count < MaxCachedTableNames)
        {
            var bytes = span.ToArray();
            _tableNames[bytes] = new TableNameEntry(bytes, name);
            _lastTableUtf8 = bytes;
            _lastTableName = name;
        }

        return name;
    }

    private bool CanCaptureFinal(TableSchema schema) =>
        !_sawSavepointRollback && !schema.IsVirtual && !schema.IsShadow && !schema.HasVirtualGeneratedColumns;

    private SqlValue[] ReadFinal(nint db, TableSchema schema, ReadOnlySpan<int> sources)
    {
        var count = schema.DataColumns.Count;
        if (count == 0)
        {
            return [];
        }

        var values = new SqlValue[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = Read(db, sources[index], useNew: true);
        }

        return values;
    }

    private void Handle(nint db, int op, byte* database, byte* table, long oldKey, long newKey)
    {
        if (!_active || Suppressed || !Fault.IsOk || !IsMain(database) || IsInternal(table))
        {
            return;
        }

        var name = ResolveTableName(table);
        var schema = Resolve(name);
        if (schema is not null && SqliteNative.PreupdateCount(db) != schema.StorageColumnCount)
        {
            _connection.InvalidateSchemaCache();
            schema = Resolve(name);
        }

        if (schema is null)
        {
            Fail(SqliteStatus.Fail(string.Concat("capture could not resolve schema for table ", name)));
            return;
        }

        if (_connection.HasUserLimits && !ChargeWrite(db, op, schema.Columns))
        {
            SqliteNative.InterruptQuery(db);
            return;
        }

        ReadOnlySpan<int> sources = schema.DataColumnIndices;
        if (schema.IsShadow)
        {
            if (schema.ContentOwner is not { } owner || Resolve(owner) is not { IsVirtual: true } virtualTable)
            {
                return;
            }

            var take = Math.Min(schema.DataColumnIndices.Length, virtualTable.DataColumns.Count);
            sources = schema.DataColumnIndices.AsSpan(0, take);
            schema = virtualTable;
            if (sources.Length != schema.DataColumns.Count)
            {
                Fault = SqliteStatus.Fail(string.Concat("virtual table content layout mismatch for ", owner));
                return;
            }
        }

        var captureFinal = CanCaptureFinal(schema);
        var depth = SqliteNative.PreupdateDepth(db);
        switch (op)
        {
            case SqliteNative.OpInsert:
                if (ReadKey(db, schema, newKey, useNew: true) is { } inserted)
                {
                    var row = Touch(db, schema, sources, inserted, existed: false);
                    row.Replaced = true;
                    if (captureFinal)
                    {
                        row.Final = ReadFinal(db, schema, sources);
                        row.HasCapturedFinal = true;
                    }
                    else
                    {
                        row.HasCapturedFinal = false;
                    }
                }

                break;
            case SqliteNative.OpDelete:
                if (ReadKey(db, schema, oldKey, useNew: false) is { } deleted)
                {
                    var row = Touch(db, schema, sources, deleted, existed: true);
                    if (captureFinal)
                    {
                        row.Final = null;
                        row.HasCapturedFinal = true;
                    }
                    else
                    {
                        row.HasCapturedFinal = false;
                    }
                }

                break;
            case SqliteNative.OpUpdate:
                if (ReadKey(db, schema, oldKey, useNew: false) is not { } before || ReadKey(db, schema, newKey, useNew: true) is not { } after)
                {
                    return;
                }

                var updated = Touch(db, schema, sources, before, existed: true);
                if (depth == 0 && Annotation is { } annotation && string.Equals(annotation.Table, schema.Table, StringComparison.OrdinalIgnoreCase))
                {
                    updated.Absolute ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    updated.Absolute.UnionWith(annotation.AbsoluteColumns);
                }

                if (!before.AsSpan().SequenceEqual(after))
                {
                    if (captureFinal)
                    {
                        updated.Final = null;
                        updated.HasCapturedFinal = true;
                    }
                    else
                    {
                        updated.HasCapturedFinal = false;
                    }

                    var moved = Touch(db, schema, sources, after, existed: false);
                    if (captureFinal)
                    {
                        moved.Final = ReadFinal(db, schema, sources);
                        moved.HasCapturedFinal = true;
                    }
                    else
                    {
                        moved.HasCapturedFinal = false;
                    }
                }
                else if (captureFinal)
                {
                    updated.Final = ReadFinal(db, schema, sources);
                    updated.HasCapturedFinal = true;
                }
                else
                {
                    updated.HasCapturedFinal = false;
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
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
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

    private TouchedRow Touch(nint db, TableSchema schema, ReadOnlySpan<int> sources, SqlValue[] key, bool existed)
    {
        Span<byte> stackEncoded = stackalloc byte[128];
        byte[]? heapEncoded = null;
        var encodedSpan = PrimaryKeyCodec.TryEncode(key, stackEncoded, out var written)
            ? (ReadOnlySpan<byte>)stackEncoded[..written]
            : (heapEncoded = PrimaryKeyCodec.Encode(key));

        var lookup = _byKey.GetAlternateLookup<RowLookupKeyRef>();
        if (lookup.TryGetValue(new RowLookupKeyRef(schema.Table, encodedSpan), out var found))
        {
            return found;
        }

        var encoded = heapEncoded ?? encodedSpan.ToArray();
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
        _byKey[new RowLookupKey(schema.Table, encoded)] = row;
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

        var read = SqliteNative.ReadValue(value, out var result);
        if (!read.IsOk)
        {
            Fault = read;
            SqliteNative.InterruptQuery(db);
        }

        return result;
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

    private readonly record struct TableNameEntry(byte[] Bytes, string Name);

    private readonly record struct RowLookupKey(string Table, byte[] EncodedKey);

    private readonly ref struct RowLookupKeyRef(string table, ReadOnlySpan<byte> encodedKey)
    {
        public string Table { get; } = table;

        public ReadOnlySpan<byte> EncodedKey { get; } = encodedKey;
    }

    private sealed class Utf8BytesComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static Utf8BytesComparer Instance { get; } = new();

        private Utf8BytesComparer()
        {
        }

        public bool Equals(byte[]? x, byte[]? y) =>
            x is null || y is null ? x is null && y is null : x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            var hash = new HashCode();
            hash.AddBytes(alternate);
            return hash.ToHashCode();
        }

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }

    private sealed class RowLookupKeyComparer : IEqualityComparer<RowLookupKey>, IAlternateEqualityComparer<RowLookupKeyRef, RowLookupKey>
    {
        public static RowLookupKeyComparer Instance { get; } = new();

        private RowLookupKeyComparer()
        {
        }

        public bool Equals(RowLookupKey x, RowLookupKey y) =>
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase)
            && x.EncodedKey.AsSpan().SequenceEqual(y.EncodedKey);

        public int GetHashCode(RowLookupKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Table, StringComparer.OrdinalIgnoreCase);
            hash.AddBytes(obj.EncodedKey);
            return hash.ToHashCode();
        }

        public bool Equals(RowLookupKeyRef alternate, RowLookupKey other) =>
            string.Equals(alternate.Table, other.Table, StringComparison.OrdinalIgnoreCase)
            && alternate.EncodedKey.SequenceEqual(other.EncodedKey);

        public int GetHashCode(RowLookupKeyRef alternate)
        {
            var hash = new HashCode();
            hash.Add(alternate.Table, StringComparer.OrdinalIgnoreCase);
            hash.AddBytes(alternate.EncodedKey);
            return hash.ToHashCode();
        }

        public RowLookupKey Create(RowLookupKeyRef alternate) => new(alternate.Table, alternate.EncodedKey.ToArray());
    }

    private sealed class TouchedRow(TableSchema schema, SqlValue[] primaryKey, byte[] encodedKey, SqlValue[]? original)
    {
        public TableSchema Schema { get; } = schema;

        public string Table => Schema.Table;

        public SqlValue[] PrimaryKey { get; } = primaryKey;

        public byte[] EncodedKey { get; } = encodedKey;

        public SqlValue[]? Original { get; } = original;

        public SqlValue[]? Final { get; set; }

        public bool HasCapturedFinal { get; set; }

        public HashSet<string>? Absolute { get; set; }

        public bool Replaced { get; set; }
    }
}

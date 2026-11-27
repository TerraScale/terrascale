using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TerraBase.Storage;

public enum MutationOp : byte
{
    Insert = 1,
    Update = 2,
    Delete = 3,
}

public enum ChangeKind : byte
{
    Assign = 0,
    DeltaOp = 1,
    AbsoluteReset = 2,
}

public readonly record struct ColumnChange(string Column, ChangeKind Kind, ulong Epoch, SqlValue Value, long Delta);

public sealed record RowMutation(string Table, MutationOp Op, ulong CausalLength, IReadOnlyList<SqlValue> PrimaryKey, IReadOnlyList<ColumnChange> Changes);

public sealed record DeltaBatch(IReadOnlyList<string> Ddl, IReadOnlyList<RowMutation> Mutations)
{
    public static DeltaBatch Empty { get; } = new([], []);

    public bool IsEmpty => Ddl.Count == 0 && Mutations.Count == 0;
}

public static class MutationBatchCodec
{
    private const byte FormatVersion = 2;
    private const int LinearInternLimit = 16;
    private const int MaxRetainedBodyBytes = 256 * 1024;

    [ThreadStatic]
    private static EncodeScratch? _scratch;

    public static byte[] Encode(DeltaBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.IsEmpty)
        {
            return [];
        }

        var scratch = _scratch ??= new EncodeScratch();
        try
        {
            var bodyLength = WriteMutationsBody(scratch, batch.Mutations);
            var strings = scratch.Strings;
            var ddl = batch.Ddl;
            var headerSize = 1 + VarUIntSize((ulong)strings.Count) + VarUIntSize((ulong)ddl.Count);
            for (var index = 0; index < strings.Count; index++)
            {
                var byteCount = Encoding.UTF8.GetByteCount(strings[index]);
                headerSize += VarUIntSize((ulong)byteCount) + byteCount;
            }

            for (var index = 0; index < ddl.Count; index++)
            {
                var byteCount = Encoding.UTF8.GetByteCount(ddl[index]);
                headerSize += VarUIntSize((ulong)byteCount) + byteCount;
            }

            var result = new byte[headerSize + bodyLength];
            var destination = result.AsSpan();
            var offset = 0;
            destination[offset++] = FormatVersion;
            offset += WireWriter.WriteVarUInt(destination[offset..], (ulong)strings.Count);
            for (var index = 0; index < strings.Count; index++)
            {
                offset += WriteString(destination[offset..], strings[index]);
            }

            offset += WireWriter.WriteVarUInt(destination[offset..], (ulong)ddl.Count);
            for (var index = 0; index < ddl.Count; index++)
            {
                offset += WriteString(destination[offset..], ddl[index]);
            }

            scratch.Body.AsSpan(0, bodyLength).CopyTo(destination[offset..]);
            return result;
        }
        finally
        {
            scratch.Reset();
        }
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out DeltaBatch batch)
    {
        batch = DeltaBatch.Empty;
        if (payload.IsEmpty)
        {
            return true;
        }

        var reader = new WireReader(payload);
        if (reader.Byte() != FormatVersion)
        {
            return false;
        }

        var stringCount = reader.Count();
        var strings = stringCount == 0 ? [] : new string[stringCount];
        for (var index = 0; index < strings.Length && !reader.Failed; index++)
        {
            strings[index] = reader.String();
        }

        var ddlCount = reader.Count();
        var ddl = ddlCount == 0 ? [] : new string[ddlCount];
        for (var index = 0; index < ddl.Length && !reader.Failed; index++)
        {
            ddl[index] = reader.String();
        }

        var mutationCount = reader.Count();
        var mutations = mutationCount == 0 ? [] : new RowMutation[mutationCount];
        for (var index = 0; index < mutations.Length && !reader.Failed; index++)
        {
            var tableIndex = reader.VarUInt();
            var table = tableIndex < (ulong)strings.Length ? strings[(int)tableIndex] : string.Empty;
            var op = reader.Byte();
            if (op is not ((byte)MutationOp.Insert or (byte)MutationOp.Update or (byte)MutationOp.Delete))
            {
                return false;
            }

            var causalLength = reader.VarUInt();
            var keyCount = reader.Count();
            var keys = keyCount == 0 ? [] : new SqlValue[keyCount];
            for (var key = 0; key < keys.Length && !reader.Failed; key++)
            {
                keys[key] = reader.Value();
            }

            var changeCount = reader.Count();
            var changes = changeCount == 0 ? [] : new ColumnChange[changeCount];
            for (var change = 0; change < changes.Length && !reader.Failed; change++)
            {
                var columnIndex = reader.VarUInt();
                var column = columnIndex < (ulong)strings.Length ? strings[(int)columnIndex] : string.Empty;
                var kind = reader.Byte();
                var epoch = reader.VarUInt();
                switch (kind)
                {
                    case (byte)ChangeKind.Assign or (byte)ChangeKind.AbsoluteReset:
                        changes[change] = new ColumnChange(column, (ChangeKind)kind, epoch, reader.Value(), 0);
                        break;
                    case (byte)ChangeKind.DeltaOp:
                        changes[change] = new ColumnChange(column, ChangeKind.DeltaOp, epoch, SqlValue.Null, reader.VarInt());
                        break;
                    default:
                        return false;
                }
            }

            mutations[index] = new RowMutation(table, (MutationOp)op, causalLength, keys, changes);
        }

        if (reader.Failed || !reader.End)
        {
            return false;
        }

        batch = new DeltaBatch(ddl, mutations);
        return true;
    }

    private static int WriteMutationsBody(EncodeScratch scratch, IReadOnlyList<RowMutation> mutations)
    {
        if (mutations is RowMutation[] array)
        {
            return WriteMutationsSpan(scratch, array);
        }

        if (mutations is List<RowMutation> list)
        {
            return WriteMutationsSpan(scratch, CollectionsMarshal.AsSpan(list));
        }

        var count = mutations.Count;
        var pos = 0;
        scratch.WriteVarUInt(ref pos, (ulong)count);
        string? lastTable = null;
        var lastTableIndex = 0;
        var columnHint = 0;
        for (var index = 0; index < count; index++)
        {
            WriteMutation(scratch, ref pos, mutations[index], ref lastTable, ref lastTableIndex, ref columnHint);
        }

        return pos;
    }

    private static int WriteMutationsSpan(EncodeScratch scratch, ReadOnlySpan<RowMutation> mutations)
    {
        var pos = 0;
        scratch.WriteVarUInt(ref pos, (ulong)mutations.Length);
        string? lastTable = null;
        var lastTableIndex = 0;
        var columnHint = 0;
        for (var index = 0; index < mutations.Length; index++)
        {
            WriteMutation(scratch, ref pos, mutations[index], ref lastTable, ref lastTableIndex, ref columnHint);
        }

        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteMutation(
        EncodeScratch scratch,
        ref int pos,
        RowMutation mutation,
        ref string? lastTable,
        ref int lastTableIndex,
        ref int columnHint)
    {
        var table = mutation.Table;
        int tableIndex;
        if (ReferenceEquals(table, lastTable))
        {
            tableIndex = lastTableIndex;
        }
        else
        {
            tableIndex = scratch.Intern(table, ref columnHint);
            lastTable = table;
            lastTableIndex = tableIndex;
        }

        scratch.WriteVarUInt(ref pos, (ulong)tableIndex);
        scratch.WriteByte(ref pos, (byte)mutation.Op);
        scratch.WriteVarUInt(ref pos, mutation.CausalLength);

        var keys = mutation.PrimaryKey;
        if (keys is SqlValue[] keyArray)
        {
            scratch.WriteVarUInt(ref pos, (ulong)keyArray.Length);
            for (var keyIndex = 0; keyIndex < keyArray.Length; keyIndex++)
            {
                scratch.WriteValue(ref pos, keyArray[keyIndex]);
            }
        }
        else if (keys is List<SqlValue> keyList)
        {
            var keySpan = CollectionsMarshal.AsSpan(keyList);
            scratch.WriteVarUInt(ref pos, (ulong)keySpan.Length);
            for (var keyIndex = 0; keyIndex < keySpan.Length; keyIndex++)
            {
                scratch.WriteValue(ref pos, keySpan[keyIndex]);
            }
        }
        else
        {
            scratch.WriteVarUInt(ref pos, (ulong)keys.Count);
            for (var keyIndex = 0; keyIndex < keys.Count; keyIndex++)
            {
                scratch.WriteValue(ref pos, keys[keyIndex]);
            }
        }

        var changes = mutation.Changes;
        if (changes is ColumnChange[] changeArray)
        {
            WriteChangesSpan(scratch, ref pos, changeArray, ref columnHint);
        }
        else if (changes is List<ColumnChange> changeList)
        {
            WriteChangesSpan(scratch, ref pos, CollectionsMarshal.AsSpan(changeList), ref columnHint);
        }
        else
        {
            scratch.WriteVarUInt(ref pos, (ulong)changes.Count);
            for (var changeIndex = 0; changeIndex < changes.Count; changeIndex++)
            {
                WriteChange(scratch, ref pos, changes[changeIndex], ref columnHint);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteChangesSpan(EncodeScratch scratch, ref int pos, ReadOnlySpan<ColumnChange> changes, ref int columnHint)
    {
        scratch.WriteVarUInt(ref pos, (ulong)changes.Length);
        for (var changeIndex = 0; changeIndex < changes.Length; changeIndex++)
        {
            WriteChange(scratch, ref pos, changes[changeIndex], ref columnHint);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteChange(EncodeScratch scratch, ref int pos, ColumnChange change, ref int columnHint)
    {
        var columnIndex = scratch.Intern(change.Column, ref columnHint);
        scratch.WriteVarUInt(ref pos, (ulong)columnIndex);
        scratch.WriteByte(ref pos, (byte)change.Kind);
        scratch.WriteVarUInt(ref pos, change.Epoch);
        switch (change.Kind)
        {
            case ChangeKind.Assign or ChangeKind.AbsoluteReset:
                scratch.WriteValue(ref pos, change.Value);
                break;
            case ChangeKind.DeltaOp:
                scratch.WriteVarInt(ref pos, change.Delta);
                break;
            default:
                throw new UnreachableException();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int VarUIntSize(ulong value) => value < 0x80 ? 1 : 1 + (BitOperations.Log2(value) / 7);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteString(Span<byte> destination, string value)
    {
        var length = value.Length;
        if (length == 0)
        {
            destination[0] = 0;
            return 1;
        }

        if (length < 0x80 && Ascii.FromUtf16(value, destination.Slice(1, length), out _) == OperationStatus.Done)
        {
            destination[0] = (byte)length;
            return 1 + length;
        }

        var count = Encoding.UTF8.GetByteCount(value);
        var header = WireWriter.WriteVarUInt(destination, (ulong)count);
        return header + Encoding.UTF8.GetBytes(value, destination.Slice(header, count));
    }

    private sealed class EncodeScratch
    {
        private Dictionary<string, int>? _lookup;
        private bool _usingLookup;

        public List<string> Strings { get; } = new(32);

        public byte[] Body { get; private set; } = new byte[4096];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Intern(string value, ref int hint)
        {
            var span = CollectionsMarshal.AsSpan(Strings);
            var count = span.Length;
            if ((uint)hint < (uint)count && ReferenceEquals(span[hint], value))
            {
                var matched = hint;
                hint = matched + 1 < count ? matched + 1 : 0;
                return matched;
            }

            if (!_usingLookup)
            {
                for (var index = 0; index < count; index++)
                {
                    if (ReferenceEquals(span[index], value))
                    {
                        hint = index + 1 < count ? index + 1 : 0;
                        return index;
                    }
                }

                for (var index = 0; index < count; index++)
                {
                    if (string.Equals(span[index], value, StringComparison.Ordinal))
                    {
                        hint = index + 1 < count ? index + 1 : 0;
                        return index;
                    }
                }

                if (count < LinearInternLimit)
                {
                    Strings.Add(value);
                    hint = 0;
                    return count;
                }

                PromoteToLookup();
            }

            var lookup = _lookup ?? throw new UnreachableException();
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, value, out var exists);
            if (!exists)
            {
                slot = Strings.Count;
                Strings.Add(value);
            }

            hint = slot + 1 < Strings.Count ? slot + 1 : 0;
            return slot;
        }

        private void PromoteToLookup()
        {
            var lookup = _lookup ??= new Dictionary<string, int>(32, StringComparer.Ordinal);
            var span = CollectionsMarshal.AsSpan(Strings);
            for (var index = 0; index < span.Length; index++)
            {
                lookup[span[index]] = index;
            }

            _usingLookup = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ref int pos, byte value)
        {
            Ensure(pos, 1);
            Body[pos++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarUInt(ref int pos, ulong value)
        {
            if (value < 0x80)
            {
                Ensure(pos, 1);
                Body[pos++] = (byte)value;
                return;
            }

            Ensure(pos, 10);
            var buffer = Body;
            while (value >= 0x80)
            {
                buffer[pos++] = (byte)(value | 0x80);
                value >>= 7;
            }

            buffer[pos++] = (byte)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVarInt(ref int pos, long value) =>
            WriteVarUInt(ref pos, (ulong)((value << 1) ^ (value >> 63)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteValue(ref int pos, SqlValue value)
        {
            WriteByte(ref pos, (byte)value.Kind);
            switch (value.Kind)
            {
                case SqlValueKind.Null:
                    break;
                case SqlValueKind.Integral:
                    _ = value.TryGetIntegral(out var integral);
                    WriteVarInt(ref pos, integral);
                    break;
                case SqlValueKind.Real:
                    _ = value.TryGetReal(out var real);
                    Ensure(pos, 8);
                    BinaryPrimitives.WriteDoubleLittleEndian(Body.AsSpan(pos), real);
                    pos += 8;
                    break;
                case SqlValueKind.Text:
                    _ = value.TryGetText(out var text);
                    WriteStringValue(ref pos, text ?? throw new UnreachableException());
                    break;
                case SqlValueKind.Blob:
                    _ = value.TryGetBlob(out var blob);
                    var bytes = blob.Span;
                    WriteVarUInt(ref pos, (ulong)bytes.Length);
                    if (!bytes.IsEmpty)
                    {
                        Ensure(pos, bytes.Length);
                        bytes.CopyTo(Body.AsSpan(pos));
                        pos += bytes.Length;
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteStringValue(ref int pos, string value)
        {
            var length = value.Length;
            if (length == 0)
            {
                WriteByte(ref pos, 0);
                return;
            }

            if (length < 0x80)
            {
                Ensure(pos, 1 + length);
                if (Ascii.FromUtf16(value, Body.AsSpan(pos + 1, length), out _) == OperationStatus.Done)
                {
                    Body[pos] = (byte)length;
                    pos += 1 + length;
                    return;
                }
            }

            var count = Encoding.UTF8.GetByteCount(value);
            WriteVarUInt(ref pos, (ulong)count);
            Ensure(pos, count);
            pos += Encoding.UTF8.GetBytes(value, Body.AsSpan(pos, count));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Ensure(int pos, int needed)
        {
            if (Body.Length - pos < needed)
            {
                Grow(pos, needed);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int pos, int needed)
        {
            var next = new byte[Math.Max(Body.Length * 2, pos + needed)];
            Body.AsSpan(0, pos).CopyTo(next);
            Body = next;
        }

        public void Reset()
        {
            Strings.Clear();
            if (_usingLookup)
            {
                _lookup?.Clear();
                _usingLookup = false;
            }

            if (Body.Length > MaxRetainedBodyBytes)
            {
                Body = new byte[4096];
            }
        }
    }
}

internal record struct ColumnMeta(HlcTimestamp Hlc, ulong Epoch, long DeltaSum);

internal readonly record struct ColumnMetaEntry(int Ordinal, ColumnMeta Meta);

internal sealed class RowMeta
{
    public static RowMeta Empty { get; } = new(0);

    private ColumnMetaEntry[] _entries;
    private int _count;

    public RowMeta()
    {
        _entries = [];
    }

    public RowMeta(int columnCapacity)
    {
        _entries = columnCapacity > 0 ? new ColumnMetaEntry[columnCapacity] : [];
    }

    public ulong CausalLength { get; set; }

    public HlcTimestamp RowHlc { get; set; }

    public int ColumnCount => _count;

    public bool Alive => (CausalLength & 1) == 1;

    public int MaxEncodedSize => 22 + (_count * 32);

    public HlcTimestamp MaxHlc()
    {
        var max = RowHlc;
        var entries = _entries;
        var count = _count;
        for (var index = 0; index < count; index++)
        {
            max = HlcCodec.Max(max, entries[index].Meta.Hlc);
        }

        return max;
    }

    public void CloneColumnsFrom(RowMeta source)
    {
        var count = source._count;
        if (count == 0)
        {
            _count = 0;
            return;
        }

        if (_entries.Length < count)
        {
            _entries = new ColumnMetaEntry[count];
        }

        source._entries.AsSpan(0, count).CopyTo(_entries);
        _count = count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ColumnMeta Column(int ordinal)
    {
        var entries = _entries;
        var count = _count;
        for (var index = 0; index < count; index++)
        {
            var current = entries[index].Ordinal;
            if (current == ordinal)
            {
                return entries[index].Meta;
            }

            if (current > ordinal)
            {
                break;
            }
        }

        return default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetColumn(int ordinal, ColumnMeta meta)
    {
        var entries = _entries;
        var count = _count;
        if (count == 0 || ordinal > entries[count - 1].Ordinal)
        {
            if (count == entries.Length)
            {
                Grow(count + 1);
                entries = _entries;
            }

            entries[count] = new ColumnMetaEntry(ordinal, meta);
            _count = count + 1;
            return;
        }

        var index = 0;
        while (index < count)
        {
            var current = entries[index].Ordinal;
            if (current == ordinal)
            {
                entries[index] = new ColumnMetaEntry(ordinal, meta);
                return;
            }

            if (current > ordinal)
            {
                break;
            }

            index++;
        }

        if (count == entries.Length)
        {
            Grow(count + 1);
            entries = _entries;
        }

        entries.AsSpan(index, count - index).CopyTo(entries.AsSpan(index + 1));
        entries[index] = new ColumnMetaEntry(ordinal, meta);
        _count = count + 1;
    }

    public static RowMeta? Decode(ReadOnlySpan<byte> blob)
    {
        var reader = new WireReader(blob);
        var causalLength = reader.VarUInt();
        var rowHlc = reader.Hlc();
        var count = reader.UInt16();
        if (reader.Failed)
        {
            return null;
        }

        var meta = new RowMeta(count) { CausalLength = causalLength, RowHlc = rowHlc };
        for (var index = 0; index < count && !reader.Failed; index++)
        {
            var ordinal = reader.UInt16();
            var hlc = reader.Hlc();
            var epoch = reader.VarUInt();
            var sum = reader.VarInt();
            meta.SetColumn(ordinal, new ColumnMeta(hlc, epoch, sum));
        }

        return reader.Failed || !reader.End ? null : meta;
    }

    public int EncodeTo(Span<byte> destination)
    {
        var offset = WireWriter.WriteVarUInt(destination, CausalLength);
        HlcCodec.Write(destination[offset..], RowHlc);
        offset += HlcTimestamp.EncodedSize;
        var count = _count;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)count);
        offset += 2;
        var entries = _entries;
        for (var index = 0; index < count; index++)
        {
            var entry = entries[index];
            BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)entry.Ordinal);
            offset += 2;
            HlcCodec.Write(destination[offset..], entry.Meta.Hlc);
            offset += HlcTimestamp.EncodedSize;
            offset += WireWriter.WriteVarUInt(destination[offset..], entry.Meta.Epoch);
            offset += WireWriter.WriteVarInt(destination[offset..], entry.Meta.DeltaSum);
        }

        return offset;
    }

    public byte[] Encode()
    {
        var maxSize = MaxEncodedSize;
        if (maxSize <= 512)
        {
            Span<byte> stack = stackalloc byte[512];
            var written = EncodeTo(stack);
            return stack[..written].ToArray();
        }

        var rented = ArrayPool<byte>.Shared.Rent(maxSize);
        try
        {
            var written = EncodeTo(rented);
            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void Grow(int minCapacity)
    {
        var next = new ColumnMetaEntry[Math.Max(_entries.Length == 0 ? 4 : _entries.Length * 2, minCapacity)];
        _entries.AsSpan(0, _count).CopyTo(next);
        _entries = next;
    }
}

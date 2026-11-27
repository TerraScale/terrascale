using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
        var lookup = scratch.Lookup;
        var strings = scratch.Strings;
        var writer = new PooledBufferWriter(64 + (batch.Mutations.Count * 64));
        try
        {
            var mutations = batch.Mutations;
            for (var mutationIndex = 0; mutationIndex < mutations.Count; mutationIndex++)
            {
                var mutation = mutations[mutationIndex];
                Intern(mutation.Table, lookup, strings);
                var changes = mutation.Changes;
                for (var changeIndex = 0; changeIndex < changes.Count; changeIndex++)
                {
                    Intern(changes[changeIndex].Column, lookup, strings);
                }
            }

            writer.Byte(FormatVersion);
            writer.VarUInt((ulong)strings.Count);
            for (var index = 0; index < strings.Count; index++)
            {
                writer.String(strings[index]);
            }

            var ddl = batch.Ddl;
            writer.VarUInt((ulong)ddl.Count);
            for (var index = 0; index < ddl.Count; index++)
            {
                writer.String(ddl[index]);
            }

            writer.VarUInt((ulong)mutations.Count);
            for (var mutationIndex = 0; mutationIndex < mutations.Count; mutationIndex++)
            {
                var mutation = mutations[mutationIndex];
                writer.VarUInt((ulong)lookup[mutation.Table]);
                writer.Byte((byte)mutation.Op);
                writer.VarUInt(mutation.CausalLength);
                var keys = mutation.PrimaryKey;
                writer.VarUInt((ulong)keys.Count);
                for (var keyIndex = 0; keyIndex < keys.Count; keyIndex++)
                {
                    writer.Value(keys[keyIndex]);
                }

                var changes = mutation.Changes;
                writer.VarUInt((ulong)changes.Count);
                for (var changeIndex = 0; changeIndex < changes.Count; changeIndex++)
                {
                    var change = changes[changeIndex];
                    writer.VarUInt((ulong)lookup[change.Column]);
                    writer.Byte((byte)change.Kind);
                    writer.VarUInt(change.Epoch);
                    switch (change.Kind)
                    {
                        case ChangeKind.Assign or ChangeKind.AbsoluteReset:
                            writer.Value(change.Value);
                            break;
                        case ChangeKind.DeltaOp:
                            writer.VarInt(change.Delta);
                            break;
                        default:
                            throw new UnreachableException();
                    }
                }
            }

            return writer.ToArray();
        }
        finally
        {
            writer.Dispose();
            lookup.Clear();
            strings.Clear();
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
            var table = Lookup(ref reader, strings);
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
                var column = Lookup(ref reader, strings);
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

    private static void Intern(string value, Dictionary<string, int> lookup, List<string> strings)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, value, out var exists);
        if (!exists)
        {
            slot = strings.Count;
            strings.Add(value);
        }
    }

    private static string Lookup(ref WireReader reader, string[] strings)
    {
        var index = reader.VarUInt();
        return index < (ulong)strings.Length ? strings[(int)index] : string.Empty;
    }

    private sealed class EncodeScratch
    {
        public Dictionary<string, int> Lookup { get; } = new(32, StringComparer.Ordinal);

        public List<string> Strings { get; } = new(32);
    }
}

internal record struct ColumnMeta(HlcTimestamp Hlc, ulong Epoch, long DeltaSum);

internal sealed class RowMeta
{
    public RowMeta()
    {
        Columns = [];
    }

    public RowMeta(int columnCapacity)
    {
        Columns = columnCapacity > 0 ? new Dictionary<int, ColumnMeta>(columnCapacity) : [];
    }

    public ulong CausalLength { get; set; }

    public HlcTimestamp RowHlc { get; set; }

    public Dictionary<int, ColumnMeta> Columns { get; }

    public bool Alive => (CausalLength & 1) == 1;

    public int MaxEncodedSize => 22 + (Columns.Count * 32);

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
            meta.Columns[ordinal] = new ColumnMeta(hlc, epoch, sum);
        }

        return reader.Failed || !reader.End ? null : meta;
    }

    public int EncodeTo(Span<byte> destination)
    {
        var offset = WireWriter.WriteVarUInt(destination, CausalLength);
        HlcCodec.Write(destination[offset..], RowHlc);
        offset += HlcTimestamp.EncodedSize;
        var count = Columns.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)count);
        offset += 2;
        if (count == 0)
        {
            return offset;
        }

        if (count <= 16)
        {
            Span<int> ordinals = stackalloc int[16];
            return WriteColumns(destination, offset, ordinals[..count]);
        }

        var rented = ArrayPool<int>.Shared.Rent(count);
        try
        {
            return WriteColumns(destination, offset, rented.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
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

    public ColumnMeta Column(int ordinal) => Columns.GetValueOrDefault(ordinal);

    private int WriteColumns(Span<byte> destination, int offset, Span<int> ordinals)
    {
        var index = 0;
        var previous = int.MinValue;
        var sorted = true;
        foreach (var key in Columns.Keys)
        {
            if (key < previous)
            {
                sorted = false;
            }

            previous = key;
            ordinals[index++] = key;
        }

        if (!sorted)
        {
            ordinals.Sort();
        }

        for (var ordinalIndex = 0; ordinalIndex < ordinals.Length; ordinalIndex++)
        {
            var ordinal = ordinals[ordinalIndex];
            var column = Columns[ordinal];
            BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)ordinal);
            offset += 2;
            HlcCodec.Write(destination[offset..], column.Hlc);
            offset += HlcTimestamp.EncodedSize;
            offset += WireWriter.WriteVarUInt(destination[offset..], column.Epoch);
            offset += WireWriter.WriteVarInt(destination[offset..], column.DeltaSum);
        }

        return offset;
    }
}

using System.Buffers;
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

    public static byte[] Encode(DeltaBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.IsEmpty)
        {
            return [];
        }

        Dictionary<string, int> lookup = new(StringComparer.Ordinal);
        List<string> strings = [];
        foreach (var mutation in batch.Mutations)
        {
            Intern(mutation.Table, lookup, strings);
            foreach (var change in mutation.Changes)
            {
                Intern(change.Column, lookup, strings);
            }
        }

        var writer = new ArrayBufferWriter<byte>(64 + (batch.Mutations.Count * 48));
        WireWriter.Byte(writer, FormatVersion);
        WireWriter.VarUInt(writer, (ulong)strings.Count);
        foreach (var value in strings)
        {
            WireWriter.String(writer, value);
        }

        WireWriter.VarUInt(writer, (ulong)batch.Ddl.Count);
        foreach (var ddl in batch.Ddl)
        {
            WireWriter.String(writer, ddl);
        }

        WireWriter.VarUInt(writer, (ulong)batch.Mutations.Count);
        foreach (var mutation in batch.Mutations)
        {
            WireWriter.VarUInt(writer, (ulong)lookup[mutation.Table]);
            WireWriter.Byte(writer, (byte)mutation.Op);
            WireWriter.VarUInt(writer, mutation.CausalLength);
            WireWriter.VarUInt(writer, (ulong)mutation.PrimaryKey.Count);
            foreach (var key in mutation.PrimaryKey)
            {
                WireWriter.Value(writer, key);
            }

            WireWriter.VarUInt(writer, (ulong)mutation.Changes.Count);
            foreach (var change in mutation.Changes)
            {
                WireWriter.VarUInt(writer, (ulong)lookup[change.Column]);
                WireWriter.Byte(writer, (byte)change.Kind);
                WireWriter.VarUInt(writer, change.Epoch);
                switch (change.Kind)
                {
                    case ChangeKind.Assign or ChangeKind.AbsoluteReset:
                        WireWriter.Value(writer, change.Value);
                        break;
                    case ChangeKind.DeltaOp:
                        WireWriter.VarInt(writer, change.Delta);
                        break;
                    default:
                        throw new UnreachableException();
                }
            }
        }

        return writer.WrittenSpan.ToArray();
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

        var strings = new string[reader.Count()];
        for (var index = 0; index < strings.Length && !reader.Failed; index++)
        {
            strings[index] = reader.String();
        }

        var ddl = new string[reader.Count()];
        for (var index = 0; index < ddl.Length && !reader.Failed; index++)
        {
            ddl[index] = reader.String();
        }

        var mutations = new RowMutation[reader.Count()];
        for (var index = 0; index < mutations.Length && !reader.Failed; index++)
        {
            var table = Lookup(ref reader, strings);
            var op = reader.Byte();
            if (op is not ((byte)MutationOp.Insert or (byte)MutationOp.Update or (byte)MutationOp.Delete))
            {
                return false;
            }

            var causalLength = reader.VarUInt();
            var keys = new SqlValue[reader.Count()];
            for (var key = 0; key < keys.Length && !reader.Failed; key++)
            {
                keys[key] = reader.Value();
            }

            var changes = new ColumnChange[reader.Count()];
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
}

internal record struct ColumnMeta(HlcTimestamp Hlc, ulong Epoch, long DeltaSum);

internal sealed class RowMeta
{
    public ulong CausalLength { get; set; }

    public HlcTimestamp RowHlc { get; set; }

    public Dictionary<int, ColumnMeta> Columns { get; } = [];

    public bool Alive => (CausalLength & 1) == 1;

    public static RowMeta? Decode(ReadOnlySpan<byte> blob)
    {
        var reader = new WireReader(blob);
        var meta = new RowMeta { CausalLength = reader.VarUInt(), RowHlc = reader.Hlc() };
        var count = reader.UInt16();
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

    public byte[] Encode()
    {
        var writer = new ArrayBufferWriter<byte>(16 + (Columns.Count * 16));
        WireWriter.VarUInt(writer, CausalLength);
        WireWriter.Hlc(writer, RowHlc);
        WireWriter.UInt16(writer, (ushort)Columns.Count);
        foreach (var (ordinal, column) in Columns.OrderBy(static pair => pair.Key))
        {
            WireWriter.UInt16(writer, (ushort)ordinal);
            WireWriter.Hlc(writer, column.Hlc);
            WireWriter.VarUInt(writer, column.Epoch);
            WireWriter.VarInt(writer, column.DeltaSum);
        }

        return writer.WrittenSpan.ToArray();
    }

    public ColumnMeta Column(int ordinal) => Columns.GetValueOrDefault(ordinal);
}

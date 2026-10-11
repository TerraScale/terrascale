using System.Buffers.Binary;
using System.Collections.Immutable;

namespace TerraBase.Engine.Lattices;

/// <summary>
/// The persisted and replicated bytes of a document's register (docs/design/anna-core.md, Encoding): a format byte, its
/// causal context (prefixes, then cloud dots) and its writes, with little-endian fixed-width integers, minimal LEB128
/// counts and lengths, and replicas and dots in ascending order. Equal states therefore encode to equal bytes on every
/// platform. Decoding accepts canonical bytes only, so decoding and encoding again returns the same bytes.
/// </summary>
public static class LatticeEncoding
{
    /// <summary>The format <see cref="Encode"/> writes first; decoding refuses any other.</summary>
    public const byte Format = 1;

    private const int DotBytes = 16;

    // A write before its body: its dot, its stamp and its kind.
    private const int WriteBytes = DotBytes + 8 + 1;

    private const byte TombstoneKind = 0;
    private const byte LiveKind = 1;

    /// <summary>The bytes of <paramref name="register"/>.</summary>
    public static byte[] Encode(MvRegister<DocumentWrite> register)
    {
        ArgumentNullException.ThrowIfNull(register);
        var versions = register.Context.Versions.ToArray();
        Array.Sort(versions, (left, right) => left.Key.Value.CompareTo(right.Key.Value));
        var cloud = register.Context.Cloud.ToArray();
        Array.Sort(cloud, Compare);
        var writes = register.Values.ToArray();
        Array.Sort(writes, (left, right) => Compare(left.Key, right.Key));

        var size = checked(1 + LengthOf(versions.Length) + (versions.Length * DotBytes) + LengthOf(cloud.Length) + (cloud.Length * DotBytes)
            + LengthOf(writes.Length));
        foreach (var (_, write) in writes)
        {
            size = checked(size + WriteBytes + (write.IsTombstone ? 0 : LengthOf(write.Body.Length) + write.Body.Length));
        }

        var bytes = new byte[size];
        var writer = new Writer(bytes);
        writer.Byte(Format);
        writer.Length(versions.Length);
        foreach (var (replica, counter) in versions)
        {
            writer.Dot(new Dot(replica, counter));
        }

        writer.Length(cloud.Length);
        foreach (var dot in cloud)
        {
            writer.Dot(dot);
        }

        writer.Length(writes.Length);
        foreach (var (dot, write) in writes)
        {
            writer.Dot(dot);
            writer.UInt64(write.Stamp.Value);
            writer.Byte(write.IsTombstone ? TombstoneKind : LiveKind);
            if (!write.IsTombstone)
            {
                writer.Length(write.Body.Length);
                writer.Bytes(write.Body);
            }
        }

        writer.End();
        return bytes;
    }

    /// <summary>The register <paramref name="data"/> encodes.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a canonical encoding of a register.</exception>
    public static MvRegister<DocumentWrite> DecodeDocument(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        if (reader.Byte() != Format)
        {
            throw new InvalidDataException("the register has an unknown format");
        }

        var versions = ImmutableDictionary.CreateBuilder<ReplicaId, ulong>();
        Dot? previous = null;
        for (var count = reader.Count(DotBytes); count > 0; count--)
        {
            var prefix = reader.Dot();
            if (previous is { } last && last.Replica.Value >= prefix.Replica.Value)
            {
                throw new InvalidDataException("the register's replicas are not in ascending order");
            }

            versions.Add(prefix.Replica, prefix.Counter);
            previous = prefix;
        }

        var cloud = ImmutableHashSet.CreateBuilder<Dot>();
        previous = null;
        for (var count = reader.Count(DotBytes); count > 0; count--)
        {
            cloud.Add(Ascending(ref previous, reader.Dot()));
        }

        var context = CausalContext.FromCanonical(versions.ToImmutable(), cloud.ToImmutable());
        var writes = ImmutableDictionary.CreateBuilder<Dot, DocumentWrite>();
        previous = null;
        for (var count = reader.Count(WriteBytes); count > 0; count--)
        {
            var dot = Ascending(ref previous, reader.Dot());
            var stamp = new Hlc(reader.UInt64());
            var body = reader.Byte() switch
            {
                TombstoneKind => null,
                LiveKind => reader.Bytes(reader.Length()).ToArray(),
                _ => throw new InvalidDataException("the register holds a write of an unknown kind"),
            };
            writes.Add(dot, DocumentWrite.Owning(stamp, body));
        }

        reader.End();
        return MvRegister<DocumentWrite>.FromParts(writes.ToImmutable(), context);
    }

    private static Dot Ascending(ref Dot? previous, Dot dot)
    {
        if (previous is { } last && Compare(last, dot) >= 0)
        {
            throw new InvalidDataException("the register's dots are not in ascending order");
        }

        previous = dot;
        return dot;
    }

    private static int Compare(Dot left, Dot right) =>
        left.Replica.Value != right.Replica.Value ? left.Replica.Value.CompareTo(right.Replica.Value) : left.Counter.CompareTo(right.Counter);

    private static int LengthOf(int value)
    {
        var bytes = 1;
        for (var rest = (uint)value; rest >= 0x80; rest >>= 7)
        {
            bytes++;
        }

        return bytes;
    }

    private ref struct Writer(Span<byte> bytes)
    {
        private readonly Span<byte> _bytes = bytes;
        private int _at;

        public void Byte(byte value) => _bytes[_at++] = value;

        public void UInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_bytes[_at..], value);
            _at += 8;
        }

        public void Dot(Dot dot)
        {
            UInt64(dot.Replica.Value);
            UInt64(dot.Counter);
        }

        public void Length(int value)
        {
            var rest = (uint)value;
            for (; rest >= 0x80; rest >>= 7)
            {
                Byte((byte)(rest | 0x80));
            }

            Byte((byte)rest);
        }

        public void Bytes(ReadOnlySpan<byte> value)
        {
            value.CopyTo(_bytes[_at..]);
            _at += value.Length;
        }

        public readonly void End()
        {
            if (_at != _bytes.Length)
            {
                throw new InvalidOperationException("the register encoding was sized wrongly");
            }
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public byte Byte()
        {
            Need(1);
            return _data[_at++];
        }

        public ulong UInt64()
        {
            Need(8);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_at..]);
            _at += 8;
            return value;
        }

        public Dot Dot()
        {
            var replica = new ReplicaId(UInt64());
            return new Dot(replica, UInt64());
        }

        // A minimal LEB128 length of at most int.MaxValue.
        public int Length()
        {
            uint value = 0;
            for (var shift = 0; ; shift += 7)
            {
                var next = Byte();
                if (shift == 28 && next > 0x07)
                {
                    throw new InvalidDataException("the register holds a length beyond its limit");
                }

                value |= (uint)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    if (next == 0 && shift > 0)
                    {
                        throw new InvalidDataException("the register holds a length that is not minimal");
                    }

                    return (int)value;
                }
            }
        }

        // A count no larger than the entries of at least minimumBytes each that the rest could hold.
        public int Count(int minimumBytes)
        {
            var count = Length();
            if (count > (_data.Length - _at) / minimumBytes)
            {
                throw new InvalidDataException("the register counts more entries than it holds");
            }

            return count;
        }

        public ReadOnlySpan<byte> Bytes(int length)
        {
            Need(length);
            var value = _data.Slice(_at, length);
            _at += length;
            return value;
        }

        public readonly void End()
        {
            if (_at != _data.Length)
            {
                throw new InvalidDataException("the register is followed by unexpected bytes");
            }
        }

        private readonly void Need(int length)
        {
            if (_data.Length - _at < length)
            {
                throw new InvalidDataException("the register encoding is truncated");
            }
        }
    }
}

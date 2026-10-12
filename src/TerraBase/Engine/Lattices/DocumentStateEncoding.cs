using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraBase.Engine.Lattices;

/// <summary>
/// The bytes of a <see cref="DocumentState"/> (docs/terrabase/multi-region-replication.md section 4), following the
/// <c>LatticeEncoding</c> rules: a format byte, canonical order, little-endian fixed-width stamps, minimal LEB128
/// counts, lengths and numbers, and a dictionary of the state's nodes so each version names its node by index.
/// Decoding is a trust boundary: it accepts canonical bytes of a normalized state only, so decoding and encoding again
/// returns the same bytes.
/// </summary>
/// <remarks>
/// Two forms. The full form (<see cref="Encode"/>) carries every value: deltas on the wire. The stored form
/// (<see cref="EncodeStored"/>) leaves out the value of a plain field, whose value is the base, because the stored
/// document already holds it; decoding it needs that document.
/// <code>
/// state   = format(1) nodes:count{u64} clear fields:count{field}
/// clear   = 0 | (1 live | 2 deleted) version changed
/// field   = name:bytes base counters:count{node:index version inc:leb dec:leb changed} dots:count{dot}
/// base    = 0 | (1 tombstone | 2 value | 3 value in the document) version changed [value:bytes if 2]
/// dot     = element:bytes version (0 | 1 version) changed
/// version = stamp:u64 node:index;  changed = u64;  bytes = length:leb utf8
/// </code>
/// </remarks>
public static class DocumentStateEncoding
{
    /// <summary>The format every encoding starts with; decoding refuses any other.</summary>
    public const byte Format = 1;

    private const byte Absent = 0;
    private const byte Tombstone = 1;
    private const byte Live = 1;
    private const byte Deleted = 2;
    private const byte Value = 2;
    private const byte InDocument = 3;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The full form of <paramref name="state"/>, with every value: for deltas.</summary>
    public static byte[] Encode(DocumentState state) => Write(state, stored: false, hashing: false);

    /// <summary>The stored form of <paramref name="state"/>: plain values live in its materialized document.</summary>
    public static byte[] EncodeStored(DocumentState state) => Write(state, stored: true, hashing: false);

    /// <summary>The state <paramref name="data"/> holds in the full form.</summary>
    /// <exception cref="InvalidDataException">The bytes are not the canonical full form of a normalized state.</exception>
    public static DocumentState Decode(ReadOnlySpan<byte> data) => Read(data, document: null);

    /// <summary>The state <paramref name="data"/> holds in the stored form, with its materialized <paramref name="document"/> (empty when none).</summary>
    /// <exception cref="InvalidDataException">The bytes are not the canonical stored form of a normalized state, or the document is not its materialization.</exception>
    public static DocumentState DecodeStored(ReadOnlySpan<byte> data, ReadOnlySpan<byte> document)
    {
        JsonDocument? json = null;
        try
        {
            json = document.IsEmpty ? null : JsonDocument.Parse(document.ToArray(), DocumentState.JsonOptions);
            var state = Read(data, json?.RootElement ?? default);
            if (!document.SequenceEqual(state.Materialize()))
            {
                throw new InvalidDataException("the stored document is not the state's materialization");
            }

            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("the stored document is not JSON", exception);
        }
        finally
        {
            json?.Dispose();
        }
    }

    /// <summary>
    /// The digest of <paramref name="state"/> for anti-entropy: the first 128 bits of SHA-256 over its stored form
    /// without bookkeeping and its materialized document. Equal states have equal digests on every node.
    /// </summary>
    public static UInt128 Digest(DocumentState state)
    {
        var hash = SHA256.HashData([.. Write(state, stored: true, hashing: true), .. state.Materialize() ?? []]);
        return new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(hash), BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8)));
    }

    private static byte[] Write(DocumentState state, bool stored, bool hashing)
    {
        ArgumentNullException.ThrowIfNull(state);
        var nodes = Nodes(state);
        var plain = stored && state.Exists;
        var writer = new Writer(nodes, hashing);
        writer.Byte(Format);
        writer.Number((ulong)nodes.Count);
        foreach (var node in nodes.Keys)
        {
            writer.UInt64(node.Value);
        }

        if (state.Clear is { } clear)
        {
            writer.Byte(clear.Deleted ? Deleted : Live);
            writer.Version(clear.Version);
            writer.Changed(clear.Changed);
        }
        else
        {
            writer.Byte(Absent);
        }

        writer.Number((ulong)state.Fields.Count);
        foreach (var (name, field) in state.Fields)
        {
            writer.Text(name);
            if (field.Base is { } @base)
            {
                var inDocument = plain && field.Value(state.Clear?.Version).Kind == FieldKind.Base;
                writer.Byte(@base.Value is null ? Tombstone : inDocument ? InDocument : Value);
                writer.Version(@base.Version);
                writer.Changed(@base.Changed);
                if (@base.Value is { } value && !inDocument)
                {
                    writer.Text(value);
                }
            }
            else
            {
                writer.Byte(Absent);
            }

            writer.Number((ulong)field.Counters.Count);
            foreach (var (node, counter) in field.Counters)
            {
                writer.Number((ulong)nodes[node]);
                writer.Version(counter.Epoch);
                writer.Number(counter.Increments);
                writer.Number(counter.Decrements);
                writer.Changed(counter.Changed);
            }

            writer.Number((ulong)field.Dots.Count);
            foreach (var (key, dot) in field.Dots)
            {
                writer.Text(key.Element);
                writer.Version(key.Version);
                if (dot.RemovedAt is { } removed)
                {
                    writer.Byte(1);
                    writer.Version(removed);
                }
                else
                {
                    writer.Byte(0);
                }

                writer.Changed(dot.Changed);
            }
        }

        return writer.ToArray();
    }

    private static DocumentState Read(ReadOnlySpan<byte> data, JsonElement? document)
    {
        var reader = new Reader(data);
        if (reader.Byte() != Format)
        {
            throw new InvalidDataException("the document state has an unknown format");
        }

        var nodes = new ReplicaId[reader.Count(8)];
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new ReplicaId(reader.UInt64());
        }

        ClearEntry? clear = reader.Byte() switch
        {
            Absent => null,
            Live => new ClearEntry(reader.Version(nodes), false, reader.Changed()),
            Deleted => new ClearEntry(reader.Version(nodes), true, reader.Changed()),
            _ => throw new InvalidDataException("the document state has a clear of an unknown kind"),
        };

        var fields = DocumentState.Bottom.Fields.ToBuilder();
        for (var count = reader.Count(4); count > 0; count--)
        {
            var name = reader.Text();
            var kind = reader.Byte();
            BaseEntry? @base = kind switch
            {
                Absent => null,
                Tombstone or Value or InDocument => new BaseEntry(reader.Version(nodes), null, reader.Changed()),
                _ => throw new InvalidDataException("the document state has a base of an unknown kind"),
            };
            if (kind == Value)
            {
                @base = @base!.Value with { Value = Json(reader.Text()) };
            }
            else if (kind == InDocument)
            {
                if (document is not { ValueKind: JsonValueKind.Object } doc || !doc.TryGetProperty(name, out var value))
                {
                    throw new InvalidDataException("the document state refers to a value its document does not hold");
                }

                @base = @base!.Value with { Value = value.GetRawText() };
            }

            var counters = FieldState.NoCounters.ToBuilder();
            for (var entries = reader.Count(20); entries > 0; entries--)
            {
                var node = nodes[reader.Index(nodes.Length)];
                counters[node] = new CounterEntry(reader.Version(nodes), reader.Number(), reader.Number(), reader.Changed());
            }

            var dots = FieldState.NoDots.ToBuilder();
            for (var entries = reader.Count(19); entries > 0; entries--)
            {
                var key = new DotKey(Json(reader.Text()), reader.Version(nodes));
                EntryVersion? removed = reader.Byte() switch
                {
                    0 => null,
                    1 => reader.Version(nodes),
                    _ => throw new InvalidDataException("the document state has a dot of an unknown kind"),
                };
                dots[key] = new DotEntry(removed, reader.Changed());
            }

            fields[name] = new FieldState(@base, counters.ToImmutable(), dots.ToImmutable());
        }

        reader.End();
        var state = new DocumentState(clear, fields.ToImmutable());

        // Re-encoding catches every non-canonical choice at once: order, duplicates, unused nodes, non-minimal numbers, value placement.
        if (!state.Normalize().Equals(state) || !data.SequenceEqual(Write(state, stored: document is not null, hashing: false)))
        {
            throw new InvalidDataException("the document state is not canonical");
        }

        return state;
    }

    // A value must be compact JSON exactly as the state writes it.
    private static string Json(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text, DocumentState.JsonOptions);
            if (string.Equals(DocumentState.Canonical(json.RootElement), text, StringComparison.Ordinal))
            {
                return text;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("the document state holds a value that is not JSON", exception);
        }

        throw new InvalidDataException("the document state holds a value that is not canonical JSON");
    }

    private static SortedDictionary<ReplicaId, int> Nodes(DocumentState state)
    {
        var nodes = new SortedSet<ReplicaId>(Comparer<ReplicaId>.Create((x, y) => x.Value.CompareTo(y.Value)));
        if (state.Clear is { } clear)
        {
            nodes.Add(clear.Version.Node);
        }

        foreach (var field in state.Fields.Values)
        {
            if (field.Base is { } @base)
            {
                nodes.Add(@base.Version.Node);
            }

            foreach (var (node, counter) in field.Counters)
            {
                nodes.Add(node);
                nodes.Add(counter.Epoch.Node);
            }

            foreach (var (key, dot) in field.Dots)
            {
                nodes.Add(key.Version.Node);
                if (dot.RemovedAt is { } removed)
                {
                    nodes.Add(removed.Node);
                }
            }
        }

        var index = new SortedDictionary<ReplicaId, int>(nodes.Comparer);
        foreach (var node in nodes)
        {
            index.Add(node, index.Count);
        }

        return index;
    }

    private sealed class Writer(SortedDictionary<ReplicaId, int> nodes, bool hashing)
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public void Byte(byte value)
        {
            _buffer.GetSpan(1)[0] = value;
            _buffer.Advance(1);
        }

        public void UInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.GetSpan(8), value);
            _buffer.Advance(8);
        }

        public void Number(ulong value)
        {
            for (; value >= 0x80; value >>= 7)
            {
                Byte((byte)(value | 0x80));
            }

            Byte((byte)value);
        }

        public void Version(EntryVersion version)
        {
            UInt64(version.Stamp.Value);
            Number((ulong)nodes[version.Node]);
        }

        // Bookkeeping is not part of the state: the digest hashes it as zero.
        public void Changed(Hlc changed) => UInt64(hashing ? 0 : changed.Value);

        public void Text(string value)
        {
            var length = Strict.GetByteCount(value);
            Number((ulong)length);
            _buffer.Advance(Strict.GetBytes(value, _buffer.GetSpan(length)));
        }

        public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
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

        // LEB128 of at most 64 bits; minimality is left to the re-encoding check.
        public ulong Number()
        {
            ulong value = 0;
            for (var shift = 0; ; shift += 7)
            {
                var next = Byte();
                if (shift == 63 && next > 1)
                {
                    throw new InvalidDataException("the document state holds a number beyond 64 bits");
                }

                value |= (ulong)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    return value;
                }
            }
        }

        // A count no larger than the entries of at least minimumBytes each that the rest could hold.
        public int Count(int minimumBytes)
        {
            var count = Number();
            if (count > (ulong)((_data.Length - _at) / minimumBytes))
            {
                throw new InvalidDataException("the document state counts more entries than it holds");
            }

            return (int)count;
        }

        public int Index(int count)
        {
            var index = Number();
            return index < (ulong)count ? (int)index : throw new InvalidDataException("the document state names an unknown node");
        }

        public EntryVersion Version(ReplicaId[] nodes)
        {
            var stamp = new Hlc(UInt64());
            return new EntryVersion(stamp, nodes[Index(nodes.Length)]);
        }

        public Hlc Changed() => new(UInt64());

        public string Text()
        {
            var length = Count(1);
            Need(length);
            try
            {
                return Strict.GetString(_data.Slice(_at, length));
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("the document state holds text that is not UTF-8", exception);
            }
            finally
            {
                _at += length;
            }
        }

        public readonly void End()
        {
            if (_at != _data.Length)
            {
                throw new InvalidDataException("the document state is followed by unexpected bytes");
            }
        }

        private readonly void Need(int length)
        {
            if (_data.Length - _at < length)
            {
                throw new InvalidDataException("the document state is truncated");
            }
        }
    }
}

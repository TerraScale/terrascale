using System.Buffers.Binary;

namespace TerraBase.Engine;

/// <summary>
/// The bytes of a change group's dependency vector, as a client database's journal stores it and its stream sends it
/// (docs/design/anna-core.md, Per-origin streams): a minimal LEB128 count, then each position's 16-byte origin and its
/// sequence (uint64, little-endian), in ascending origin byte order, with no origin twice and every sequence at least 1.
/// Decoding accepts this canonical form only.
/// </summary>
public static class OriginPositions
{
    /// <summary>Most positions one dependency vector holds.</summary>
    public const int MaxCount = 1024;

    /// <summary>Bytes of one origin.</summary>
    public const int OriginBytes = 16;

    private const int PositionBytes = OriginBytes + 8;

    /// <summary>Bytes <see cref="Write"/> writes for <paramref name="count"/> positions.</summary>
    public static int Size(int count) => LengthOf(count) + (count * PositionBytes);

    /// <summary>The bytes of <paramref name="positions"/>, which must be canonical.</summary>
    public static byte[] Encode(IReadOnlyList<OriginPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var bytes = new byte[Size(positions.Count)];
        Write(positions, bytes);
        return bytes;
    }

    /// <summary>Writes <paramref name="positions"/> at the start of <paramref name="destination"/>; returns the bytes written.</summary>
    /// <exception cref="ArgumentException">The positions are not canonical.</exception>
    public static int Write(IReadOnlyList<OriginPosition> positions, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Count > MaxCount)
        {
            throw new ArgumentException($"a dependency vector holds at most {MaxCount} positions", nameof(positions));
        }

        var at = 0;
        for (var rest = (uint)positions.Count; ; rest >>= 7)
        {
            if (rest < 0x80)
            {
                destination[at++] = (byte)rest;
                break;
            }

            destination[at++] = (byte)(rest | 0x80);
        }

        ReadOnlySpan<byte> previous = default;
        foreach (var (origin, sequence) in positions)
        {
            if (origin.Length != OriginBytes || sequence < 1 || (!previous.IsEmpty && previous.SequenceCompareTo(origin.Span) >= 0))
            {
                throw new ArgumentException("dependency positions have 16-byte origins in ascending order and sequences of at least 1", nameof(positions));
            }

            origin.Span.CopyTo(destination[at..]);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[(at + OriginBytes)..], (ulong)sequence);
            previous = origin.Span;
            at += PositionBytes;
        }

        return at;
    }

    /// <summary>Reads the positions <paramref name="data"/> starts with; <paramref name="read"/> is how many bytes they took.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a canonical dependency vector.</exception>
    public static List<OriginPosition> Read(ReadOnlySpan<byte> data, out int read)
    {
        uint count = 0;
        var at = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (at == data.Length || shift > 14)
            {
                throw new InvalidDataException("a dependency vector's count is truncated or too large");
            }

            var next = data[at++];
            count |= (uint)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                if (next == 0 && shift > 0)
                {
                    throw new InvalidDataException("a dependency vector's count is not minimal");
                }

                break;
            }
        }

        if (count > MaxCount || count > (uint)((data.Length - at) / PositionBytes))
        {
            throw new InvalidDataException($"a dependency vector holds at most {MaxCount} positions, within its bytes");
        }

        var positions = new List<OriginPosition>((int)count);
        ReadOnlySpan<byte> previous = default;
        for (var index = 0; index < count; index++, at += PositionBytes)
        {
            var origin = data.Slice(at, OriginBytes);
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(data[(at + OriginBytes)..]);
            if (sequence is 0 or > long.MaxValue || (!previous.IsEmpty && previous.SequenceCompareTo(origin) >= 0))
            {
                throw new InvalidDataException("a dependency vector holds an origin out of order or a sequence of 0");
            }

            positions.Add(new OriginPosition(origin.ToArray(), (long)sequence));
            previous = origin;
        }

        read = at;
        return positions;
    }

    private static int LengthOf(int value)
    {
        var bytes = 1;
        for (var rest = (uint)value; rest >= 0x80; rest >>= 7)
        {
            bytes++;
        }

        return bytes;
    }
}

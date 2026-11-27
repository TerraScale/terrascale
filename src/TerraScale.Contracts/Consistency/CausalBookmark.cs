using System.Buffers;
using System.Buffers.Text;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace TerraScale.Contracts.Consistency;

public sealed class CausalBookmark : IEquatable<CausalBookmark>
{
    public const int MaxEncodedLength = 1024;
    public const int MaxRegionBytes = 64;

    private const byte FormatVersion = 1;

    private readonly KeyValuePair<string, long>[] _entries;

    private CausalBookmark(KeyValuePair<string, long>[] entries)
    {
        _entries = entries;
    }

    public static CausalBookmark Empty { get; } = new([]);

    public IReadOnlyList<KeyValuePair<string, long>> Entries => _entries;

    public bool IsEmpty => _entries.Length == 0;

    public int GetBinarySize() => 1 + VarUIntSize((ulong)_entries.Length) + _entries.Sum(static entry =>
    {
        var bytes = Encoding.UTF8.GetByteCount(entry.Key);
        return VarUIntSize((ulong)bytes) + bytes + VarUIntSize((ulong)entry.Value);
    });

    public static CausalBookmark Create(IEnumerable<KeyValuePair<string, long>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Dictionary<string, long> merged = new(StringComparer.Ordinal);
        foreach (var (region, sequence) in entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(region);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(Encoding.UTF8.GetByteCount(region), MaxRegionBytes, nameof(entries));
            ArgumentOutOfRangeException.ThrowIfNegative(sequence, nameof(entries));
            if (sequence == 0)
            {
                continue;
            }

            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(merged, region, out _);
            slot = Math.Max(slot, sequence);
        }

        return FromSorted([.. merged.OrderBy(static pair => pair.Key, StringComparer.Ordinal)]);
    }

    public static CausalBookmark ForRegion(string region, long sequence) => Create([new(region, sequence)]);

    public long Get(string region)
    {
        ArgumentNullException.ThrowIfNull(region);
        foreach (var (key, sequence) in _entries)
        {
            if (string.Equals(key, region, StringComparison.Ordinal))
            {
                return sequence;
            }
        }

        return 0;
    }

    public CausalBookmark Merge(CausalBookmark other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.IsEmpty ? this : IsEmpty ? other : Create([.. _entries, .. other._entries]);
    }

    public bool Dominates(CausalBookmark other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (region, sequence) in other._entries)
        {
            if (Get(region) < sequence)
            {
                return false;
            }
        }

        return true;
    }

    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteByte(writer, FormatVersion);
        WriteVarUInt(writer, (ulong)_entries.Length);
        foreach (var (region, sequence) in _entries)
        {
            var length = Encoding.UTF8.GetByteCount(region);
            WriteVarUInt(writer, (ulong)length);
            var span = writer.GetSpan(length);
            Encoding.UTF8.GetBytes(region, span);
            writer.Advance(length);
            WriteVarUInt(writer, (ulong)sequence);
        }
    }

    public string Encode()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var writer = new ArrayBufferWriter<byte>(8 + (_entries.Length * 16));
        WriteTo(writer);
        return Base64Url.EncodeToString(writer.WrittenSpan);
    }

    public static bool TryDecode(ReadOnlySpan<char> encoded, out CausalBookmark bookmark)
    {
        bookmark = Empty;
        if (encoded.IsEmpty)
        {
            return true;
        }

        if (encoded.Length > MaxEncodedLength)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[Base64Url.GetMaxDecodedLength(MaxEncodedLength)];
        if (Base64Url.DecodeFromChars(encoded, buffer, out _, out var written) != OperationStatus.Done)
        {
            return false;
        }

        ReadOnlySpan<byte> source = buffer[..written];
        if (!TryRead(ref source, out var decoded) || !source.IsEmpty)
        {
            return false;
        }

        bookmark = decoded;
        return true;
    }

    public static bool TryRead(ref ReadOnlySpan<byte> source, out CausalBookmark bookmark)
    {
        bookmark = Empty;
        if (source.IsEmpty || source[0] != FormatVersion)
        {
            return false;
        }

        var cursor = source[1..];
        if (!TryReadVarUInt(ref cursor, out var count) || count > (ulong)cursor.Length)
        {
            return false;
        }

        var entries = new KeyValuePair<string, long>[(int)count];
        for (var index = 0; index < entries.Length; index++)
        {
            if (!TryReadVarUInt(ref cursor, out var length) || length == 0 || length > MaxRegionBytes || length > (ulong)cursor.Length)
            {
                return false;
            }

            var regionBytes = cursor[..(int)length];
            if (!Utf8.IsValid(regionBytes))
            {
                return false;
            }

            var region = Encoding.UTF8.GetString(regionBytes);
            cursor = cursor[(int)length..];
            if (!TryReadVarUInt(ref cursor, out var sequence) || sequence == 0 || sequence > long.MaxValue)
            {
                return false;
            }

            if (index > 0 && string.CompareOrdinal(entries[index - 1].Key, region) >= 0)
            {
                return false;
            }

            entries[index] = new(region, (long)sequence);
        }

        source = cursor;
        bookmark = FromSorted(entries);
        return true;
    }

    public bool Equals(CausalBookmark? other)
    {
        if (other is null || other._entries.Length != _entries.Length)
        {
            return false;
        }

        for (var index = 0; index < _entries.Length; index++)
        {
            if (!string.Equals(_entries[index].Key, other._entries[index].Key, StringComparison.Ordinal)
                || _entries[index].Value != other._entries[index].Value)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is CausalBookmark other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (region, sequence) in _entries)
        {
            hash.Add(region, StringComparer.Ordinal);
            hash.Add(sequence);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => Encode();

    private static CausalBookmark FromSorted(KeyValuePair<string, long>[] entries) =>
        entries.Length == 0 ? Empty : new CausalBookmark(entries);

    private static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    private static void WriteVarUInt(IBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(10);
        var written = 0;
        while (value >= 0x80)
        {
            span[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[written++] = (byte)value;
        writer.Advance(written);
    }

    private static int VarUIntSize(ulong value) => 1 + (BitOperations.Log2(value) / 7);

    private static bool TryReadVarUInt(ref ReadOnlySpan<byte> source, out ulong value)
    {
        value = 0;
        for (var index = 0; index < 10 && index < source.Length; index++)
        {
            var current = source[index];
            if (index == 9 && current > 1)
            {
                return false;
            }

            value |= (ulong)(current & 0x7F) << (7 * index);
            if (current < 0x80)
            {
                source = source[(index + 1)..];
                return true;
            }
        }

        value = 0;
        return false;
    }
}

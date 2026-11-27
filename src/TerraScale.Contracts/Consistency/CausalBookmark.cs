using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace TerraScale.Contracts.Consistency;

public sealed class CausalBookmark : IEquatable<CausalBookmark>
{
    public const int MaxEncodedLength = 1024;
    public const int MaxRegionBytes = 64;

    private const byte FormatVersion = 1;
    private const int MaxInternedRegions = 256;

    private static readonly ConcurrentDictionary<ulong, string> ShortRegionCache = new();
    private static readonly Comparison<KeyValuePair<string, long>> EntryComparison =
        static (left, right) => string.CompareOrdinal(left.Key, right.Key);

    private readonly KeyValuePair<string, long>[] _entries;
    private string? _encoded;

    private CausalBookmark(KeyValuePair<string, long>[] entries)
    {
        _entries = entries;
    }

    public static CausalBookmark Empty { get; } = new([]);

    public IReadOnlyList<KeyValuePair<string, long>> Entries => _entries;

    public bool IsEmpty => _entries.Length == 0;

    public int GetBinarySize()
    {
        var entries = _entries;
        var total = 1 + VarUIntSize((ulong)entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var bytes = Encoding.UTF8.GetByteCount(entry.Key);
            total += VarUIntSize((ulong)bytes) + bytes + VarUIntSize((ulong)entry.Value);
        }

        return total;
    }

    public static CausalBookmark Create(IEnumerable<KeyValuePair<string, long>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries is Dictionary<string, long> dictionary && dictionary.Comparer.Equals(StringComparer.Ordinal))
        {
            return CreateFromOrdinalDictionary(dictionary);
        }

        if (entries is KeyValuePair<string, long>[] array)
        {
            return CreateFromSpan(array);
        }

        if (entries is List<KeyValuePair<string, long>> list)
        {
            return CreateFromSpan(CollectionsMarshal.AsSpan(list));
        }

        Dictionary<string, long> merged = new(StringComparer.Ordinal);
        foreach (var (region, sequence) in entries)
        {
            ValidateEntry(region, sequence);
            if (sequence == 0)
            {
                continue;
            }

            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(merged, region, out _);
            slot = Math.Max(slot, sequence);
        }

        return CreateFromValidatedDictionary(merged);
    }

    public static CausalBookmark ForRegion(string region, long sequence)
    {
        ValidateEntry(region, sequence);
        return sequence == 0 ? Empty : new CausalBookmark([new KeyValuePair<string, long>(region, sequence)]);
    }

    public CausalBookmark With(string region, long sequence)
    {
        ValidateEntry(region, sequence);
        var entries = _entries;
        if (entries.Length == 0)
        {
            return sequence == 0 ? Empty : new CausalBookmark([new KeyValuePair<string, long>(region, sequence)]);
        }

        if (entries.Length == 1)
        {
            var cmp = string.CompareOrdinal(entries[0].Key, region);
            if (cmp == 0)
            {
                return sequence == 0
                    ? Empty
                    : entries[0].Value == sequence
                        ? this
                        : new CausalBookmark([new KeyValuePair<string, long>(region, sequence)]);
            }

            if (sequence == 0)
            {
                return this;
            }

            return cmp < 0
                ? new CausalBookmark([entries[0], new KeyValuePair<string, long>(region, sequence)])
                : new CausalBookmark([new KeyValuePair<string, long>(region, sequence), entries[0]]);
        }

        var index = 0;
        while (index < entries.Length)
        {
            var cmp = string.CompareOrdinal(entries[index].Key, region);
            if (cmp == 0)
            {
                if (entries[index].Value == sequence)
                {
                    return this;
                }

                if (sequence == 0)
                {
                    if (entries.Length == 1)
                    {
                        return Empty;
                    }

                    var shrunk = new KeyValuePair<string, long>[entries.Length - 1];
                    if (index > 0)
                    {
                        entries.AsSpan(0, index).CopyTo(shrunk);
                    }

                    if (index + 1 < entries.Length)
                    {
                        entries.AsSpan(index + 1).CopyTo(shrunk.AsSpan(index));
                    }

                    return new CausalBookmark(shrunk);
                }

                var replaced = new KeyValuePair<string, long>[entries.Length];
                entries.AsSpan().CopyTo(replaced);
                replaced[index] = new KeyValuePair<string, long>(region, sequence);
                return new CausalBookmark(replaced);
            }

            if (cmp > 0)
            {
                break;
            }

            index++;
        }

        if (sequence == 0)
        {
            return this;
        }

        var expanded = new KeyValuePair<string, long>[entries.Length + 1];
        if (index > 0)
        {
            entries.AsSpan(0, index).CopyTo(expanded);
        }

        expanded[index] = new KeyValuePair<string, long>(region, sequence);
        if (index < entries.Length)
        {
            entries.AsSpan(index).CopyTo(expanded.AsSpan(index + 1));
        }

        return new CausalBookmark(expanded);
    }

    public long Get(string region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var entries = _entries;
        for (var index = 0; index < entries.Length; index++)
        {
            if (string.Equals(entries[index].Key, region, StringComparison.Ordinal))
            {
                return entries[index].Value;
            }
        }

        return 0;
    }

    public CausalBookmark Merge(CausalBookmark other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.IsEmpty || ReferenceEquals(this, other))
        {
            return this;
        }

        if (IsEmpty)
        {
            return other;
        }

        var left = _entries;
        var right = other._entries;
        var merged = new KeyValuePair<string, long>[left.Length + right.Length];
        var leftIndex = 0;
        var rightIndex = 0;
        var outIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var cmp = string.CompareOrdinal(left[leftIndex].Key, right[rightIndex].Key);
            if (cmp < 0)
            {
                merged[outIndex++] = left[leftIndex++];
            }
            else if (cmp > 0)
            {
                merged[outIndex++] = right[rightIndex++];
            }
            else
            {
                merged[outIndex++] = new KeyValuePair<string, long>(left[leftIndex].Key, Math.Max(left[leftIndex].Value, right[rightIndex].Value));
                leftIndex++;
                rightIndex++;
            }
        }

        while (leftIndex < left.Length)
        {
            merged[outIndex++] = left[leftIndex++];
        }

        while (rightIndex < right.Length)
        {
            merged[outIndex++] = right[rightIndex++];
        }

        if (outIndex < merged.Length)
        {
            Array.Resize(ref merged, outIndex);
        }

        return new CausalBookmark(merged);
    }

    public bool Dominates(CausalBookmark other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other) || other._entries.Length == 0)
        {
            return true;
        }

        var ours = _entries;
        var theirs = other._entries;
        if (ours.Length < theirs.Length)
        {
            return false;
        }

        if (theirs.Length == 1)
        {
            return Get(theirs[0].Key) >= theirs[0].Value;
        }

        var ourIndex = 0;
        for (var theirIndex = 0; theirIndex < theirs.Length; theirIndex++)
        {
            var (theirKey, theirSeq) = theirs[theirIndex];
            while (true)
            {
                if (ourIndex >= ours.Length)
                {
                    return false;
                }

                var cmp = string.CompareOrdinal(ours[ourIndex].Key, theirKey);
                if (cmp < 0)
                {
                    ourIndex++;
                    continue;
                }

                if (cmp > 0 || ours[ourIndex].Value < theirSeq)
                {
                    return false;
                }

                ourIndex++;
                break;
            }
        }

        return true;
    }

    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var size = GetBinarySize();
        var span = writer.GetSpan(size);
        var written = WriteTo(span);
        writer.Advance(written);
    }

    public string Encode()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        if (Volatile.Read(ref _encoded) is { } cached)
        {
            return cached;
        }

        var size = GetBinarySize();
        string encoded;
        if (size <= 512)
        {
            Span<byte> buffer = stackalloc byte[512];
            var written = WriteTo(buffer);
            encoded = Base64Url.EncodeToString(buffer[..written]);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                var written = WriteTo(rented);
                encoded = Base64Url.EncodeToString(rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        Volatile.Write(ref _encoded, encoded);
        return encoded;
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

        if (count == 0)
        {
            source = cursor;
            return true;
        }

        var entries = new KeyValuePair<string, long>[(int)count];
        for (var index = 0; index < entries.Length; index++)
        {
            if (!TryReadVarUInt(ref cursor, out var length) || length == 0 || length > MaxRegionBytes || length > (ulong)cursor.Length)
            {
                return false;
            }

            var regionBytes = cursor[..(int)length];
            if (!TryDecodeRegion(regionBytes, out var region))
            {
                return false;
            }

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
        bookmark = new CausalBookmark(entries);
        return true;
    }

    public bool Equals(CausalBookmark? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

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

    private int WriteTo(Span<byte> destination)
    {
        var offset = 0;
        destination[offset++] = FormatVersion;
        var entries = _entries;
        offset += WriteVarUInt(destination[offset..], (ulong)entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var (region, sequence) = entries[index];
            var length = region.Length;
            if (length < 0x80 && Ascii.FromUtf16(region, destination.Slice(offset + 1, length), out _) == OperationStatus.Done)
            {
                destination[offset] = (byte)length;
                offset += 1 + length;
            }
            else
            {
                var byteCount = Encoding.UTF8.GetByteCount(region);
                offset += WriteVarUInt(destination[offset..], (ulong)byteCount);
                offset += Encoding.UTF8.GetBytes(region, destination.Slice(offset, byteCount));
            }

            offset += WriteVarUInt(destination[offset..], (ulong)sequence);
        }

        return offset;
    }

    private static bool TryDecodeRegion(ReadOnlySpan<byte> regionBytes, out string region)
    {
        if (regionBytes.Length <= 8 && TryPackShortAscii(regionBytes, out var packed))
        {
            if (ShortRegionCache.TryGetValue(packed, out var cached))
            {
                region = cached;
                return true;
            }

            region = Encoding.ASCII.GetString(regionBytes);
            if (ShortRegionCache.Count < MaxInternedRegions)
            {
                _ = ShortRegionCache.TryAdd(packed, region);
            }

            return true;
        }

        if (Ascii.IsValid(regionBytes))
        {
            region = Encoding.ASCII.GetString(regionBytes);
            return true;
        }

        if (!Utf8.IsValid(regionBytes))
        {
            region = string.Empty;
            return false;
        }

        region = Encoding.UTF8.GetString(regionBytes);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryPackShortAscii(ReadOnlySpan<byte> bytes, out ulong packed)
    {
        ulong value = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            var current = bytes[index];
            if (current == 0 || current >= 0x80)
            {
                packed = 0;
                return false;
            }

            value |= (ulong)current << (index * 8);
        }

        packed = value;
        return true;
    }

    private static CausalBookmark CreateFromOrdinalDictionary(Dictionary<string, long> dictionary)
    {
        if (dictionary.Count == 0)
        {
            return Empty;
        }

        var count = 0;
        foreach (var (region, sequence) in dictionary)
        {
            ValidateEntry(region, sequence);
            if (sequence > 0)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return Empty;
        }

        var result = new KeyValuePair<string, long>[count];
        var index = 0;
        foreach (var pair in dictionary)
        {
            if (pair.Value > 0)
            {
                result[index++] = pair;
            }
        }

        if (result.Length > 1)
        {
            Array.Sort(result, EntryComparison);
        }

        return new CausalBookmark(result);
    }

    private static CausalBookmark CreateFromSpan(ReadOnlySpan<KeyValuePair<string, long>> entries)
    {
        if (entries.IsEmpty)
        {
            return Empty;
        }

        if (entries.Length == 1)
        {
            var entry = entries[0];
            ValidateEntry(entry.Key, entry.Value);
            return entry.Value == 0 ? Empty : new CausalBookmark([entry]);
        }

        var strictlySortedNonZero = true;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            ValidateEntry(entry.Key, entry.Value);
            if (entry.Value == 0 || (index > 0 && string.CompareOrdinal(entries[index - 1].Key, entry.Key) >= 0))
            {
                strictlySortedNonZero = false;
            }
        }

        if (strictlySortedNonZero)
        {
            return new CausalBookmark(entries.ToArray());
        }

        Dictionary<string, long> merged = new(entries.Length, StringComparer.Ordinal);
        for (var index = 0; index < entries.Length; index++)
        {
            var (region, sequence) = entries[index];
            if (sequence == 0)
            {
                continue;
            }

            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(merged, region, out _);
            slot = Math.Max(slot, sequence);
        }

        return CreateFromValidatedDictionary(merged);
    }

    private static CausalBookmark CreateFromValidatedDictionary(Dictionary<string, long> merged)
    {
        if (merged.Count == 0)
        {
            return Empty;
        }

        var result = new KeyValuePair<string, long>[merged.Count];
        var index = 0;
        foreach (var pair in merged)
        {
            result[index++] = pair;
        }

        if (result.Length > 1)
        {
            Array.Sort(result, EntryComparison);
        }

        return new CausalBookmark(result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateEntry(string region, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Encoding.UTF8.GetByteCount(region), MaxRegionBytes, nameof(region));
        ArgumentOutOfRangeException.ThrowIfNegative(sequence, nameof(sequence));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteVarUInt(Span<byte> destination, ulong value)
    {
        if (value < 0x80)
        {
            destination[0] = (byte)value;
            return 1;
        }

        var written = 0;
        while (value >= 0x80)
        {
            destination[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[written++] = (byte)value;
        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int VarUIntSize(ulong value) => value < 0x80 ? 1 : 1 + (BitOperations.Log2(value) / 7);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadVarUInt(ref ReadOnlySpan<byte> source, out ulong value)
    {
        if (!source.IsEmpty)
        {
            var first = source[0];
            if (first < 0x80)
            {
                source = source[1..];
                value = first;
                return true;
            }
        }

        return TryReadVarUIntSlow(ref source, out value);
    }

    private static bool TryReadVarUIntSlow(ref ReadOnlySpan<byte> source, out ulong value)
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

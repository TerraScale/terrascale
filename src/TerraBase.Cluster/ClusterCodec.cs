using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal static class ClusterCodec
{
    private const int BookmarkStackLimit = 512;
    private const int FastBookmarkEntryLimit = 32;
    private const int StringPoolMask = 127;
    private const int MaxPooledStringBytes = 64;

    private static readonly PooledUtf8String?[] StringPool = new PooledUtf8String?[StringPoolMask + 1];

    extension(IBufferWriter<byte> writer)
    {
        public void WriteHlc(HlcTimestamp value)
        {
            var span = writer.GetSpan(12);
            BinaryPrimitives.WriteInt64LittleEndian(span, value.WallMs);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], value.Logical);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], value.NodeId);
            writer.Advance(12);
        }

        public void WriteBookmark(CausalBookmark bookmark)
        {
            var entries = bookmark.Entries;
            var count = entries.Count;
            if (count == 0)
            {
                writer.WriteUInt8(0);
                return;
            }

            var maxSize = EstimateBookmarkSize(entries);
            var span = writer.GetSpan(maxSize);
            var written = WriteBookmarkToSpan(span, entries);
            writer.Advance(written);
        }

        public void WriteMode(ConsistencyMode mode) => writer.WriteUInt8(EncodeMode(mode));

        public void WriteCommit(CommitMetadata commit)
        {
            var maxSize = EstimateCommitSize(commit);
            var span = writer.GetSpan(maxSize);
            var written = WriteCommitToSpan(span, commit);
            writer.Advance(written);
        }

        public void WriteCommits(IReadOnlyList<CommitMetadata> commits)
        {
            var count = commits.Count;
            if (count == 0)
            {
                writer.WriteUInt8(0);
                return;
            }

            var totalMax = EstimateCommitsSize(commits);
            var span = writer.GetSpan(totalMax);
            var written = WriteCommitsToSpan(span, commits);
            writer.Advance(written);
        }

        public void WriteRowKey(RowKey key)
        {
            writer.WriteString(key.Table);
            writer.WriteString(key.PrimaryKey);
        }

        public void WriteRowKeys(IReadOnlyList<RowKey> keys)
        {
            var count = keys.Count;
            writer.WriteVarUInt64((ulong)count);
            for (var index = 0; index < count; index++)
            {
                writer.WriteRowKey(keys[index]);
            }
        }

        public void WriteReadKeys(IReadOnlyList<ReadKey> keys)
        {
            var count = keys.Count;
            writer.WriteVarUInt64((ulong)count);
            for (var index = 0; index < count; index++)
            {
                var key = keys[index];
                writer.WriteRowKey(key.Key);
                writer.WriteHlc(key.Observed);
            }
        }
    }

    public static void WriteDeltaPush(IBufferWriter<byte> writer, Ulid databaseId, string senderNodeId, IReadOnlyList<CommitMetadata> commits, bool ackRequested)
    {
        var maxSize = 16 + 5 + (senderNodeId.Length * 3) + EstimateCommitsSize(commits) + 1;
        var span = writer.GetSpan(maxSize);
        if (!databaseId.TryWriteBytes(span))
        {
            throw new UnreachableException();
        }

        var offset = 16;
        offset += WriteStringToSpan(span[offset..], senderNodeId);
        offset += WriteCommitsToSpan(span[offset..], commits);
        span[offset++] = ackRequested ? (byte)1 : (byte)0;
        writer.Advance(offset);
    }

    public static string ReadPooledString(ref WireReader reader)
    {
        var bytes = reader.ReadBytesSpan();
        return DecodePooledString(bytes);
    }

    public static string DecodePooledString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var length = bytes.Length;
        if (length > MaxPooledStringBytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        var hash = ((uint)length * 31u) ^ bytes[0] ^ ((uint)bytes[length - 1] << 5) ^ ((uint)bytes[length >> 1] << 2);
        var slot = (int)(hash & StringPoolMask);
        var cached = Volatile.Read(ref StringPool[slot]);
        if (cached is not null && cached.Utf8.AsSpan().SequenceEqual(bytes))
        {
            return cached.Value;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        Volatile.Write(ref StringPool[slot], new PooledUtf8String(bytes.ToArray(), decoded));
        return decoded;
    }

    public static HlcTimestamp ReadHlc(ref WireReader reader)
    {
        var hlc = ReadCommitHlc(ref reader);
        if (!hlc.IsValid)
        {
            reader.MarkFaulted();
        }

        return hlc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HlcTimestamp ReadCommitHlc(ref WireReader reader)
    {
        var wall = reader.ReadInt64();
        var logical = reader.ReadUInt16();
        var node = reader.ReadUInt16();
        return new HlcTimestamp(wall, logical, node);
    }

    public static CausalBookmark ReadBookmark(ref WireReader reader)
    {
        var count = reader.ReadLength();
        if (count == 0)
        {
            return CausalBookmark.Empty;
        }

        if (count <= FastBookmarkEntryLimit)
        {
            return ReadBookmarkFast(ref reader, count);
        }

        return ReadBookmarkSlow(ref reader, count, null);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ConsistencyMode ReadMode(ref WireReader reader) => reader.ReadUInt8() == 1 ? ConsistencyMode.Strong : ConsistencyMode.Fast;

    public static CommitMetadata ReadCommit(ref WireReader reader)
    {
        var txId = reader.ReadUlid();
        var origin = ReadPooledString(ref reader);
        var sequence = (long)Math.Min(reader.ReadVarUInt64(), long.MaxValue);
        var hlc = ReadCommitHlc(ref reader);
        var epoch = reader.ReadVarUInt64();
        var mode = ReadMode(ref reader);
        var bookmark = ReadBookmark(ref reader);
        var payload = reader.ReadBytes();
        return new CommitMetadata(txId, origin, sequence, hlc, epoch, mode, bookmark, payload);
    }

    public static IReadOnlyList<CommitMetadata> ReadCommits(ref WireReader reader)
    {
        var count = reader.ReadLength();
        if (count == 0)
        {
            return [];
        }

        var commits = new CommitMetadata[count];
        var read = 0;
        for (; read < count && !reader.IsFaulted; read++)
        {
            commits[read] = ReadCommit(ref reader);
        }

        return read == count ? commits : commits[..read];
    }

    public static RowKey ReadRowKey(ref WireReader reader)
    {
        var table = ReadPooledString(ref reader);
        var primaryKey = reader.ReadString();
        return new RowKey(table, primaryKey);
    }

    public static IReadOnlyList<RowKey> ReadRowKeys(ref WireReader reader)
    {
        var count = reader.ReadLength();
        if (count == 0)
        {
            return [];
        }

        var keys = new RowKey[count];
        var read = 0;
        for (; read < count && !reader.IsFaulted; read++)
        {
            keys[read] = ReadRowKey(ref reader);
        }

        return read == count ? keys : keys[..read];
    }

    public static IReadOnlyList<ReadKey> ReadReadKeys(ref WireReader reader)
    {
        var count = reader.ReadLength();
        if (count == 0)
        {
            return [];
        }

        var keys = new ReadKey[count];
        var read = 0;
        for (; read < count && !reader.IsFaulted; read++)
        {
            var key = ReadRowKey(ref reader);
            keys[read] = new ReadKey(key, ReadHlc(ref reader));
        }

        return read == count ? keys : keys[..read];
    }

    internal static int EstimateCommitSize(CommitMetadata commit) =>
        16 + 5 + (commit.OriginRegion.Length * 3) + 10 + 12 + 10 + 1 + EstimateBookmarkSize(commit.Bookmark.Entries) + 5 + commit.DeltaPayload.Length;

    internal static int EstimateCommitsSize(IReadOnlyList<CommitMetadata> commits)
    {
        var total = 5;
        if (commits is CommitMetadata[] array)
        {
            for (var index = 0; index < array.Length; index++)
            {
                total += EstimateCommitSize(array[index]);
            }

            return total;
        }

        for (var index = 0; index < commits.Count; index++)
        {
            total += EstimateCommitSize(commits[index]);
        }

        return total;
    }

    internal static int WriteCommitsToSpan(Span<byte> destination, IReadOnlyList<CommitMetadata> commits)
    {
        var count = commits.Count;
        var offset = WriteVarUInt64ToSpan(destination, (ulong)count);
        if (commits is CommitMetadata[] array)
        {
            for (var index = 0; index < array.Length; index++)
            {
                offset += WriteCommitToSpan(destination[offset..], array[index]);
            }

            return offset;
        }

        for (var index = 0; index < count; index++)
        {
            offset += WriteCommitToSpan(destination[offset..], commits[index]);
        }

        return offset;
    }

    internal static int WriteCommitToSpan(Span<byte> destination, CommitMetadata commit)
    {
        var offset = 0;
        if (!commit.TxId.TryWriteBytes(destination))
        {
            throw new UnreachableException();
        }

        offset += 16;
        offset += WriteStringToSpan(destination[offset..], commit.OriginRegion);
        offset += WriteVarUInt64ToSpan(destination[offset..], (ulong)commit.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], commit.Hlc.WallMs);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(offset + 8)..], commit.Hlc.Logical);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(offset + 10)..], commit.Hlc.NodeId);
        offset += 12;
        offset += WriteVarUInt64ToSpan(destination[offset..], commit.SchemaEpoch);
        destination[offset++] = EncodeMode(commit.Mode);
        offset += WriteBookmarkToSpan(destination[offset..], commit.Bookmark.Entries);
        var payload = commit.DeltaPayload.Span;
        offset += WriteVarUInt64ToSpan(destination[offset..], (ulong)payload.Length);
        if (!payload.IsEmpty)
        {
            payload.CopyTo(destination[offset..]);
            offset += payload.Length;
        }

        return offset;
    }

    private static CausalBookmark ReadBookmarkFast(ref WireReader reader, int count)
    {
        Span<byte> scratch = stackalloc byte[BookmarkStackLimit];
        scratch[0] = 1;
        scratch[1] = (byte)count;
        var offset = 2;
        var prevOffset = 0;
        var prevLength = 0;

        for (var index = 0; index < count && !reader.IsFaulted; index++)
        {
            var regionBytes = reader.ReadBytesSpan();
            var sequence = reader.ReadVarUInt64();
            var regionLen = regionBytes.Length;
            if (regionLen is 0 or > CausalBookmark.MaxRegionBytes
                || sequence is 0 or > long.MaxValue
                || offset + 1 + regionLen + 10 > BookmarkStackLimit)
            {
                var fallback = CollectExistingFromScratch(scratch[..offset], index);
                AddIfValid(fallback, regionBytes, sequence);
                return ReadBookmarkSlow(ref reader, count - index - 1, fallback);
            }

            var writeStart = offset + 1;
            var allPrintableAscii = true;
            for (var byteIndex = 0; byteIndex < regionLen; byteIndex++)
            {
                var b = regionBytes[byteIndex];
                if (b is < 0x21 or > 0x7E)
                {
                    allPrintableAscii = false;
                    break;
                }

                scratch[writeStart + byteIndex] = b;
            }

            if (!allPrintableAscii || (index > 0 && regionBytes.SequenceCompareTo(scratch.Slice(prevOffset, prevLength)) <= 0))
            {
                var fallback = CollectExistingFromScratch(scratch[..offset], index);
                AddIfValid(fallback, regionBytes, sequence);
                return ReadBookmarkSlow(ref reader, count - index - 1, fallback);
            }

            scratch[offset] = (byte)regionLen;
            prevOffset = writeStart;
            prevLength = regionLen;
            offset = writeStart + regionLen;
            if (sequence < 0x80)
            {
                scratch[offset++] = (byte)sequence;
            }
            else
            {
                offset += WriteVarUInt64ToSpan(scratch[offset..], sequence);
            }
        }

        if (reader.IsFaulted)
        {
            return CausalBookmark.Empty;
        }

        ReadOnlySpan<byte> encoded = scratch[..offset];
        return CausalBookmark.TryRead(ref encoded, out var bookmark) ? bookmark : CausalBookmark.Empty;
    }

    private static List<KeyValuePair<string, long>> CollectExistingFromScratch(ReadOnlySpan<byte> scratch, int parsedCount)
    {
        List<KeyValuePair<string, long>> entries = new(parsedCount + 1);
        var span = scratch[1..];
        _ = ReadVarUIntSpan(ref span);
        for (var index = 0; index < parsedCount; index++)
        {
            var len = (int)ReadVarUIntSpan(ref span);
            var region = DecodePooledString(span[..len]);
            span = span[len..];
            var sequence = (long)ReadVarUIntSpan(ref span);
            entries.Add(new(region, sequence));
        }

        return entries;
    }

    private static CausalBookmark ReadBookmarkSlow(ref WireReader reader, int remainingCount, List<KeyValuePair<string, long>>? existing)
    {
        var entries = existing ?? new List<KeyValuePair<string, long>>(remainingCount);
        for (var index = 0; index < remainingCount && !reader.IsFaulted; index++)
        {
            var regionBytes = reader.ReadBytesSpan();
            var sequence = reader.ReadVarUInt64();
            AddIfValid(entries, regionBytes, sequence);
        }

        return CausalBookmark.Create(entries);
    }

    private static void AddIfValid(List<KeyValuePair<string, long>> entries, ReadOnlySpan<byte> regionBytes, ulong sequence)
    {
        if (regionBytes.IsEmpty || regionBytes.Length > CausalBookmark.MaxRegionBytes || sequence > long.MaxValue)
        {
            return;
        }

        var region = DecodePooledString(regionBytes);
        if (!string.IsNullOrWhiteSpace(region))
        {
            entries.Add(new(region, (long)sequence));
        }
    }

    private static int EstimateBookmarkSize(IReadOnlyList<KeyValuePair<string, long>> entries) =>
        5 + (entries.Count * (CausalBookmark.MaxRegionBytes + 12));

    private static int WriteBookmarkToSpan(Span<byte> destination, IReadOnlyList<KeyValuePair<string, long>> entries)
    {
        if (entries is KeyValuePair<string, long>[] array)
        {
            var offset = WriteVarUInt64ToSpan(destination, (ulong)array.Length);
            for (var index = 0; index < array.Length; index++)
            {
                var entry = array[index];
                offset += WriteStringToSpan(destination[offset..], entry.Key);
                offset += WriteVarUInt64ToSpan(destination[offset..], (ulong)entry.Value);
            }

            return offset;
        }

        var written = WriteVarUInt64ToSpan(destination, (ulong)entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            written += WriteStringToSpan(destination[written..], entry.Key);
            written += WriteVarUInt64ToSpan(destination[written..], (ulong)entry.Value);
        }

        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteStringToSpan(Span<byte> destination, string value)
    {
        var length = value.Length;
        if (length == 0)
        {
            destination[0] = 0;
            return 1;
        }

        if (length < 128)
        {
            var allAscii = true;
            for (var index = 0; index < length; index++)
            {
                var ch = value[index];
                if (ch > 0x7F)
                {
                    allAscii = false;
                    break;
                }

                destination[1 + index] = (byte)ch;
            }

            if (allAscii)
            {
                destination[0] = (byte)length;
                return 1 + length;
            }
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        var header = WriteVarUInt64ToSpan(destination, (ulong)byteCount);
        var written = Encoding.UTF8.GetBytes(value, destination[header..]);
        return header + written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteVarUInt64ToSpan(Span<byte> destination, ulong value)
    {
        if (value < 0x80)
        {
            destination[0] = (byte)value;
            return 1;
        }

        var count = 0;
        while (value >= 0x80)
        {
            destination[count++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[count++] = (byte)value;
        return count;
    }

    private static ulong ReadVarUIntSpan(ref ReadOnlySpan<byte> source)
    {
        ulong result = 0;
        var shift = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                source = source[(index + 1)..];
                return result;
            }

            shift += 7;
        }

        source = [];
        return result;
    }

    private static byte EncodeMode(ConsistencyMode mode) => mode switch
    {
        ConsistencyMode.Fast => 0,
        ConsistencyMode.Strong => 1,
        _ => throw new UnreachableException(),
    };

    private sealed record PooledUtf8String(byte[] Utf8, string Value);
}

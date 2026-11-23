using System.Buffers;
using System.Diagnostics;
using System.Text;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal static class ClusterCodec
{
    extension(IBufferWriter<byte> writer)
    {
        public void WriteHlc(HlcTimestamp value)
        {
            writer.WriteInt64(value.WallMs);
            writer.WriteUInt16(value.Logical);
            writer.WriteUInt16(value.NodeId);
        }

        public void WriteBookmark(CausalBookmark bookmark)
        {
            writer.WriteVarUInt64((ulong)bookmark.Entries.Count);
            foreach (var (region, sequence) in bookmark.Entries)
            {
                writer.WriteString(region);
                writer.WriteVarUInt64((ulong)sequence);
            }
        }

        public void WriteMode(ConsistencyMode mode) => writer.WriteUInt8(mode switch
        {
            ConsistencyMode.Fast => 0,
            ConsistencyMode.Strong => 1,
            _ => throw new UnreachableException(),
        });

        public void WriteCommit(CommitMetadata commit)
        {
            writer.WriteUlid(commit.TxId);
            writer.WriteString(commit.OriginRegion);
            writer.WriteVarUInt64((ulong)commit.Sequence);
            writer.WriteHlc(commit.Hlc);
            writer.WriteVarUInt64(commit.SchemaEpoch);
            writer.WriteMode(commit.Mode);
            writer.WriteBookmark(commit.Bookmark);
            writer.WriteBytes(commit.DeltaPayload.Span);
        }

        public void WriteCommits(IReadOnlyList<CommitMetadata> commits)
        {
            writer.WriteVarUInt64((ulong)commits.Count);
            foreach (var commit in commits)
            {
                writer.WriteCommit(commit);
            }
        }

        public void WriteRowKey(RowKey key)
        {
            writer.WriteString(key.Table);
            writer.WriteString(key.PrimaryKey);
        }

        public void WriteRowKeys(IReadOnlyList<RowKey> keys)
        {
            writer.WriteVarUInt64((ulong)keys.Count);
            foreach (var key in keys)
            {
                writer.WriteRowKey(key);
            }
        }

        public void WriteReadKeys(IReadOnlyList<ReadKey> keys)
        {
            writer.WriteVarUInt64((ulong)keys.Count);
            foreach (var key in keys)
            {
                writer.WriteRowKey(key.Key);
                writer.WriteHlc(key.Observed);
            }
        }
    }

    public static HlcTimestamp ReadHlc(ref WireReader reader)
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

        List<KeyValuePair<string, long>> entries = new(count);
        for (var index = 0; index < count && !reader.IsFaulted; index++)
        {
            var region = reader.ReadString();
            var sequence = reader.ReadVarUInt64();
            if (region.Length > 0 && !string.IsNullOrWhiteSpace(region) && Encoding.UTF8.GetByteCount(region) <= CausalBookmark.MaxRegionBytes && sequence <= long.MaxValue)
            {
                entries.Add(new(region, (long)sequence));
            }
        }

        return CausalBookmark.Create(entries);
    }

    public static ConsistencyMode ReadMode(ref WireReader reader) => reader.ReadUInt8() == 1 ? ConsistencyMode.Strong : ConsistencyMode.Fast;

    public static CommitMetadata ReadCommit(ref WireReader reader)
    {
        var txId = reader.ReadUlid();
        var origin = reader.ReadString();
        var sequence = (long)Math.Min(reader.ReadVarUInt64(), long.MaxValue);
        var hlc = ReadHlc(ref reader);
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

        var commits = new List<CommitMetadata>(count);
        for (var index = 0; index < count && !reader.IsFaulted; index++)
        {
            commits.Add(ReadCommit(ref reader));
        }

        return commits;
    }

    public static RowKey ReadRowKey(ref WireReader reader)
    {
        var table = reader.ReadString();
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

        var keys = new List<RowKey>(count);
        for (var index = 0; index < count && !reader.IsFaulted; index++)
        {
            keys.Add(ReadRowKey(ref reader));
        }

        return keys;
    }

    public static IReadOnlyList<ReadKey> ReadReadKeys(ref WireReader reader)
    {
        var count = reader.ReadLength();
        if (count == 0)
        {
            return [];
        }

        var keys = new List<ReadKey>(count);
        for (var index = 0; index < count && !reader.IsFaulted; index++)
        {
            var key = ReadRowKey(ref reader);
            keys.Add(new ReadKey(key, ReadHlc(ref reader)));
        }

        return keys;
    }
}

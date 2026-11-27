using System.Buffers.Binary;
using System.IO.Hashing;
using DotNext.Net.Cluster;
using Microsoft.Win32.SafeHandles;

namespace TerraBase.Cluster;

internal static class RaftWalCodec
{
    public const uint Magic = 0x54524657;
    public const int FormatVersion = 1;
    public const int FileHeaderSize = 8;
    public const int RecordHeaderSize = 5;
    public const int ChecksumSize = 4;
    public const byte RecordTermVote = 1;
    public const byte RecordAppend = 2;
    public const byte RecordTruncate = 3;
    public const byte RecordSnapshot = 4;

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static SafeFileHandle OpenWalFile(string path)
    {
        var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.WriteThrough);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateFile);
        }

        return handle;
    }

    public static SafeFileHandle OpenNewWalFile(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateFile);
        }

        return handle;
    }

    public static void WriteFileHeader(SafeFileHandle handle)
    {
        Span<byte> header = stackalloc byte[FileHeaderSize];
        _ = WriteHeaderToSpan(header);
        RandomAccess.Write(handle, header, 0);
        RandomAccess.FlushToDisk(handle);
    }

    public static int WriteHeaderToSpan(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], FormatVersion);
        return FileHeaderSize;
    }

    public static int RecordFrameSize(int payloadLength) => RecordHeaderSize + payloadLength + ChecksumSize;

    public static int FinishRecord(Span<byte> destination, byte kind, int payloadLength)
    {
        destination[0] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(destination[1..], payloadLength);
        var checksum = XxHash32.HashToUInt32(destination[..(RecordHeaderSize + payloadLength)]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(RecordHeaderSize + payloadLength)..], checksum);
        return RecordHeaderSize + payloadLength + ChecksumSize;
    }

    public static int WriteTermVoteRecord(Span<byte> destination, long term, bool hasVote, ClusterMemberId votedFor)
    {
        var body = destination[RecordHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(body, term);
        body[8] = hasVote ? (byte)1 : (byte)0;
        var payloadLen = 9;
        if (hasVote)
        {
            votedFor.Format(body.Slice(9, ClusterMemberId.Size));
            payloadLen += ClusterMemberId.Size;
        }

        return FinishRecord(destination, RecordTermVote, payloadLen);
    }

    public static int WriteTruncateRecord(Span<byte> destination, long newLastIndex)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination[RecordHeaderSize..], newLastIndex);
        return FinishRecord(destination, RecordTruncate, 8);
    }

    public static int WriteSnapshotRecord(Span<byte> destination, long snapshotIndex, long snapshotTerm)
    {
        var body = destination[RecordHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(body, snapshotIndex);
        BinaryPrimitives.WriteInt64LittleEndian(body[8..], snapshotTerm);
        return FinishRecord(destination, RecordSnapshot, 16);
    }

    public static int WriteAppendRecord(Span<byte> destination, long logIndex, RaftLogEntryData entry)
    {
        var body = destination[RecordHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(body, logIndex);
        BinaryPrimitives.WriteInt64LittleEndian(body[8..], entry.Term);
        body[16] = (byte)((entry.CommandId.HasValue ? 1 : 0) | (entry.IsConfiguration ? 2 : 0));
        BinaryPrimitives.WriteInt32LittleEndian(body[17..], entry.CommandId.GetValueOrDefault());
        var payload = entry.Payload.Span;
        if (!payload.IsEmpty)
        {
            payload.CopyTo(body[21..]);
        }

        return FinishRecord(destination, RecordAppend, 21 + payload.Length);
    }
}

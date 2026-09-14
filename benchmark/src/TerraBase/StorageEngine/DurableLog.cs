using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TerraBase.StorageEngine;

/// <summary>A checksummed append-only log used by a storage engine.</summary>
public interface IDurableLog : IAsyncDisposable
{
    /// <summary>The number of bytes currently occupied by the log.</summary>
    long Length { get; }

    /// <summary>Appends one atomic frame containing the supplied records.</summary>
    ValueTask AppendAsync(
        IReadOnlyList<StorageLogRecord> records,
        StorageDurability durability = StorageDurability.Synced,
        CancellationToken cancellationToken = default);

    /// <summary>Reads and verifies every complete frame in append order.</summary>
    IAsyncEnumerable<StorageLogRecord> ReplayAsync(CancellationToken cancellationToken = default);
}

/// <summary>Recovery details returned when a torn log tail is discarded.</summary>
public sealed record StorageLogRecovery(long VerifiedBytes, long DiscardedBytes);

/// <summary>A filesystem backed, checksummed write-ahead log.</summary>
public sealed class FileDurableLog : IDurableLog
{
    private static readonly ConcurrentDictionary<string, object> PathGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly byte[] FrameMagic = "TBLGF1\0"u8.ToArray();
    private const byte FrameVersion = 1;
    private const int FrameHeaderLength = 7 + 1 + 4 + 4;
    private const int ChecksumLength = 32;
    private const int MaximumFrameBytes = 128 * 1024 * 1024;
    private readonly object gate = new();
    private readonly object pathGate;
    private readonly string path;
    private readonly FileStream stream;
    private bool disposed;

    /// <summary>Opens or creates a log and repairs only a torn trailing frame.</summary>
    /// <param name="path">The log file path.</param>
    public FileDurableLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(this.path)
            ?? throw new IOException("The durable log path has no parent directory.");
        Directory.CreateDirectory(parent);
        pathGate = PathGates.GetOrAdd(this.path, static _ => new object());
        lock (pathGate)
        {
            stream = new FileStream(
                this.path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan | FileOptions.WriteThrough);
            RecoverUnsafe();
            stream.Position = stream.Length;
        }
    }

    /// <summary>The fully qualified log path.</summary>
    public string Path => path;

    /// <inheritdoc />
    public long Length
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return stream.Length;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(
        IReadOnlyList<StorageLogRecord> records,
        StorageDurability durability = StorageDurability.Synced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            throw new ArgumentException("At least one record is required.", nameof(records));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            lock (pathGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = EncodeFrame(records);
                stream.Position = stream.Length;
                stream.Write(frame);
                if (durability == StorageDurability.Synced)
                {
                    stream.Flush(flushToDisk: true);
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StorageLogRecord> ReplayAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StorageLogRecord[] records;
        lock (gate)
        {
            ThrowIfDisposed();
            lock (pathGate)
            {
                records = ReadFramesUnsafe().SelectMany(static frame => frame).ToArray();
            }
        }

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }

            disposed = true;
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static byte[] EncodeFrame(IReadOnlyList<StorageLogRecord> records)
    {
        using var payload = new MemoryStream();
        Span<byte> countBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(countBytes, records.Count);
        payload.Write(countBytes);
        foreach (var record in records)
        {
            var encoded = StorageRecordCodec.Encode(record);
            BinaryPrimitives.WriteInt32LittleEndian(countBytes, encoded.Length);
            payload.Write(countBytes);
            payload.Write(encoded);
        }

        var payloadBytes = payload.ToArray();
        var frame = new byte[FrameHeaderLength + payloadBytes.Length + ChecksumLength];
        FrameMagic.CopyTo(frame, 0);
        frame[7] = FrameVersion;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), records.Count);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), payloadBytes.Length);
        payloadBytes.CopyTo(frame, FrameHeaderLength);
        SHA256.HashData(frame.AsSpan(0, frame.Length - ChecksumLength), frame.AsSpan(frame.Length - ChecksumLength));
        return frame;
    }

    private StorageLogRecord[][] ReadFramesUnsafe()
    {
        stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var bytes = copy.ToArray();
        var frames = new List<StorageLogRecord[]>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < FrameHeaderLength)
            {
                throw new InvalidDataException("The durable log has an incomplete frame header.");
            }

            if (!bytes.AsSpan(offset, FrameMagic.Length).SequenceEqual(FrameMagic)
                || bytes[offset + 7] != FrameVersion)
            {
                throw new InvalidDataException("The durable log contains an invalid frame header.");
            }

            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 8));
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12));
            if (count is < 1 or > 1_000_000 || payloadLength is < 4 or > MaximumFrameBytes)
            {
                throw new InvalidDataException("The durable log contains an invalid frame length.");
            }

            var frameLength = checked(FrameHeaderLength + payloadLength + ChecksumLength);
            if (frameLength > bytes.Length - offset)
            {
                throw new InvalidDataException("The durable log contains an incomplete frame.");
            }

            var frame = bytes.AsSpan(offset, frameLength);
            Span<byte> checksum = stackalloc byte[ChecksumLength];
            SHA256.HashData(frame[..^ChecksumLength], checksum);
            if (!CryptographicOperations.FixedTimeEquals(checksum, frame[^ChecksumLength..]))
            {
                throw new InvalidDataException("The durable log frame checksum does not match.");
            }

            var payload = frame.Slice(FrameHeaderLength, payloadLength);
            var cursor = 4;
            var decoded = new StorageLogRecord[count];
            for (var index = 0; index < count; index++)
            {
                if (cursor > payload.Length - 4)
                {
                    throw new InvalidDataException("The durable log frame record list is truncated.");
                }

                var recordLength = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
                cursor += 4;
                if (recordLength < 1 || recordLength > payload.Length - cursor)
                {
                    throw new InvalidDataException("The durable log frame contains an invalid record length.");
                }

                decoded[index] = StorageRecordCodec.Decode(payload.Slice(cursor, recordLength));
                cursor += recordLength;
            }

            if (cursor != payload.Length)
            {
                throw new InvalidDataException("The durable log frame contains trailing bytes.");
            }

            frames.Add(decoded);
            offset += frameLength;
        }

        return frames.ToArray();
    }

    private StorageLogRecovery RecoverUnsafe()
    {
        stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var bytes = copy.ToArray();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < FrameHeaderLength
                || !bytes.AsSpan(offset, FrameMagic.Length).SequenceEqual(FrameMagic)
                || bytes[offset + 7] != FrameVersion)
            {
                break;
            }

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12));
            var frameLength = payloadLength is >= 4 and <= MaximumFrameBytes
                ? checked(FrameHeaderLength + payloadLength + ChecksumLength)
                : -1;
            if (frameLength < 0 || frameLength > bytes.Length - offset)
            {
                break;
            }

            var frame = bytes.AsSpan(offset, frameLength);
            Span<byte> checksum = stackalloc byte[ChecksumLength];
            SHA256.HashData(frame[..^ChecksumLength], checksum);
            if (!CryptographicOperations.FixedTimeEquals(checksum, frame[^ChecksumLength..]))
            {
                if (offset + frameLength < bytes.Length)
                {
                    throw new InvalidDataException("The durable log contains corruption before its tail.");
                }

                break;
            }

            try
            {
                _ = DecodeFrame(frame);
            }
            catch (InvalidDataException) when (offset + frameLength == bytes.Length)
            {
                break;
            }

            offset += frameLength;
        }

        var discarded = bytes.Length - offset;
        if (discarded > 0)
        {
            stream.SetLength(offset);
            stream.Flush(flushToDisk: true);
        }

        return new StorageLogRecovery(offset, discarded);
    }

    private static StorageLogRecord[] DecodeFrame(ReadOnlySpan<byte> frame)
    {
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame[12..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(frame[8..]);
        var payload = frame.Slice(FrameHeaderLength, payloadLength);
        var cursor = 4;
        var records = new StorageLogRecord[count];
        for (var index = 0; index < count; index++)
        {
            if (cursor > payload.Length - 4)
            {
                throw new InvalidDataException("The durable log frame record list is truncated.");
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            cursor += 4;
            if (length < 1 || length > payload.Length - cursor)
            {
                throw new InvalidDataException("The durable log frame contains an invalid record length.");
            }

            records[index] = StorageRecordCodec.Decode(payload.Slice(cursor, length));
            cursor += length;
        }

        if (cursor != payload.Length)
        {
            throw new InvalidDataException("The durable log frame contains trailing bytes.");
        }

        return records;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TerraBase.StorageEngine;

/// <summary>Writes and verifies immutable storage segments.</summary>
public interface IStorageSegmentStore
{
    /// <summary>Publishes one fully checksummed immutable segment.</summary>
    ValueTask<StorageSegmentMetadata> WriteAsync(
        StorageSegmentDescriptor descriptor,
        IReadOnlyList<StorageLogRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>Reads and verifies every record in a segment.</summary>
    ValueTask<IReadOnlyList<StorageLogRecord>> ReadAsync(
        StorageSegmentDescriptor descriptor,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a segment that is no longer named by the manifest.</summary>
    ValueTask DeleteAsync(StorageSegmentDescriptor descriptor, CancellationToken cancellationToken = default);
}

/// <summary>A local immutable segment store with atomic temporary-file publication.</summary>
public sealed class LocalStorageSegmentStore : IStorageSegmentStore
{
    private static readonly byte[] SegmentMagic = "TBSEG1\0\0"u8.ToArray();
    private const byte SegmentVersion = 1;
    private const int HeaderLength = 8 + 1 + 8;
    private const int MaximumRecordBytes = 64 * 1024 * 1024;
    private readonly IStorageTierPlacement placement;

    /// <summary>Creates a segment store using the supplied placement policy.</summary>
    public LocalStorageSegmentStore(IStorageTierPlacement placement)
    {
        this.placement = placement ?? throw new ArgumentNullException(nameof(placement));
    }

    /// <inheritdoc />
    public async ValueTask<StorageSegmentMetadata> WriteAsync(
        StorageSegmentDescriptor descriptor,
        IReadOnlyList<StorageLogRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            throw new ArgumentException("A segment must contain at least one record.", nameof(records));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var location = placement.Resolve(descriptor);
        var directory = Path.GetDirectoryName(location.Path)
            ?? throw new IOException("The segment location has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = location.Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                var header = new byte[HeaderLength];
                SegmentMagic.CopyTo(header, 0);
                header[8] = SegmentVersion;
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(9), checked((ulong)records.Count));
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var encoded = StorageRecordCodec.Encode(record);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), encoded.Length);
                    await stream.WriteAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, location.Path, overwrite: true);
            var bytes = await File.ReadAllBytesAsync(location.Path, cancellationToken).ConfigureAwait(false);
            return new StorageSegmentMetadata(
                descriptor.Id,
                descriptor.Tier,
                location.Path,
                checked((ulong)records.Count),
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                StorageRecordCodec.CanonicalKey(records.MinBy(static record => record.Key, DocumentKeyComparer.Instance)!.Key),
                StorageRecordCodec.CanonicalKey(records.MaxBy(static record => record.Key, DocumentKeyComparer.Instance)!.Key));
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<StorageLogRecord>> ReadAsync(
        StorageSegmentDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        var location = placement.Resolve(descriptor);
        var bytes = await File.ReadAllBytesAsync(location.Path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < HeaderLength || !bytes.AsSpan(0, SegmentMagic.Length).SequenceEqual(SegmentMagic) || bytes[8] != SegmentVersion)
        {
            throw new InvalidDataException($"Segment {descriptor.Id} has an invalid header.");
        }

        var count = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(9));
        if (count is 0 or > 1_000_000)
        {
            throw new InvalidDataException($"Segment {descriptor.Id} has an invalid record count.");
        }

        var records = new List<StorageLogRecord>(checked((int)count));
        var offset = HeaderLength;
        for (ulong index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > bytes.Length - 4)
            {
                throw new InvalidDataException($"Segment {descriptor.Id} is truncated.");
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (length is < 1 or > MaximumRecordBytes || length > bytes.Length - offset)
            {
                throw new InvalidDataException($"Segment {descriptor.Id} has an invalid record length.");
            }

            records.Add(StorageRecordCodec.Decode(bytes.AsSpan(offset, length)));
            offset += length;
        }

        if (offset != bytes.Length)
        {
            throw new InvalidDataException($"Segment {descriptor.Id} has trailing bytes.");
        }

        return records;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(StorageSegmentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        var path = placement.Resolve(descriptor).Path;
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class DocumentKeyComparer : IComparer<DocumentKey>
    {
        internal static DocumentKeyComparer Instance { get; } = new();

        public int Compare(DocumentKey left, DocumentKey right) =>
            StringComparer.Ordinal.Compare(StorageRecordCodec.CanonicalKey(left), StorageRecordCodec.CanonicalKey(right));
    }
}

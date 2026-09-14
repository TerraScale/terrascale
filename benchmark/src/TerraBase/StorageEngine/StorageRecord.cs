using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraBase.StorageEngine;

/// <summary>The durability requested for one log append.</summary>
public enum StorageDurability
{
    /// <summary>The bytes are written but are not synchronously flushed.</summary>
    Written,

    /// <summary>The bytes are flushed to the operating system before return.</summary>
    Synced,
}

/// <summary>One versioned document mutation in the durable storage log.</summary>
public sealed record StorageLogRecord
{
    private StorageLogRecord(
        ulong sequence,
        DocumentKey key,
        bool tombstone,
        JsonElement value,
        DateTimeOffset timestamp)
    {
        if (sequence == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Storage sequences start at one.");
        }

        if (timestamp.Offset != TimeSpan.Zero)
        {
            timestamp = timestamp.ToUniversalTime();
        }

        if (tombstone && value.ValueKind != JsonValueKind.Undefined)
        {
            throw new ArgumentException("A tombstone cannot contain a document value.", nameof(value));
        }

        if (!tombstone && value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A put record requires a JSON value.", nameof(value));
        }

        Sequence = sequence;
        Key = key;
        Tombstone = tombstone;
        Value = value.Clone();
        Timestamp = timestamp;
    }

    /// <summary>Creates a document put record.</summary>
    public static StorageLogRecord Put(ulong sequence, DocumentKey key, JsonElement value, DateTimeOffset timestamp) =>
        new(sequence, key, false, value, timestamp);

    /// <summary>Creates a document tombstone record.</summary>
    public static StorageLogRecord Tombstone(ulong sequence, DocumentKey key, DateTimeOffset timestamp) =>
        new(sequence, key, true, default, timestamp);

    /// <summary>The monotonically increasing storage sequence.</summary>
    public ulong Sequence { get; }

    /// <summary>The document key affected by this record.</summary>
    public DocumentKey Key { get; }

    /// <summary>Whether this record removes the current document.</summary>
    public bool Tombstone { get; }

    /// <summary>The document body, or an undefined value for a tombstone.</summary>
    public JsonElement Value { get; }

    /// <summary>The UTC time assigned to the mutation.</summary>
    public DateTimeOffset Timestamp { get; }
}

/// <summary>One immutable segment's durable metadata.</summary>
public sealed record StorageSegmentMetadata(
    string Id,
    StorageTier Tier,
    string Path,
    ulong RecordCount,
    long ByteLength,
    string Checksum,
    string? MinimumKey,
    string? MaximumKey);

internal static class StorageRecordCodec
{
    private static readonly byte[] RecordMagic = "TBREC1\0"u8.ToArray();
    private const byte Version = 1;
    private const int PrefixLength = 7 + 1 + 1 + 8 + 8 + 4 + 4;
    private const int ChecksumLength = 32;
    private const int MaximumKeyBytes = 768;
    private const int MaximumValueBytes = 64 * 1024 * 1024;

    internal static byte[] Encode(StorageLogRecord record)
    {
        var keyBytes = Encoding.UTF8.GetBytes(CanonicalKey(record.Key));
        var valueBytes = record.Tombstone
            ? []
            : JsonSerializer.SerializeToUtf8Bytes(record.Value);
        if (keyBytes.Length > MaximumKeyBytes)
        {
            throw new InvalidDataException("The storage key is too large for the durable record format.");
        }

        if (valueBytes.Length > MaximumValueBytes)
        {
            throw new InvalidDataException("The storage value is too large for the durable record format.");
        }

        var bytes = new byte[PrefixLength + keyBytes.Length + valueBytes.Length + ChecksumLength];
        RecordMagic.CopyTo(bytes, 0);
        bytes[7] = Version;
        bytes[8] = record.Tombstone ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(9), record.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(17), record.Timestamp.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(25), keyBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(29), valueBytes.Length);
        keyBytes.CopyTo(bytes, PrefixLength);
        valueBytes.CopyTo(bytes, PrefixLength + keyBytes.Length);
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - ChecksumLength), bytes.AsSpan(bytes.Length - ChecksumLength));
        return bytes;
    }

    internal static StorageLogRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < PrefixLength + ChecksumLength)
        {
            throw new InvalidDataException("The durable record is truncated.");
        }

        if (!bytes[..RecordMagic.Length].SequenceEqual(RecordMagic) || bytes[7] != Version)
        {
            throw new InvalidDataException("The durable record has an unsupported header.");
        }

        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[25..]);
        var valueLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[29..]);
        if (keyLength is < 1 or > MaximumKeyBytes || valueLength is < 0 or > MaximumValueBytes)
        {
            throw new InvalidDataException("The durable record has an invalid payload length.");
        }

        var expectedLength = checked(PrefixLength + keyLength + valueLength + ChecksumLength);
        if (bytes.Length != expectedLength)
        {
            throw new InvalidDataException("The durable record has an invalid frame length.");
        }

        Span<byte> checksum = stackalloc byte[ChecksumLength];
        SHA256.HashData(bytes[..^ChecksumLength], checksum);
        if (!CryptographicOperations.FixedTimeEquals(checksum, bytes[^ChecksumLength..]))
        {
            throw new InvalidDataException("The durable record checksum does not match.");
        }

        string canonicalKey;
        try
        {
            canonicalKey = Encoding.UTF8.GetString(bytes.Slice(PrefixLength, keyLength));
        }
        catch (DecoderFallbackException failure)
        {
            throw new InvalidDataException("The durable record key is not valid UTF-8.", failure);
        }

        var keyParts = canonicalKey.Split('\0');
        if (keyParts.Length != 3)
        {
            throw new InvalidDataException("The durable record key is malformed.");
        }

        DocumentKey key;
        try
        {
            key = new DocumentKey(new TenantId(keyParts[0]), new CollectionId(keyParts[1]), keyParts[2]);
        }
        catch (ArgumentException failure)
        {
            throw new InvalidDataException("The durable record key is invalid.", failure);
        }

        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[9..]);
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[17..]);
        if (sequence == 0 || ticks is < DateTimeOffset.MinValue.Ticks or > DateTimeOffset.MaxValue.Ticks)
        {
            throw new InvalidDataException("The durable record has an invalid sequence or timestamp.");
        }

        DateTimeOffset timestamp;
        try
        {
            timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException failure)
        {
            throw new InvalidDataException("The durable record timestamp is invalid.", failure);
        }

        var tombstone = bytes[8] switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("The durable record has an invalid mutation kind."),
        };

        if (tombstone)
        {
            if (valueLength != 0)
            {
                throw new InvalidDataException("The tombstone carries a value.");
            }

            return StorageLogRecord.Tombstone(sequence, key, timestamp);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes.Slice(PrefixLength + keyLength, valueLength));
            return StorageLogRecord.Put(sequence, key, document.RootElement, timestamp);
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException("The durable record value is not valid JSON.", failure);
        }
    }

    internal static string CanonicalKey(DocumentKey key) =>
        $"{key.Tenant.Value}\0{key.Collection.Value}\0{key.DocumentId}";
}

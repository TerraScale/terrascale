using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Storage;

namespace TerraBase.Continuity;

internal sealed record ArchivedSnapshot(string Key, string Bookmark, ulong SchemaEpoch, long SizeBytes, DateTimeOffset TakenAtUtc);

internal sealed record ArchivedSegment(
    string Key,
    string Origin,
    long FirstSequence,
    long LastSequence,
    long SizeBytes,
    DateTimeOffset SealedAtUtc,
    DateTimeOffset FirstCommittedAtUtc,
    DateTimeOffset LastCommittedAtUtc);

internal sealed record ArchivedOperation(
    string OperationId,
    string Kind,
    string SourceDatabaseId,
    string TargetDatabaseId,
    string State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? FailureMessage);

internal sealed record ArchiveManifest(
    int Version,
    string DatabaseId,
    IReadOnlyList<ArchivedSnapshot> Snapshots,
    IReadOnlyList<ArchivedSegment> Segments,
    IReadOnlyList<ArchivedOperation> Operations,
    DateTimeOffset UpdatedAtUtc)
{
    public const int CurrentVersion = 1;

    public static ArchiveManifest Empty(Ulid databaseId, DateTimeOffset now) =>
        new(CurrentVersion, databaseId.ToString(), [], [], [], now);

    public bool IsEmpty => Snapshots.Count == 0 && Segments.Count == 0;

    public IReadOnlyDictionary<string, long> ArchivedThrough(CausalBookmark snapshotFloor)
    {
        Dictionary<string, long> through = new(snapshotFloor.Entries, StringComparer.Ordinal);
        foreach (var segment in Segments)
        {
            ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(through, segment.Origin, out _);
            if (segment.LastSequence > current)
            {
                current = segment.LastSequence;
            }
        }

        return through;
    }

    public long ArchivedThrough(string origin, CausalBookmark snapshotFloor)
    {
        var through = snapshotFloor.Get(origin);
        for (var index = 0; index < Segments.Count; index++)
        {
            var segment = Segments[index];
            if (string.Equals(segment.Origin, origin, StringComparison.Ordinal) && segment.LastSequence > through)
            {
                through = segment.LastSequence;
            }
        }

        return through;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(ArchiveManifest))]
[JsonSerializable(typeof(PendingArchiveRetirement))]
internal sealed partial class ContinuityJsonContext : JsonSerializerContext;

internal sealed record DecodedSnapshot(ArchivedSnapshot Archived, CausalBookmark Bookmark);

internal sealed record ArchiveManifestCorrupted(string Message, string Code);

internal readonly union ArchiveManifestDecodeOutcome(ArchiveManifest, ArchiveManifestCorrupted);

internal readonly union ArchiveManifestOutcome(ArchiveManifest, ContinuityUnavailable);

internal readonly union ManifestInflateOutcome(ReadOnlyMemory<byte>, ArchiveManifestCorrupted);

internal sealed record PendingArchiveRetirement(string? ManifestETag, IReadOnlyList<string> Keys)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? SnapshotUploadDeadlineUtc { get; init; }
}

internal sealed record ArchiveRetirementCorrupted(string Message);

internal readonly union ArchiveRetirementDecodeOutcome(PendingArchiveRetirement, ArchiveRetirementCorrupted);

internal static class ManifestCodec
{
    public const string JsonContentType = "application/json";
    public const string BinaryContentType = "application/octet-stream";

    private const int MaxManifestBytes = 256 << 20;
    private const int InflateChunkBytes = 1 << 16;
    private const int InitialBufferBytes = 1 << 16;
    private const int RetainedBufferBytes = 8 << 20;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? SerializeBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? SerializeWriter;

    public static byte[] Serialize(ArchiveManifest manifest)
    {
        var buffer = SerializeBuffer ??= new ArrayBufferWriter<byte>(InitialBufferBytes);
        var writer = SerializeWriter ??= new Utf8JsonWriter(buffer);
        writer.Reset(buffer);
        try
        {
            JsonSerializer.Serialize(writer, manifest, ContinuityJsonContext.Default.ArchiveManifest);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.ResetWrittenCount();
            if (buffer.Capacity > RetainedBufferBytes)
            {
                writer.Dispose();
                SerializeBuffer = null;
                SerializeWriter = null;
            }
        }
    }

    public static byte[] Serialize(PendingArchiveRetirement retirement) =>
        JsonSerializer.SerializeToUtf8Bytes(retirement, ContinuityJsonContext.Default.PendingArchiveRetirement);

    public static ArchiveRetirementDecodeOutcome DeserializeRetirement(ReadOnlySpan<byte> json)
    {
        try
        {
            var retirement = JsonSerializer.Deserialize(json, ContinuityJsonContext.Default.PendingArchiveRetirement);
            return retirement switch
            {
                { SnapshotUploadDeadlineUtc: not null, Keys.Count: 1 } => retirement,
                { SnapshotUploadDeadlineUtc: null, ManifestETag.Length: > 0, Keys.Count: > 0 } => retirement,
                _ => new ArchiveRetirementCorrupted("archive retirement record is missing its ETag or keys"),
            };
        }
        catch (JsonException exception)
        {
            return new ArchiveRetirementCorrupted(string.Concat("archive retirement record is unreadable: ", exception.Message));
        }
    }

    public static ArchiveManifestDecodeOutcome Deserialize(Ulid databaseId, ReadOnlyMemory<byte> content)
    {
        if (content.Length >= 2 && content.Span[0] == 0x1F && content.Span[1] == 0x8B)
        {
            var inflated = Inflate(content);
            return inflated switch
            {
                ReadOnlyMemory<byte> json => Decode(databaseId, json.Span),
                ArchiveManifestCorrupted corrupted => corrupted,
            };
        }

        return Decode(databaseId, content.Span);
    }

    private static ManifestInflateOutcome Inflate(ReadOnlyMemory<byte> compressed)
    {
        using var source = MemoryMarshal.TryGetArray(compressed, out var segment) && segment.Array is { } array
            ? new MemoryStream(array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(compressed.ToArray(), writable: false);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        var output = new ArrayBufferWriter<byte>(InflateChunkBytes);
        try
        {
            while (true)
            {
                var read = gzip.Read(output.GetSpan(InflateChunkBytes)[..InflateChunkBytes]);
                if (read == 0)
                {
                    return output.WrittenMemory;
                }

                output.Advance(read);
                if (output.WrittenCount > MaxManifestBytes)
                {
                    return new ArchiveManifestCorrupted("archive manifest exceeds the decompressed size limit", "archive_manifest_corrupted");
                }
            }
        }
        catch (InvalidDataException exception)
        {
            return new ArchiveManifestCorrupted(string.Concat("archive manifest is not valid gzip: ", exception.Message), "archive_manifest_corrupted");
        }
    }

    private static ArchiveManifestDecodeOutcome Decode(Ulid databaseId, ReadOnlySpan<byte> json)
    {
        ArchiveManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(json, ContinuityJsonContext.Default.ArchiveManifest);
        }
        catch (JsonException exception)
        {
            return new ArchiveManifestCorrupted(string.Concat("archive manifest is unreadable: ", exception.Message), "archive_manifest_corrupted");
        }

        if (manifest is not { Version: ArchiveManifest.CurrentVersion } decoded
            || !Ulid.TryParse(decoded.DatabaseId, out var id)
            || id != databaseId
            || decoded.UpdatedAtUtc == DateTimeOffset.MaxValue
            || decoded.Snapshots is null
            || decoded.Segments is null
            || decoded.Operations is null)
        {
            return new ArchiveManifestCorrupted("archive manifest does not describe this database", "archive_manifest_corrupted");
        }

        for (var index = 0; index < decoded.Snapshots.Count; index++)
        {
            var snapshot = decoded.Snapshots[index];
            if (snapshot is null
                || !CausalBookmark.TryDecode(snapshot.Bookmark, out var bookmark)
                || !string.Equals(snapshot.Key, ArchiveKeys.Snapshot(databaseId, snapshot.TakenAtUtc, bookmark), StringComparison.Ordinal))
            {
                return new ArchiveManifestCorrupted("archive manifest does not describe this database", "archive_manifest_corrupted");
            }
        }

        for (var index = 0; index < decoded.Segments.Count; index++)
        {
            var segment = decoded.Segments[index];
            if (segment is null
                || segment.Origin is not { } origin
                || !StorageEngineOptions.IsValidRegion(origin)
                || segment.SizeBytes <= 0
                || segment.FirstSequence <= 0
                || segment.LastSequence < segment.FirstSequence
                || !string.Equals(segment.Key, ArchiveKeys.Segment(databaseId, origin, segment.FirstSequence, segment.LastSequence), StringComparison.Ordinal))
            {
                return new ArchiveManifestCorrupted("archive manifest does not describe this database", "archive_manifest_corrupted");
            }
        }

        return decoded;
    }

    public static IReadOnlyList<DecodedSnapshot> Snapshots(ArchiveManifest manifest)
    {
        if (manifest.Snapshots.Count == 0)
        {
            return [];
        }

        List<DecodedSnapshot> snapshots = new(manifest.Snapshots.Count);
        for (var index = 0; index < manifest.Snapshots.Count; index++)
        {
            var snapshot = manifest.Snapshots[index];
            if (CausalBookmark.TryDecode(snapshot.Bookmark, out var bookmark))
            {
                snapshots.Add(new DecodedSnapshot(snapshot, bookmark));
            }
        }

        return snapshots;
    }

    public static string StateLabel(RestoreOperationState state) => state switch
    {
        RestoreOperationState.Pending => "pending",
        RestoreOperationState.Running => "running",
        RestoreOperationState.Succeeded => "succeeded",
        RestoreOperationState.Failed => "failed",
        _ => throw new UnreachableException(),
    };
}

internal static class ArchiveKeys
{
    public static string DatabasePrefix(Ulid databaseId) =>
        string.Create(CultureInfo.InvariantCulture, $"databases/{databaseId}/");

    public static string Manifest(Ulid databaseId) =>
        string.Create(CultureInfo.InvariantCulture, $"databases/{databaseId}/manifest.json");

    public static string RetirementPrefix(Ulid databaseId) =>
        string.Create(CultureInfo.InvariantCulture, $"databases/{databaseId}/retirements/");

    public static string Snapshot(Ulid databaseId, DateTimeOffset takenAtUtc, CausalBookmark bookmark)
    {
        var encoded = bookmark.Encode();
        var maxBytes = Encoding.UTF8.GetMaxByteCount(encoded.Length);
        byte[]? rented = null;
        var utf8Buffer = maxBytes <= 1024 ? stackalloc byte[maxBytes] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            var written = Encoding.UTF8.GetBytes(encoded, utf8Buffer);
            _ = SHA256.HashData(utf8Buffer[..written], digest);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        Span<char> hashChars = stackalloc char[16];
        _ = Convert.TryToHexStringLower(digest[..8], hashChars, out _);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"databases/{databaseId}/snapshots/{takenAtUtc.ToUnixTimeMilliseconds():D16}-{hashChars}.snap");
    }

    public static string Segment(Ulid databaseId, CommitLogSegment segment) =>
        Segment(databaseId, segment.OriginRegion, segment.FirstSequence, segment.LastSequence);

    public static string Segment(Ulid databaseId, string origin, long firstSequence, long lastSequence) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"databases/{databaseId}/segments/{origin}/{firstSequence:D20}-{lastSequence:D20}.tblog");
}

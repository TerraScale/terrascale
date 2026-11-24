using System.Diagnostics;
using System.Globalization;
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
            through[segment.Origin] = Math.Max(segment.LastSequence, through.GetValueOrDefault(segment.Origin));
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

internal sealed record PendingArchiveRetirement(string ManifestETag, IReadOnlyList<string> Keys);

internal readonly union ArchiveRetirementDecodeOutcome(PendingArchiveRetirement, ContinuityUnavailable);

internal static class ManifestCodec
{
    public const string JsonContentType = "application/json";
    public const string BinaryContentType = "application/octet-stream";

    public static byte[] Serialize(ArchiveManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, ContinuityJsonContext.Default.ArchiveManifest);

    public static byte[] Serialize(PendingArchiveRetirement retirement) =>
        JsonSerializer.SerializeToUtf8Bytes(retirement, ContinuityJsonContext.Default.PendingArchiveRetirement);

    public static ArchiveRetirementDecodeOutcome DeserializeRetirement(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ContinuityJsonContext.Default.PendingArchiveRetirement) is { ManifestETag.Length: > 0, Keys.Count: > 0 } retirement
                ? retirement
                : new ContinuityUnavailable("archive retirement record is missing its ETag or keys", "archive_retirement_corrupted");
        }
        catch (JsonException exception)
        {
            return new ContinuityUnavailable(string.Concat("archive retirement record is unreadable: ", exception.Message), "archive_retirement_corrupted");
        }
    }

    public static ArchiveManifestDecodeOutcome Deserialize(Ulid databaseId, ReadOnlySpan<byte> json)
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
            || decoded.Snapshots is null
            || decoded.Segments is null
            || decoded.Operations is null
            || decoded.Snapshots.Any(snapshot => snapshot is null || !CausalBookmark.TryDecode(snapshot.Bookmark, out var bookmark)
                || snapshot.Key != ArchiveKeys.Snapshot(databaseId, snapshot.TakenAtUtc, bookmark))
            || decoded.Segments.Any(segment => segment is null || segment.Origin is not { } origin || !StorageEngineOptions.IsValidRegion(origin)
                || segment.FirstSequence <= 0 || segment.LastSequence < segment.FirstSequence
                || segment.Key != ArchiveKeys.Segment(databaseId, origin, segment.FirstSequence, segment.LastSequence)))
        {
            return new ArchiveManifestCorrupted("archive manifest does not describe this database", "archive_manifest_corrupted");
        }

        return decoded;
    }

    public static IReadOnlyList<DecodedSnapshot> Snapshots(ArchiveManifest manifest)
    {
        List<DecodedSnapshot> snapshots = [];
        foreach (var snapshot in manifest.Snapshots)
        {
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
    public static string DatabasePrefix(Ulid databaseId) => string.Concat("databases/", databaseId.ToString(), "/");

    public static string Manifest(Ulid databaseId) => string.Concat(DatabasePrefix(databaseId), "manifest.json");

    public static string RetirementPrefix(Ulid databaseId) => string.Concat(DatabasePrefix(databaseId), "retirements/");

    public static string Snapshot(Ulid databaseId, DateTimeOffset takenAtUtc, CausalBookmark bookmark)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bookmark.Encode())).AsSpan(0, 8));
        return string.Concat(
            DatabasePrefix(databaseId),
            "snapshots/",
            takenAtUtc.ToUnixTimeMilliseconds().ToString("D16", CultureInfo.InvariantCulture),
            "-",
            hash,
            ".snap");
    }

    public static string Segment(Ulid databaseId, CommitLogSegment segment) =>
        Segment(databaseId, segment.OriginRegion, segment.FirstSequence, segment.LastSequence);

    public static string Segment(Ulid databaseId, string origin, long firstSequence, long lastSequence) =>
        string.Concat(
            DatabasePrefix(databaseId),
            "segments/",
            origin,
            "/",
            firstSequence.ToString("D20", CultureInfo.InvariantCulture),
            "-",
            lastSequence.ToString("D20", CultureInfo.InvariantCulture),
            ".tblog");
}

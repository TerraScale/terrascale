using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public long ArchivedSegmentThrough(string origin)
    {
        var through = 0L;
        foreach (var segment in Segments)
        {
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
internal sealed partial class ContinuityJsonContext : JsonSerializerContext;

internal sealed record DecodedSnapshot(ArchivedSnapshot Archived, CausalBookmark Bookmark);

internal static class ManifestCodec
{
    public const string JsonContentType = "application/json";
    public const string BinaryContentType = "application/octet-stream";

    public static byte[] Serialize(ArchiveManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, ContinuityJsonContext.Default.ArchiveManifest);

    public static Result<ArchiveManifest, Unavailable> Deserialize(Ulid databaseId, ReadOnlySpan<byte> json)
    {
        ArchiveManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(json, ContinuityJsonContext.Default.ArchiveManifest);
        }
        catch (JsonException exception)
        {
            return new Unavailable(string.Concat("archive manifest is unreadable: ", exception.Message), "archive_manifest_corrupted");
        }

        if (manifest is not { Version: ArchiveManifest.CurrentVersion } decoded
            || !Ulid.TryParse(decoded.DatabaseId, out var id)
            || id != databaseId
            || decoded.Snapshots.Any(static snapshot => !CausalBookmark.TryDecode(snapshot.Bookmark, out _)))
        {
            return new Unavailable("archive manifest does not describe this database", "archive_manifest_corrupted");
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

    public static string Snapshot(Ulid databaseId, CausalBookmark bookmark)
    {
        var sequence = 0L;
        foreach (var (_, value) in bookmark.Entries)
        {
            sequence += value;
        }

        return string.Concat(DatabasePrefix(databaseId), "snapshots/", sequence.ToString("D20", CultureInfo.InvariantCulture), ".snap");
    }

    public static string Segment(Ulid databaseId, CommitLogSegment segment) =>
        string.Concat(
            DatabasePrefix(databaseId),
            "segments/",
            segment.OriginRegion,
            "/",
            segment.FirstSequence.ToString("D20", CultureInfo.InvariantCulture),
            "-",
            segment.LastSequence.ToString("D20", CultureInfo.InvariantCulture),
            ".tblog");
}

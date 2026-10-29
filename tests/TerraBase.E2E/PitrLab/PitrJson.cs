using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.E2E.PitrLab;

/// <summary>A PitrLab job file (snake_case JSON). Each mode reads only its own fields; see <see cref="PitrLabHost"/>.</summary>
internal sealed record PitrJob(
    string Mode,
    string? Root = null,
    string? Bucket = null,
    string? ReadyFile = null,
    string? CaPem = null,
    PitrFaults? Faults = null,
    PitrSource? Source = null,
    IReadOnlyList<string>? Prefixes = null,
    string? Manifest = null,
    string? DatabasePrefix = null,
    string? KeysPrefix = null,
    string? Out = null,
    string? From = null,
    string? To = null,
    PitrMutation? Mutation = null,
    string? DataDirectory = null,
    PitrTargetIdentity? Target = null);

/// <summary>
/// s3-serve fault injection, applied to object PUTs and multipart parts whose key matches <see cref="KeyRegex"/> (all
/// when null). Puts and parts are counted separately; <see cref="PutDelayMs"/> delays both. A burst (PUTs
/// <see cref="PutStoreThenFailFrom"/> to From + Count - 1 stored, then answered 500) outlasts a client's own retries.
/// </summary>
internal sealed record PitrFaults(
    int PutDelayMs = 0, int PutFailEvery = 0, int PutStoreThenFailEvery = 0, string? KeyRegex = null, int PartFailAt = 0, int PartFailEvery = 0,
    int PutStoreThenFailFrom = 0, int PutStoreThenFailCount = 0);

/// <summary>Where objects are read from: <c>s3</c> (credentials from the environment) or <c>dir</c> (a local root).</summary>
internal sealed record PitrSource(string Kind, string? Root = null);

internal sealed record PitrTargetIdentity(string Organization, string Project, string Database, string Name, string Owner);

internal sealed record PitrMutation(string Kind, string Key);

internal sealed record ReadyFile(string Endpoint, string Region, string Bucket, string CaPem, string Log, int Pid);

internal sealed record ManifestEntry(string Key, long Size, string Sha256, string? Etag);

internal sealed record AuditProblem(string Kind, string? Key, string Detail);

internal sealed record SnapshotReport(
    string Key, long AtMs, long Position, long Size, string Sha256, bool Authenticated, string? KeyId, string? Error, long? ChainEnd, string? ChainBreak);

internal sealed record SegmentReport(
    string Key, long Start, long? End, long AtMs, long Size, string Sha256, bool Authenticated, string? KeyId, string? Error, long TrailingBytes,
    long Records, long Upserts, long Deletes, long Commits, long Creates, long Drops, bool EndsOnCommit, string? RecordsError);

internal sealed record GenerationReport(string Generation, long? FirstPosition, long? EndPosition, IReadOnlyList<SnapshotReport> Snapshots, IReadOnlyList<SegmentReport> Segments);

internal sealed record AuditTotals(int Generations, int Segments, int Snapshots, long Bytes, long LogBytes, long Upserts, long Deletes, long Commits, long Creates, long Drops);

internal sealed record DataKeyReport(string Id, bool Unwrapped, int Objects);

internal sealed record AuditReport(
    string Verdict, string DatabasePrefix, string KeysPrefix, IReadOnlyList<AuditProblem> Problems, IReadOnlyList<string> Notes, AuditTotals Totals,
    IReadOnlyList<DataKeyReport> DataKeys, IReadOnlyList<string> UnparseableKeys, IReadOnlyList<GenerationReport> Generations);

internal sealed record ObjectFingerprint(long Size, string Sha256);

/// <summary>
/// An object's sidecar under <c>meta/</c>: its S3 ETag and user metadata (<c>x-amz-meta-*</c>, lowercase names), valid only
/// while the object file keeps this size and modification time.
/// </summary>
internal sealed record ObjectMeta(string ETag, long Size, long ModifiedTicks, IReadOnlyDictionary<string, string> Metadata);

internal sealed record MutationManifest(
    string Kind, string Key, string? NewKey, string From, string To, bool Linked, int Objects, ObjectFingerprint? Before, ObjectFingerprint? After, long? FlippedOffset,
    string Proves);

internal sealed record SelfCheckResult(string Name, bool Ok, string Detail);

internal sealed record PrefixInventory(string Prefix, long Objects, long Bytes, long ListRequests);

internal sealed record InventoryReport(long Objects, long Bytes, long ListRequests, IReadOnlyList<PrefixInventory> Prefixes);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PitrJob))]
[JsonSerializable(typeof(PitrTargetIdentity))]
[JsonSerializable(typeof(ReadyFile))]
[JsonSerializable(typeof(List<ManifestEntry>))]
[JsonSerializable(typeof(AuditReport))]
[JsonSerializable(typeof(MutationManifest))]
[JsonSerializable(typeof(List<SelfCheckResult>))]
[JsonSerializable(typeof(InventoryReport))]
[JsonSerializable(typeof(ObjectMeta))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class PitrJson : JsonSerializerContext
{
    /// <summary>Writes <paramref name="value"/> aside and moves it into place, so a reader never sees half a file.</summary>
    public static async Task WriteAtomicAsync<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var partial = $"{full}.{Ulid.NewUlid()}.partial";
        await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(file, value, type, cancellationToken);
            file.Flush(flushToDisk: true);
        }

        File.Move(partial, full, overwrite: true);
    }
}

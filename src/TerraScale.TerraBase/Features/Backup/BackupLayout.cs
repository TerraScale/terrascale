using System.Globalization;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>A snapshot object: the generation it starts or continues, and the frame position it was taken at.</summary>
/// <param name="Key">The object key.</param>
/// <param name="At">When it was taken.</param>
/// <param name="Generation">Its generation.</param>
/// <param name="Position">Frames of the generation it contains.</param>
internal sealed record SnapshotKey(string Key, DateTimeOffset At, Ulid Generation, long Position);

/// <summary>A log segment object: frames <paramref name="Start"/> onwards of its generation, read by <paramref name="At"/>.</summary>
/// <param name="Key">The object key.</param>
/// <param name="Start">Frames of the generation before it.</param>
/// <param name="At">When its last frame was read.</param>
internal sealed record SegmentKey(string Key, long Start, DateTimeOffset At);

/// <summary>
/// Object keys in the bucket, layout 1 (<c>docs/backup.md</c>). One directory per storage file:
/// <c>v1/&lt;organization&gt;/&lt;project&gt;/&lt;database&gt;/</c>, or <c>v1/_system/</c>. Under it
/// <c>snapshots/&lt;ms&gt;-&lt;generation&gt;-&lt;position&gt;</c> lists every snapshot of every generation in time order, and
/// <c>wal/&lt;generation&gt;/&lt;start&gt;-&lt;ms&gt;</c> the generation's segments in frame order. Wrapped data keys are at
/// <c>v1/keys/&lt;organization&gt;/&lt;project&gt;/&lt;id&gt;</c> (<c>v1/keys/_system/&lt;id&gt;</c>). Times are Unix
/// milliseconds (13 digits), positions 16 digits, so key order is time and frame order.
/// </summary>
internal static class BackupLayout
{
    private const string Version = "v1";
    private const string System = "_system";

    /// <summary>The directory of a storage file: <paramref name="database"/>, or the system database when null.</summary>
    public static string File(TenantContext? database) =>
        database is null ? $"{Version}/{System}" : $"{Version}/{database.OrganizationId}/{database.ProjectId}/{database.DatabaseId}";

    /// <summary>Whose data key encrypts a file: its project's, or the system database's own.</summary>
    public static string KeyScope(TenantContext? database) => database is null ? System : $"{database.OrganizationId}/{database.ProjectId}";

    public static string Snapshots(string file) => $"{file}/snapshots/";

    public static string Snapshot(string file, DateTimeOffset at, Ulid generation, long position) =>
        string.Create(CultureInfo.InvariantCulture, $"{Snapshots(file)}{at.ToUnixTimeMilliseconds():D13}-{generation}-{position:D16}");

    public static string Segments(string file, Ulid generation) => $"{file}/wal/{generation}/";

    public static string Segment(string file, Ulid generation, long start, DateTimeOffset at) =>
        string.Create(CultureInfo.InvariantCulture, $"{Segments(file, generation)}{start:D16}-{at.ToUnixTimeMilliseconds():D13}");

    /// <summary>Lists from the segment that starts at <paramref name="start"/>: every later key sorts after this.</summary>
    public static string SegmentsBefore(string file, Ulid generation, long start) =>
        string.Create(CultureInfo.InvariantCulture, $"{Segments(file, generation)}{start:D16}");

    public static string Data(string scope) => $"{Version}/{scope}/";

    public static string Keys(string scope) => $"{Version}/keys/{scope}/";

    public static string Key(string scope, string id) => Keys(scope) + id;

    public static SnapshotKey? ParseSnapshot(string key)
    {
        var parts = key[(key.LastIndexOf('/') + 1)..].Split('-');
        return parts is [var at, var generation, var position] && Milliseconds(at) is { } time
            && Ulid.TryParse(generation, out var id) && Number(position) is { } frames
            ? new(key, time, id, frames)
            : null;
    }

    public static SegmentKey? ParseSegment(string key)
    {
        var parts = key[(key.LastIndexOf('/') + 1)..].Split('-');
        return parts is [var start, var at] && Number(start) is { } frames && Milliseconds(at) is { } time ? new(key, frames, time) : null;
    }

    private static long? Number(string value) =>
        value.Length == 16 && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static DateTimeOffset? Milliseconds(string value) =>
        value.Length == 13 && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}

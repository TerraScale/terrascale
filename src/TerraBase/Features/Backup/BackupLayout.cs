using System.Globalization;
using TerraBase.Configuration;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

internal sealed record SnapshotKey(string Key, DateTimeOffset At, Ulid Generation, long Position);

internal sealed record SegmentKey(string Key, long Start, DateTimeOffset At);

internal static class BackupLayout
{
    private const string Version = "v1";
    private const string System = "_system";

    public static string File(TenantContext? database, string machineId)
    {
        if (!TerraBaseOptions.IsMachineId(machineId))
        {
            throw new TerraBaseConfigurationException("TS_NODE must be a valid node identity");
        }

        return $"{FileRoot(database)}/nodes/{machineId}";
    }

    public static string FileRoot(TenantContext? database) =>
        database is null ? $"{Version}/{System}" : $"{Version}/{database.OrganizationId}/{database.ProjectId}/{database.DatabaseId}";

    public static string KeyScope(TenantContext? database) => database is null ? System : $"{database.OrganizationId}/{database.ProjectId}";

    public static string Snapshots(string file) => $"{file}/snapshots/";

    public static string Snapshot(string file, DateTimeOffset at, Ulid generation, long position) =>
        string.Create(CultureInfo.InvariantCulture, $"{Snapshots(file)}{at.ToUnixTimeMilliseconds():D13}-{generation}-{position:D16}");

    public static string GenerationProof(string file, Ulid generation) => $"{file}/generations/{generation}";

    public static string Segments(string file, Ulid generation) => $"{file}/wal/{generation}/";

    public static string Segment(string file, Ulid generation, long start, DateTimeOffset at) =>
        string.Create(CultureInfo.InvariantCulture, $"{Segments(file, generation)}{start:D16}-{at.ToUnixTimeMilliseconds():D13}");

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

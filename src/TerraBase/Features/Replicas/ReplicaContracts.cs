using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

public sealed record ReplicaCatalogEntry(
    string OrganizationId, string ProjectId, string DatabaseId, string Name, string HomeRegion, IReadOnlyList<string> ReplicaRegions, long PlacementVersion,
    bool AllRegions = false, DatabaseIdentity? Identity = null);

public sealed record ReplicaCatalogPage(IReadOnlyList<ReplicaCatalogEntry> Databases, string? Next);

internal sealed record ReplicaRevisionNotice(string OrganizationId, string ProjectId, string DatabaseId, long CommittedAt, string? Revision, string? Base)
{
    public long? CdcPosition { get; init; }
}

internal static class ReplicaSubjects
{
    public static string Revision(string replica, string home, string database, int format) =>
        StorageFormat.Marker(format) is { } marker ? $"terrabase.revision.{replica}.{home}.{database}.{marker}" : $"terrabase.revision.{replica}.{home}.{database}";

    public static string Revisions(string replica) => $"terrabase.revision.{replica}.>";

    public static string Held(string home, string database) => $"terrabase.held.{home}.{database}";

    public static string HeldAt(string home) => $"terrabase.held.{home}.*";
}

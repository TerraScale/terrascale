namespace TerraBase.Features.Replicas;

/// <summary>A database homed on the answering node whose placement names the asking region as a replica.</summary>
/// <param name="OrganizationId">Its organization, a ULID.</param>
/// <param name="ProjectId">Its project, a ULID.</param>
/// <param name="DatabaseId">The database, a ULID.</param>
/// <param name="Name">Its name in the project.</param>
/// <param name="HomeRegion">The region that serves its writes.</param>
/// <param name="ReplicaRegions">The regions that hold a replica.</param>
/// <param name="PlacementVersion">Raised by the home on every placement change; the database's epoch.</param>
/// <param name="AllRegions">Every serving region holds a replica.</param>
public sealed record ReplicaCatalogEntry(
    string OrganizationId, string ProjectId, string DatabaseId, string Name, string HomeRegion, IReadOnlyList<string> ReplicaRegions, long PlacementVersion,
    bool AllRegions = false);

/// <summary>One page of <see cref="ReplicaCatalogEntry"/> from one catalog snapshot.</summary>
/// <param name="Databases">At most 100 databases.</param>
/// <param name="Next">The cursor of the next page, or null once the scan ended.</param>
public sealed record ReplicaCatalogPage(IReadOnlyList<ReplicaCatalogEntry> Databases, string? Next);

internal sealed record ReplicaRevisionNotice(string OrganizationId, string ProjectId, string DatabaseId, long CommittedAt, string? Revision, string? Base)
{
    public long? CdcPosition { get; init; }
}

internal static class ReplicaSubjects
{
    public static string Revision(string replica, string home, string database) => $"terrabase.revision.{replica}.{home}.{database}";

    public static string Revisions(string replica) => $"terrabase.revision.{replica}.>";
}

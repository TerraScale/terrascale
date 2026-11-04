namespace TerraBase.Features.Replicas;

/// <summary>A database homed on the answering node whose placement names the asking region as a replica.</summary>
/// <param name="OrganizationId">Its organization, a ULID.</param>
/// <param name="ProjectId">Its project, a ULID.</param>
/// <param name="DatabaseId">The database, a ULID.</param>
/// <param name="Name">Its name in the project.</param>
/// <param name="HomeRegion">The region that serves its writes.</param>
/// <param name="ReplicaRegions">The regions that hold a replica.</param>
/// <param name="PlacementVersion">Raised by the home on every placement change.</param>
public sealed record ReplicaCatalogEntry(
    string OrganizationId, string ProjectId, string DatabaseId, string Name, string HomeRegion, IReadOnlyList<string> ReplicaRegions, long PlacementVersion);

/// <summary>One page of <see cref="ReplicaCatalogEntry"/> from one catalog snapshot.</summary>
/// <param name="Databases">At most 100 databases.</param>
/// <param name="Next">The cursor of the next page, or null once the scan ended.</param>
public sealed record ReplicaCatalogPage(IReadOnlyList<ReplicaCatalogEntry> Databases, string? Next);

/// <summary>A home's NATS notice that a database has a newer revision.</summary>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="ProjectId">Its project.</param>
/// <param name="DatabaseId">The database.</param>
/// <param name="CommittedAt">When the home committed it, Unix milliseconds on the home's clock.</param>
public sealed record ReplicaRevisionNotice(string OrganizationId, string ProjectId, string DatabaseId, long CommittedAt);

/// <summary>NATS subjects of region replication.</summary>
internal static class ReplicaSubjects
{
    /// <summary>A database's newer revision, published by its home: <c>terrabase.revision.&lt;home&gt;.&lt;database&gt;</c>.</summary>
    public static string Revision(string home, string database) => $"terrabase.revision.{home}.{database}";

    /// <summary>Every revision notice.</summary>
    public const string Revisions = "terrabase.revision.>";
}

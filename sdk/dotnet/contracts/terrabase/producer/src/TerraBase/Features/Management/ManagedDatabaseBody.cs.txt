namespace TerraBase.Features.Management;

/// <summary>A managed database and the region of the node serving it; region is not persisted in the catalog.</summary>
/// <param name="Id">Database id.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Database name used by the document API.</param>
/// <param name="BranchedFrom">Source database id, if branched.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Region">The region that answered.</param>
/// <param name="HomeRegion">The region the database is created and managed in.</param>
/// <param name="ReplicaRegions">The other regions that hold a full copy and serve it too; empty when <paramref name="AllRegions"/>.</param>
/// <param name="AllRegions">Every serving region holds a full copy, including regions that join later.</param>
public sealed record ManagedDatabaseBody(
    string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt, string Region, string HomeRegion, IReadOnlyList<string> ReplicaRegions,
    bool AllRegions)
{
    /// <summary>Adds the serving node's region to a catalog database.</summary>
    public static ManagedDatabaseBody From(ManagedDatabase database, string region)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new(database.Id, database.ProjectId, database.Name, database.BranchedFrom, database.CreatedAt, region, database.Home(region), database.Replicas, database.AllRegions);
    }
}

namespace TerraBase.Features.Management;

public sealed record ManagedDatabaseBody(
    string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt, string Region, string HomeRegion, IReadOnlyList<string> ReplicaRegions,
    bool AllRegions, string? PreviousName, string? ConflictOf, DateTimeOffset? ExpiresAt = null)
{
    public static ManagedDatabaseBody From(ManagedDatabase database, string region)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new(database.Id, database.ProjectId, database.Name, database.BranchedFrom, database.CreatedAt, region, database.Home(region), database.Replicas, database.AllRegions,
            database.PreviousName, database.ConflictOf, database.ExpiresAt);
    }
}

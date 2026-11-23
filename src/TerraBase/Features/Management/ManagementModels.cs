using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Features.Catalog;

namespace TerraBase.Features.Management;

public sealed record OrganizationBody(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, string Plan)
{
    public static OrganizationBody From(OrganizationRecord organization) =>
        new(organization.Id.ToString(), organization.Name, organization.CreatedBy, organization.CreatedAt, organization.Plan);
}

public sealed record ProjectBody(string Id, string OrganizationId, string Name, DateTimeOffset CreatedAt, string? Plan)
{
    public static ProjectBody From(ProjectRecord project) => new(project.Id.ToString(), project.OrganizationId.ToString(), project.Name, project.CreatedAt, project.Plan);
}

public sealed record DatabaseBody(
    string Id,
    string ProjectId,
    string Name,
    string? BranchedFrom,
    DateTimeOffset CreatedAt,
    DateTimeOffset CreatedAtUtc,
    string Region,
    string HomeRegion,
    IReadOnlyList<string> ReplicaRegions,
    IReadOnlyList<string> Regions,
    bool AllRegions,
    long PlacementVersion,
    string DefaultConsistency,
    bool Strict,
    string State,
    string? PreviousName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ExpiresAt,
    long? TtlSeconds)
{
    public static DatabaseBody From(DatabaseRecord database, string region, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(database);
        IReadOnlyList<string> regions = [.. database.Regions.Order(StringComparer.Ordinal)];
        var home = regions.Contains(region, StringComparer.Ordinal) || regions.Count == 0 ? region : regions[0];
        return new(database.Id.ToString(), database.ProjectId.ToString(), database.Name, database.BranchedFrom?.ToString(), database.CreatedAt, database.CreatedAt, region,
            home, [.. regions.Where(item => item != home)], regions, database.AllRegions, database.Version, ConsistencyHeaders.Format(database.DefaultConsistency),
            database.Strict, CatalogStore.State(database.State), database.PreviousName, database.ExpiresAt,
            database.ExpiresAt is { } expires ? Math.Max(0, (long)(expires - now).TotalSeconds) : null);
    }
}

public sealed record PageBody<T>(IReadOnlyList<T> Items, string? Next);

public sealed record DatabasesBody(IReadOnlyList<DatabaseBody> Items, IReadOnlyList<DatabaseBody> Databases, string? Next);

public sealed record NameInput(string? Organization, string? Project, string? Name);

public sealed record PlanInput(string? Organization, string? Project, string? Plan);

public sealed record EnrollmentInput(string? Project, string? Organization);

public sealed record PageInput(string? Organization, string? Project, string? After, int? Limit);

public sealed record RouteInput(string? Organization, string? Project, string? Database, string? ExpectedId);

public sealed record DatabaseInput(
    string? Project,
    string? Database,
    string? ExpectedId,
    string? Name,
    string? HomeRegion,
    IReadOnlyList<string>? ReplicaRegions,
    bool? AllRegions,
    string? DefaultConsistency,
    bool? Strict,
    long? TtlSeconds,
    string? Region,
    JsonElement? Target)
{
    public IReadOnlyList<string>? Regions
    {
        get;
        init
        {
            field = value;
            HasRegions = true;
        }
    }

    [JsonIgnore]
    public bool HasRegions { get; private init; }
}

public sealed record RegionReplicationBody(string Region, string AppliedBookmark, long LagMs, bool Healthy);

public sealed record DatabaseReplicationBody(string DatabaseId, DateTimeOffset ObservedAtUtc, IReadOnlyList<RegionReplicationBody> Regions);

public sealed record CountBody(long Count);

public sealed record ProjectClosureInput(string? Project, int? Version, string? ClosureId, string? AuthorityOrganizationId);

public sealed record ProjectClosureBody(string Status, string ClosureId, int PendingHolders, int EvidenceVersion);

public sealed record PlatformCloseProjectBody(string OrganizationId);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OrganizationBody))]
[JsonSerializable(typeof(ProjectBody))]
[JsonSerializable(typeof(DatabaseBody))]
[JsonSerializable(typeof(PageBody<OrganizationBody>))]
[JsonSerializable(typeof(PageBody<ProjectBody>))]
[JsonSerializable(typeof(DatabasesBody))]
[JsonSerializable(typeof(NameInput))]
[JsonSerializable(typeof(PlanInput))]
[JsonSerializable(typeof(EnrollmentInput))]
[JsonSerializable(typeof(PageInput))]
[JsonSerializable(typeof(RouteInput))]
[JsonSerializable(typeof(DatabaseInput))]
[JsonSerializable(typeof(DatabaseReplicationBody))]
[JsonSerializable(typeof(CountBody))]
[JsonSerializable(typeof(AdminInput))]
[JsonSerializable(typeof(ProjectClosureInput))]
[JsonSerializable(typeof(ProjectClosureBody))]
[JsonSerializable(typeof(PlatformCloseProjectBody))]
internal sealed partial class ManagementJsonContext : JsonSerializerContext;

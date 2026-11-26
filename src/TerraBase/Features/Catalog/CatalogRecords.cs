using System.Diagnostics.CodeAnalysis;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.Catalog;

public sealed record OrganizationRecord(Ulid Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, string Plan, bool Suspended);

public sealed record ProjectRecord(Ulid Id, Ulid OrganizationId, string Name, DateTimeOffset CreatedAt, string? Plan);

public sealed record DatabaseRecord(
    Ulid Id,
    Ulid ProjectId,
    string Name,
    IReadOnlyList<string> Regions,
    bool AllRegions,
    ConsistencyMode DefaultConsistency,
    bool Strict,
    ControlDatabaseState State,
    string KeyId,
    Ulid? BranchedFrom,
    string? PreviousName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    long Version,
    long StorageLimit)
{
    public bool PlacedIn(string region) => AllRegions || Regions.Contains(region, StringComparer.Ordinal);
}

public sealed record CatalogPage<T>(IReadOnlyList<T> Items, string? Next);

public static class Plans
{
    public const string Hobby = "hobby";

    public const string TerraScale = PlatformIdentity.PlanKey;

    public static IReadOnlyList<string> All { get; } = [Hobby, "starter", "pro", "team", "enterprise"];

    public static bool IsPlan([NotNullWhen(true)] string? plan) => plan is not null && All.Contains(plan, StringComparer.Ordinal);

    public static int DatabaseLimit(string plan) => plan switch
    {
        "hobby" => 1,
        "starter" => 5,
        "pro" => 25,
        "team" => 100,
        TerraScale => int.MaxValue,
        _ => 1000,
    };
}

public static class DatabaseNames
{
    public static bool IsValid([NotNullWhen(true)] string? name) => name is { Length: >= 1 and <= 63 } && name[0] is >= 'a' and <= 'z'
        && name.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
}

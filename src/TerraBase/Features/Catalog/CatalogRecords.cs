using System.Diagnostics.CodeAnalysis;
using TerraBase.Features.Qos;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.Catalog;

public sealed record OrganizationRecord(Ulid Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, string Plan, bool Suspended)
{
    private Tenant? _tenant;

    internal Tenant AsTenant()
    {
        var cached = Volatile.Read(ref _tenant);
        if (cached is not null && cached.OrganizationId == Id && string.Equals(cached.Plan, Plan, StringComparison.Ordinal))
        {
            return cached;
        }

        var created = new Tenant(Id, Plan);
        if (cached is null)
        {
            return Interlocked.CompareExchange(ref _tenant, created, null) ?? created;
        }

        Volatile.Write(ref _tenant, created);
        return created;
    }

    public bool Equals(OrganizationRecord? other) =>
        other is not null
        && Id == other.Id
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(CreatedBy, other.CreatedBy, StringComparison.Ordinal)
        && CreatedAt == other.CreatedAt
        && string.Equals(Plan, other.Plan, StringComparison.Ordinal)
        && Suspended == other.Suspended;

    public override int GetHashCode() => HashCode.Combine(Id, Name, CreatedBy, CreatedAt, Plan, Suspended);
}

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

    public static bool IsPlan([NotNullWhen(true)] string? plan) =>
        plan is Hobby or "starter" or "pro" or "team" or "enterprise";

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
    public static bool IsValid([NotNullWhen(true)] string? name)
    {
        if (name is not { Length: >= 1 and <= 63 } || name[0] is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        for (var index = 1; index < name.Length; index++)
        {
            if (name[index] is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

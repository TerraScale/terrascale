using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>An organization's plan. Stored only; TerraBase enforces nothing from it yet.</summary>
[JsonConverter(typeof(TerraBaseStringEnumConverter<TerraBasePlan>))]
public enum TerraBasePlan
{
    /// <summary>The default plan.</summary>
    [JsonStringEnumMemberName("hobby")]
    Hobby,

    /// <summary>Paid plan.</summary>
    [JsonStringEnumMemberName("pro")]
    Pro,

    /// <summary>Contracted plan.</summary>
    [JsonStringEnumMemberName("enterprise")]
    Enterprise,
}

internal sealed class TerraBaseStringEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, Enum
{
    public TerraBaseStringEnumConverter() : base(allowIntegerValues: false) { }
}

/// <summary>One page of a Management API list.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items of this page.</param>
/// <param name="Next">Pass as <c>after</c> to read the next page; <see langword="null"/> on the last page.</param>
public sealed record TerraBasePage<T>(IReadOnlyList<T> Items, string? Next) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Items);
}

/// <summary>An organization: the tenant, which owns billing and projects.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
/// <param name="Name">Display name.</param>
/// <param name="CreatedBy">ts-auth user id of the creator.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Plan">The organization's plan.</param>
public sealed record TerraBaseOrganization(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, TerraBasePlan Plan);

/// <summary>A project: a folder of databases inside an organization.</summary>
/// <param name="Id">Canonical uppercase ULID; the <c>{project}</c> route segment.</param>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="Name">Display name, unique within the organization.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record TerraBaseProject(string Id, string OrganizationId, string Name, DateTimeOffset CreatedAt);

/// <summary>Where a project closure stands in TerraBase.</summary>
/// <param name="Status"><c>accepted</c> once every catalog holder confirmed the project empty under the fence; otherwise <c>pending</c>.</param>
/// <param name="ClosureId">The closure id the fence was written with.</param>
/// <param name="PendingHolders">TerraBase machines that have not confirmed the project empty under the fence.</param>
/// <param name="EvidenceVersion">Closure evidence version; absent legacy responses default to 0. Destructive consumers must require version 2 explicitly.</param>
public sealed record TerraBaseProjectClosure(string Status, string ClosureId, int PendingHolders, int EvidenceVersion = 0)
{
    /// <summary>Whether the returned status is accepted. Does not validate <see cref="EvidenceVersion"/>.</summary>
    public bool Accepted => this.Status == "accepted";
}

/// <summary>A database registered in a project, addressed by <see cref="ProjectId"/> and <see cref="Name"/>.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Database name, unique within the project.</param>
/// <param name="BranchedFrom">Id of the database this one was branched from, if any.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record TerraBaseManagedDatabase(string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt)
{
    /// <summary>The expiry instant, or null without a TTL. Expiry deletes the database, backups and data keys.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The region that answered.</summary>
    [JsonRequired]
    public string Region { get; init; } = null!;

    /// <summary>Every region that holds the database, sorted by region id. The order has no meaning.</summary>
    public IReadOnlyList<string> Regions { get; init; } = [];

    /// <summary>Placement epoch; changes when membership or the writer changes, unchanged for an identical region set.</summary>
    public long PlacementVersion { get; init; }

    /// <summary>Whether every serving region, including regions that join later, holds the database.</summary>
    public bool AllRegions { get; init; }

    /// <summary>The name lost during a cross-region name conflict, or null when no conflict is reported.</summary>
    public string? PreviousName { get; init; }

    /// <summary>The immutable database id that kept <see cref="PreviousName"/>, or null.</summary>
    public string? ConflictOf { get; init; }
}

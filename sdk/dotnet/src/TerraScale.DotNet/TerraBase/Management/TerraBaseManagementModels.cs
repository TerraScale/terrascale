using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

[JsonConverter(typeof(TerraBaseStringEnumConverter<TerraBasePlan>))]
public enum TerraBasePlan
{
    [JsonStringEnumMemberName("hobby")]
    Hobby,

    [JsonStringEnumMemberName("pro")]
    Pro,

    [JsonStringEnumMemberName("enterprise")]
    Enterprise,
}

internal sealed class TerraBaseStringEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, Enum
{
    public TerraBaseStringEnumConverter() : base(allowIntegerValues: false) { }
}

public sealed record TerraBasePage<T>(IReadOnlyList<T> Items, string? Next = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Items);
}

public sealed record TerraBaseOrganization(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, TerraBasePlan Plan);

public sealed record TerraBaseProject(string Id, string OrganizationId, string Name, DateTimeOffset CreatedAt);

public sealed record TerraBaseProjectClosure(string Status, string ClosureId, int PendingHolders, int EvidenceVersion = 0)
{
    public bool Accepted => Status == "accepted";
}

public sealed record TerraBaseManagedDatabase(string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt)
{
    [JsonConstructor]
    internal TerraBaseManagedDatabase(string Id, string ProjectId, string Name, DateTimeOffset CreatedAt, string? BranchedFrom = null)
        : this(Id, ProjectId, Name, BranchedFrom, CreatedAt)
    {
    }

    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonRequired]
    public string Region
    {
        get => field ?? throw new InvalidOperationException("The database's serving region is missing.");
        init => field = value;
    }

    public IReadOnlyList<string> Regions { get; init; } = [];

    public long PlacementVersion { get; init; }

    public bool AllRegions { get; init; }

    public string? PreviousName { get; init; }

    public string? ConflictOf { get; init; }

    public TerraBaseConsistencyMode? DefaultConsistency { get; init; }

    public bool Strict { get; init; }
}

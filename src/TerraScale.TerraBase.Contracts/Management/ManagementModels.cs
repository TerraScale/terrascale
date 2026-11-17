using System.Text.Json;
using MessagePack;

namespace TerraScale.TerraBase.Contracts.Management
{
    /// <summary>A database identity and its current placement.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseManagedDatabase(string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt, string Region = "", string HomeRegion = "", IReadOnlyList<string>? ReplicaRegions = null, bool AllRegions = false, string? PreviousName = null, string? ConflictOf = null, DateTimeOffset? ExpiresAt = null, IReadOnlyList<string>? Regions = null);

    /// <summary>One page of databases in creation order.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDatabasePage(IReadOnlyList<TerraBaseManagedDatabase> Items, string? Next);

    /// <summary>A database create, rename or placement request.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDatabasePlacement(string? Name = null, string? HomeRegion = null, IReadOnlyList<string>? ReplicaRegions = null, bool? AllRegions = null, long? TtlSeconds = null, IReadOnlyList<string>? Regions = null);

    /// <summary><c>PUT /v1/projects/{project}/databases/{database}/ttl</c> request.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDatabaseTtl(long TtlSeconds);

    /// <summary>A move of a database's home to one of its replica regions.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseMoveHome(string HomeRegion);

    /// <summary>One problem a rules check found, named by table and field.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRulesProblem(string Table, string Field, string Code, string Message);

    /// <summary>A database's rules at one version, with the problems and warnings the checks found.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRules(string Version, JsonElement Tables, IReadOnlyList<TerraBaseRulesProblem>? Problems, IReadOnlyList<TerraBaseRulesProblem>? Warnings, DateTimeOffset UpdatedAt, string UpdatedBy);

    /// <summary>A rules deploy or restore: the new rules, or the problems that refused them.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRulesDeploy(TerraBaseRules? Rules, IReadOnlyList<TerraBaseRulesProblem> Problems);

    /// <summary>One kept rules version.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRulesVersion(string Version, DateTimeOffset UpdatedAt, string UpdatedBy, string Digest);

    /// <summary>One page of rules versions, newest first.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRulesVersionPage(IReadOnlyList<TerraBaseRulesVersion> Items, string? Next);

    /// <summary>One condition the simulator evaluated.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRulesTestValue(string Rule, bool Value);

    /// <summary>What the rules decide for one simulated request.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRulesTestResult(bool Allowed, string DecidedBy, string? Clause, IReadOnlyList<TerraBaseRulesTestValue> Results, IReadOnlyList<string> Columns, IReadOnlyList<TerraBaseRulesProblem> Problems);
}

using System.Text.Json.Serialization;
using TerraBase.Errors;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;

namespace TerraBase.Features.Rules;

internal sealed record RulesSet(IReadOnlyDictionary<string, RulesTable> Tables);

internal sealed record RulesTable(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Read = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Create = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Update = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Delete = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Columns = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Writable = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Validate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RulesValues? Set = null);

internal sealed record RulesValues(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Create = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Update = null);

internal sealed record RulesProblem(string Table, string Field, string Code, string Message);

internal sealed record StoredRules(string Version, RulesSet Rules, DateTimeOffset UpdatedAt, string UpdatedBy, string Digest);

internal sealed record RulesVersionItem(string Version, DateTimeOffset UpdatedAt, string UpdatedBy, string Digest);

internal sealed record RulesVersionPage(IReadOnlyList<RulesVersionItem> Items, string? Next);

internal sealed record RulesTableSchema(ShapeTableStatus Status, IReadOnlyList<string> Columns, string? RowId = null);

internal sealed record RulesBody(
    string Version,
    IReadOnlyDictionary<string, RulesTable> Tables,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RulesProblem>? Problems,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RulesProblem>? Warnings,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

internal sealed record RulesVersionsBody(IReadOnlyList<RulesVersionItem> Items, string? Next);

internal sealed record RulesErrorBody(RulesErrorDetail Error);

internal sealed record RulesErrorDetail(string Code, string Message, RulesErrorDetails Details);

internal sealed record RulesErrorDetails(IReadOnlyList<RulesProblem> Problems);

internal sealed record CurrentRules(StoredRules Rules, IReadOnlyList<RulesProblem> Problems);

internal sealed record DeployedRules(StoredRules Rules, IReadOnlyList<RulesProblem> Warnings);

internal sealed record InvalidRules(IReadOnlyList<RulesProblem> Problems);

internal sealed record StaleRules(string? Current);

internal abstract record RulesPrecondition;

internal sealed record RulesIfMatch(string Version) : RulesPrecondition;

internal sealed record RulesIfNone : RulesPrecondition;

internal abstract record RulesChange;

internal sealed record RulesDeploy(RulesSet Rules) : RulesChange;

internal sealed record RulesRestore(string Version) : RulesChange;

internal readonly union RulesCurrentResult(CurrentRules, NotFound, Unavailable);

internal readonly union RulesVersionResult(StoredRules, NotFound, Unavailable);

internal readonly union RulesVersionsResult(RulesVersionPage, NotFound, Unavailable);

internal readonly union RulesDeployResult(DeployedRules, InvalidRules, StaleRules, NotFound, Unavailable);

internal interface IRulesStorage
{
    ValueTask<StoredRules?> ReadRulesAsync(string? version, CancellationToken cancellationToken);

    ValueTask<RulesVersionPage?> ListRulesVersionsAsync(string? after, int limit, CancellationToken cancellationToken);

    ValueTask<RulesTestBody> TestRulesAsync(RulesTestRequest request, RulesSet rules, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RulesProblem>> CheckRulesAsync(RulesSet rules, CancellationToken cancellationToken);
}

internal interface IRulesWriter : IRulesStorage
{
    ValueTask<RulesTableSchema> ReadRulesTableAsync(string table, CancellationToken cancellationToken);

    ValueTask WriteRulesAsync(StoredRules rules, CancellationToken cancellationToken);
}

internal interface IRulesSchema
{
    RulesTableSchema RulesTable(string table);

    bool Runs(RulesTableSchema table, CompiledCondition condition);
}

internal readonly union RulesTestResult(RulesTestBody, NotFound, Unavailable);

internal interface IRules
{
    ValueTask<RulesTestResult> TestAsync(TenantContext database, RulesTestRequest request, CancellationToken cancellationToken);

    ValueTask<RulesCurrentResult> GetAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<RulesVersionsResult> VersionsAsync(TenantContext database, string? after, int limit, CancellationToken cancellationToken);

    ValueTask<RulesVersionResult> VersionAsync(TenantContext database, string version, CancellationToken cancellationToken);

    ValueTask<RulesDeployResult> DeployAsync(TenantContext database, RulesChange change, RulesPrecondition precondition, string actor,
        CancellationToken cancellationToken);
}

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseRulesSet(IReadOnlyDictionary<string, TerraBaseRulesTable> Tables);

public sealed record TerraBaseRulesTable(string? Read = null, string? Create = null, string? Update = null,
    string? Delete = null, IReadOnlyList<string>? Columns = null, IReadOnlyList<string>? Writable = null,
    IReadOnlyList<string>? Validate = null, TerraBaseRulesValues? Set = null);

public sealed record TerraBaseRulesValues(IReadOnlyDictionary<string, string>? Create = null,
    IReadOnlyDictionary<string, string>? Update = null);

public sealed record TerraBaseRulesProblem(string Table, string Field, string Code, string Message);

public sealed record TerraBaseRules(string Version, IReadOnlyDictionary<string, TerraBaseRulesTable> Tables,
    DateTimeOffset UpdatedAt, string UpdatedBy, IReadOnlyList<TerraBaseRulesProblem>? Problems = null,
    IReadOnlyList<TerraBaseRulesProblem>? Warnings = null);

public sealed record TerraBaseRulesVersion(string Version, DateTimeOffset UpdatedAt, string UpdatedBy, string Digest);

public sealed record TerraBaseRulesTestAuth(string Uid, IReadOnlyDictionary<string, JsonElement>? Claims = null,
    [property: JsonPropertyName("org_id")] string? OrgId = null,
    [property: JsonPropertyName("org_role")] string? OrgRole = null,
    [property: JsonPropertyName("is_anonymous")] bool IsAnonymous = false);

public sealed record TerraBaseRulesTestRequest(string Operation, string Table, TerraBaseRulesTestAuth Auth,
    IReadOnlyDictionary<string, JsonElement>? Row = null, IReadOnlyDictionary<string, JsonElement>? New = null,
    long? Time = null, TerraBaseRulesSet? Rules = null);

public sealed record TerraBaseRulesTestValue(string Rule, bool Value);

public sealed record TerraBaseRulesTestResult(bool Allowed, string DecidedBy, string? Clause,
    IReadOnlyList<TerraBaseRulesTestValue> Results, IReadOnlyList<string> Columns, IReadOnlyList<TerraBaseRulesProblem> Problems)
{
    [JsonConstructor]
    internal TerraBaseRulesTestResult(bool Allowed, string DecidedBy, IReadOnlyList<TerraBaseRulesTestValue> Results,
        IReadOnlyList<string> Columns, IReadOnlyList<TerraBaseRulesProblem> Problems, string? Clause = null)
        : this(Allowed, DecidedBy, Clause, Results, Columns, Problems)
    {
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TerraBaseRulesSet))]
[JsonSerializable(typeof(TerraBaseRules))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseRulesVersion>))]
[JsonSerializable(typeof(TerraBaseRulesTestRequest))]
[JsonSerializable(typeof(TerraBaseRulesTestResult))]
[JsonSerializable(typeof(IReadOnlyList<TerraBaseRulesProblem>))]
internal sealed partial class TerraBaseRulesJson : JsonSerializerContext;

public sealed partial class TerraBaseDatabase
{
    public async Task<TerraBaseRules> GetRulesAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/rules", null, TerraBaseRulesJson.Default.TerraBaseRules,
            null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseRules> DeployRulesAsync(TerraBaseRulesSet rules, string? currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var headers = RulesPrecondition(currentVersion);
        return (await connection.SendAsync(HttpMethod.Put, path + "/rules",
            TerraBaseConnection.Json(rules, TerraBaseRulesJson.Default.TerraBaseRulesSet), TerraBaseRulesJson.Default.TerraBaseRules,
            headers, cancellationToken).ConfigureAwait(false)).Value;
    }

    public async Task<TerraBasePage<TerraBaseRulesVersion>> ListRulesVersionsAsync(string? after = null, int? limit = null,
        CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/rules/versions" + TerraBaseConnection.Query(
            ("after", after), ("limit", TerraBaseConnection.Number(limit))), null,
            TerraBaseRulesJson.Default.TerraBasePageTerraBaseRulesVersion, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseRules> GetRulesVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(version);
        return (await connection.SendAsync(HttpMethod.Get, path + "/rules/versions/" + version, null,
            TerraBaseRulesJson.Default.TerraBaseRules, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public async Task<TerraBaseRules> RestoreRulesAsync(string version, string? currentVersion,
        CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(version);
        var headers = RulesPrecondition(currentVersion);
        return (await connection.SendAsync(HttpMethod.Post, path + "/rules/versions/" + version + "/restore", null,
            TerraBaseRulesJson.Default.TerraBaseRules, headers, cancellationToken).ConfigureAwait(false)).Value;
    }

    public async Task<TerraBaseRulesTestResult> TestRulesAsync(TerraBaseRulesTestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await connection.SendAsync(HttpMethod.Post, path + "/rules:test",
            TerraBaseConnection.Json(request, TerraBaseRulesJson.Default.TerraBaseRulesTestRequest),
            TerraBaseRulesJson.Default.TerraBaseRulesTestResult, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    private static Action<HttpRequestHeaders> RulesPrecondition(string? version)
    {
        if (version is null)
            return headers => headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        TerraScaleIds.Validate(version);
        return headers => headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
    }
}
